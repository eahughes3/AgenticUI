using AGUI.Abstractions;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace AgenticUI.AgentServer;

internal static class LocalRunOptions
{
    private const string StateKey = "AgenticUI.LocalRunInput";

    public static ChatOptions? Prepare(ChatOptions? options, IChatClient client)
    {
        if (options?.RawRepresentationFactory?.Invoke(client) is not RunAgentInput input)
        {
            return options;
        }

        var local = options.Clone();
        local.RawRepresentationFactory = null;
        local.AdditionalProperties ??= new();
        local.AdditionalProperties[StateKey] = input;
        return local;
    }

    public static bool TryGetRunAgentInput(this ChatOptions options, [NotNullWhen(true)] out RunAgentInput? input)
    {
        input = options.AdditionalProperties?.TryGetValue(StateKey, out var value) == true
            ? value as RunAgentInput
            : null;
        return input is not null;
    }
}
