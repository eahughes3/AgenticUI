using System.Diagnostics;
using System.Text.Json;
using AgenticUI.AgentServer;
using AgenticUI.Web;
using AgenticUI.Web.Components.Pages.Scenarios;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgenticUI.Tests;

public sealed class BackendMcpTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1000", 1000)]
    [InlineData("{\"text\":\"0\"}", 0)]
    [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"1000\"}]}", 1000)]
    [InlineData("[{\"text\":\"42\"}]", 42)]
    [InlineData("{\"isError\":true,\"content\":[{\"text\":\"42\"}]}", null)]
    [InlineData("{\"content\":[{\"text\":\"invalid\"}]}", null)]
    [InlineData("-1", null)]
    [InlineData("1001", null)]
    [InlineData("null", null)]
    public void CardReadsTheReturnedNumberWithoutFallback(string json, int? expected)
    {
        var block = new RandomNumberToolBlock { McpResult = JsonSerializer.Deserialize<JsonElement>(json) };
        Assert.Equal(expected, block.Number);
        Assert.Null(new RandomNumberToolBlock().Number);
    }

    [Fact]
    public void DuplicateToolNamesAreRejected()
    {
        var model = new ScriptedChatClient((_, _, _) => ScriptedChatClient.Stream(new TextContent("done")));
        var catalog = new AgentCatalog(model, model);
        Assert.Throws<InvalidOperationException>(() => catalog.CreateBackendToolRendering(
            [AIFunctionFactory.Create(() => 0, "get_weather")]));
    }

    [Fact]
    public void CardAcceptsNativeMcpTextContent()
    {
        Assert.Equal(0, new RandomNumberToolBlock { McpResult = new TextContent("0") }.Number);
        Assert.Equal(1000, new RandomNumberToolBlock { McpResult = new TextContent("1000") }.Number);
        Assert.Null(new RandomNumberToolBlock { McpResult = new TextContent("invalid") }.Number);
    }

    [Fact]
    public async Task CardRendersPendingState()
    {
        var html = await RenderCardAsync(new RandomNumberToolBlock());
        Assert.Contains("Generating number...", html);
        Assert.DoesNotContain("random-number-card__number", html);
    }

    [Theory]
    [InlineData("{\"content\":[{\"text\":\"0\"}]}", "0")]
    [InlineData("{\"content\":[{\"text\":\"1000\"}]}", "1000")]
    [InlineData("{\"isError\":true,\"content\":[{\"text\":\"42\"}]}", null)]
    [InlineData("{\"content\":[{\"text\":\"not a number\"}]}", null)]
    public async Task GeneratedBlockRendersBoundaryAndErrorResults(string json, string? expected)
    {
        var model = new ScriptedChatClient((messages, _, _) =>
            messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any()
                ? ScriptedChatClient.Stream(new TextContent("done"))
                : ScriptedChatClient.Stream(new FunctionCallContent("random-1", "get_random_number",
                    new Dictionary<string, object?>())));
        using var chat = new LocalAgentConnection(new AgentCatalog(model, model)).CreateBackendToolsChatClient(
            [AIFunctionFactory.Create(() => JsonSerializer.Deserialize<JsonElement>(json), "get_random_number")]);
        using var agent = new UIAgent(chat, RegisterGeneratedBlocks);
        RandomNumberToolBlock? card = null;
        await foreach (var block in agent.SendMessageAsync(new ChatMessage(ChatRole.User, "random")))
        {
            if (block is RandomNumberToolBlock random)
            {
                card = random;
            }
        }
        Assert.NotNull(card);
        Assert.True(card.HasResult);
        var html = await RenderCardAsync(card);
        if (expected is null)
        {
            Assert.Contains("role=\"alert\"", html);
            Assert.DoesNotContain("random-number-card__number", html);
        }
        else
        {
            Assert.Contains($"class=\"random-number-card__number\">{expected}</div>", html);
        }
    }

    [Fact]
    public async Task StdioPreservesTheNoArgumentRandomTool()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "RandomNumberTest",
            Command = "dotnet",
            Arguments = ["run", "--no-build", "--project", ServerProject, "--no-launch-profile"],
            EnvironmentVariables = new Dictionary<string, string?> { ["Mcp__Transport"] = "stdio" }
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: timeout.Token));
        AssertToolContract(tool);
        var result = await tool.CallAsync(cancellationToken: timeout.Token);
        AssertRandomResult(result);
    }

    [Fact]
    public async Task HttpDiscoveryAndNativeInvocationPopulateRandomCardAlongsideWeather()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var process = StartHttpServer();
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var endpoint = await ReadEndpointAsync(process, timeout.Token);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:Endpoint"] = "http://mcpserver/mcp",
                ["services:mcpserver:http:0"] = endpoint
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddServiceDiscovery();
            services.AddHttpClient("backend-mcp").AddServiceDiscovery();
            await using var provider = services.BuildServiceProvider();
            var connection = new BackendMcpConnection(provider.GetRequiredService<IHttpClientFactory>(),
                configuration, provider.GetRequiredService<ILoggerFactory>());
            configuration["Mcp:Endpoint"] = "http://127.0.0.1:1/mcp";
            await Assert.ThrowsAsync<HttpRequestException>(() => connection.ConnectAsync(timeout.Token));
            configuration["Mcp:Endpoint"] = "http://mcpserver/mcp";
            await using var session = await connection.ConnectAsync(timeout.Token);
            AssertToolContract(Assert.IsType<McpClientTool>(Assert.Single(session.Tools)));

            var model = new ScriptedChatClient((messages, options, _) =>
            {
                Assert.Contains(options!.Tools!, tool => tool.Name == "get_weather");
                Assert.Contains(options.Tools!, tool => tool.Name == "get_random_number");
                return messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any()
                    ? ScriptedChatClient.Stream(new TextContent("done"))
                    : ScriptedChatClient.Stream(new FunctionCallContent("random-1", "get_random_number",
                        new Dictionary<string, object?>()));
            });
            var agents = new LocalAgentConnection(new AgentCatalog(model, model));
            using var chat = agents.CreateBackendToolsChatClient(session.Tools);
            using var agent = new UIAgent(chat, RegisterGeneratedBlocks);
            RandomNumberToolBlock? card = null;
            await foreach (var block in agent.SendMessageAsync(
                new ChatMessage(ChatRole.User, "Generate a random number"), timeout.Token))
            {
                if (block is RandomNumberToolBlock random)
                {
                    card = random;
                }
            }
            Assert.NotNull(card);
            Assert.True(card.HasResult);
            Assert.NotNull(card.Number);
            Assert.InRange(card.Number.Value, 0, 1000);
            Assert.Equal("random-1", card.Result!.CallId);
            Assert.Equal(2, model.Requests.Count);
            Assert.Single(model.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            var html = await RenderCardAsync(card);
            Assert.Contains($"class=\"random-number-card__number\">{card.Number}</div>", html);
            Assert.DoesNotContain("Generating number...", html);

            for (var i = 0; i < 10; i++)
            {
                AssertRandomResult(await ((McpClientTool)session.Tools[0]).CallAsync(cancellationToken: timeout.Token));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            var stderr = await errors;
            Assert.True(process.ExitCode == 0 || string.IsNullOrEmpty(stderr), stderr);
        }
    }

    [Fact]
    public async Task CanceledDiscoveryDoesNotPreventRetry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("backend-mcp");
        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Endpoint"] = "http://127.0.0.1:1/mcp"
        }).Build();
        var connection = new BackendMcpConnection(provider.GetRequiredService<IHttpClientFactory>(),
            configuration, provider.GetRequiredService<ILoggerFactory>());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ConnectAsync(canceled.Token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.ConnectAsync(timeout.Token));
    }

    private static void AssertToolContract(McpClientTool tool)
    {
        Assert.Equal("get_random_number", tool.Name);
        Assert.Empty(tool.JsonSchema.GetProperty("properties").EnumerateObject());
    }

    private static void RegisterGeneratedBlocks(UIAgentOptions options)
    {
        options.Thread = new LocalConversationThread();
        var registrations = Assert.Single(typeof(RandomNumberToolBlock).Assembly.GetTypes(),
            type => type.Name == "GeneratedToolBlockRegistrations");
        var method = registrations.GetMethod("AddGeneratedToolBlocks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, [options]);
    }

    private static async Task<string> RenderCardAsync(RandomNumberToolBlock block)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<RandomNumberCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Block"] = block }));
            return result.ToHtmlString();
        });
    }

    private static void AssertRandomResult(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.True(int.TryParse(text.Text, out var number), text.Text);
        Assert.InRange(number, 0, 1000);
    }

    private static string ServerProject
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticUI.slnx")))
            {
                directory = directory.Parent;
            }
            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
                "AgentiUI.McpServerApp", "AgentiUI.McpServerApp.csproj");
        }
    }

    private static Process StartHttpServer()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "run", "--no-build", "--project", ServerProject,
            "--no-launch-profile", "--", "--Mcp:Transport=http", "--urls=http://127.0.0.1:0" })
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start test MCP server.");
    }

    private static async Task<string> ReadEndpointAsync(Process process, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            const string marker = "Now listening on: ";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                // Drain the pipe after readiness so verbose tool logs cannot block the server.
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
                return line[(index + marker.Length)..].Trim();
            }
        }
        throw new InvalidOperationException("MCP server exited before listening.");
    }
}
