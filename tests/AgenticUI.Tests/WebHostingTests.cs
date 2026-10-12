using System.Diagnostics;
using System.Net;
using Xunit;

namespace AgenticUI.Tests;

public sealed class WebHostingTests
{
    [Fact]
    public async Task BackendToolsNavigationResolvesProductionAgentCatalogWithoutCrashing()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticUI.slnx")))
        {
            directory = directory.Parent;
        }
        var project = Path.Combine(directory?.FullName ??
            throw new InvalidOperationException("Repository root not found."),
            "src", "AgenticUI.Web", "AgenticUI.Web.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "run", "--no-build", "--project", project,
            "--no-launch-profile", "--", "--urls=http://127.0.0.1:0" })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__chat"] =
            "Endpoint=https://test.openai.azure.com/;Key=test-key;";
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Unable to start test web host.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var errors = process.StandardError.ReadToEndAsync();
        Task<string>? remainingOutput = null;
        try
        {
            string? endpoint = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    endpoint = line[(index + marker.Length)..].Trim();
                    break;
                }
            }
            Assert.NotNull(endpoint);
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            using var http = new HttpClient { BaseAddress = new Uri(endpoint) };

            for (var i = 0; i < 2; i++)
            {
                using var response = await http.GetAsync("/backend-tools", timeout.Token);
                var html = await response.Content.ReadAsStringAsync(timeout.Token);
                Assert.True(response.StatusCode == HttpStatusCode.OK, html);
                Assert.Contains("<h1>Backend tools</h1>", html);
                Assert.Contains("Connecting to backend tools...", html);
                Assert.DoesNotContain("MissingMethodException", html);
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
            var stdout = remainingOutput is null ? "" : await remainingOutput;
            Assert.DoesNotContain("MissingMethodException", stderr + stdout);
        }
    }
}
