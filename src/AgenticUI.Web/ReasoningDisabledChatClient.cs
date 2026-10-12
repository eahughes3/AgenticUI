using Microsoft.Extensions.AI;

namespace AgenticUI.Web;

public sealed class ReasoningDisabledChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Prepare(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Prepare(options), cancellationToken);

    private static ChatOptions Prepare(ChatOptions? options)
    {
        var prepared = options?.Clone() ?? new ChatOptions();
        prepared.Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.None,
            Output = ReasoningOutput.None
        };
        return prepared;
    }
}
