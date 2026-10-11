using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
configuration.AddEnvironmentVariables().AddCommandLine(args);
var transport = configuration["Mcp:Transport"] ?? "stdio";

if (transport.Equals("http", StringComparison.OrdinalIgnoreCase))
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddServiceDefaults();
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<RandomNumberTools>();

    var app = builder.Build();
    app.MapDefaultEndpoints();
    app.MapMcp("/mcp");
    await app.RunAsync();
}
else if (transport.Equals("stdio", StringComparison.OrdinalIgnoreCase))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<RandomNumberTools>();

    await builder.Build().RunAsync();
}
else
{
    throw new InvalidOperationException($"Unsupported MCP transport '{transport}'. Use 'stdio' or 'http'.");
}
