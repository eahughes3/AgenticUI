using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgenticUI.Tests;

internal sealed class ScriptedChatClient(
    Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> respond) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        lock (Requests)
        {
            Requests.Add(request);
        }
        await foreach (var update in respond(request, options, cancellationToken))
        {
            yield return update;
        }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    public static async IAsyncEnumerable<ChatResponseUpdate> Stream(params AIContent[] contents)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ResponseId = Guid.NewGuid().ToString("N")
        };
    }
}
