using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgenticUI.Web;

public sealed class BackendMcpConnection(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILoggerFactory loggerFactory)
{
    public async Task<BackendMcpSession> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(configuration["Mcp:Endpoint"] ?? "https://mcpserver/mcp",
            UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Mcp:Endpoint must be an absolute HTTP or HTTPS URL.");
        }
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint, Name = "BackendTools" },
            httpClientFactory.CreateClient("backend-mcp"), loggerFactory, ownsHttpClient: true);
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return new BackendMcpSession(client, transport, tools.Cast<AITool>().ToArray());
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
            await transport.DisposeAsync();
            throw;
        }
    }
}

public sealed class BackendMcpSession(
    McpClient client, HttpClientTransport transport, IReadOnlyList<AITool> tools) : IAsyncDisposable
{
    public IReadOnlyList<AITool> Tools { get; } = tools;

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await transport.DisposeAsync();
    }
}
