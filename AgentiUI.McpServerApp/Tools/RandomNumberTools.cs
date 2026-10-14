using ModelContextProtocol.Server;
using System.ComponentModel;

/// <summary>
/// Sample MCP tools for demonstration purposes.
/// These tools can be invoked by MCP clients to perform various operations.
/// </summary>
public class RandomNumberTools
{
    [McpServerTool(Name = "get_random_number")]
    [Description("Generates and returns one random integer from 0 through 1000, inclusive. Takes no arguments.")]
    public int GetRandomNumber() => Random.Shared.Next(0, 1001);
}
