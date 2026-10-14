using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;

namespace AgenticUI.Web.Components.Pages.Scenarios;

[ToolBlock("get_random_number")]
public partial class RandomNumberToolBlock : FunctionInvocationContentBlock
{
    [ToolResult]
    public object? McpResult { get; set; }

    public int? Number => McpResult switch
    {
        TextContent text => ParseNumber(text.Text),
        JsonElement result => ReadNumber(result),
        _ => null
    };

    private static int? ParseNumber(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        number is >= 0 and <= 1000 ? number : null;

    private static int? ReadNumber(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            {
                return null;
            }
            if (result.TryGetProperty("content", out var content))
            {
                return ReadNumber(content);
            }
            if (result.TryGetProperty("text", out var text))
            {
                return ReadNumber(text);
            }
        }
        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() == 1)
        {
            return ReadNumber(result[0]);
        }
        if (result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var number))
        {
            return number is >= 0 and <= 1000 ? number : null;
        }
        if (result.ValueKind == JsonValueKind.String)
        {
            return ParseNumber(result.GetString());
        }
        return null;
    }
}
