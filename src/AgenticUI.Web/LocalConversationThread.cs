using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;

namespace AgenticUI.Web;

/// <summary>Lets UIAgent send new messages only while the local MAF session owns history.</summary>
public sealed class LocalConversationThread : IConversationThread
{
    private readonly List<ChatResponseUpdate> _committed = [];
    private readonly List<ChatResponseUpdate> _pending = [];

    public string ThreadId { get; } = Guid.NewGuid().ToString("N");
    public bool IsStateful => true;
    public string? ConversationId { get; private set; }

    public void AppendUserMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_pending.Count != 0)
        {
            throw new InvalidOperationException("The previous conversation turn has not been committed.");
        }

        _pending.Add(new ChatResponseUpdate(message.Role, [.. message.Contents]));
    }

    public void AppendUpdate(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ConversationId is { } id)
        {
            if (ConversationId is not null && ConversationId != id)
            {
                throw new InvalidOperationException("A local conversation cannot change its session ID.");
            }

            ConversationId = id;
        }

        _pending.Add(update);
    }

    public void CompleteTurn()
    {
        _committed.AddRange(_pending);
        _pending.Clear();
    }

    public IReadOnlyList<ChatResponseUpdate> GetUpdates() => _committed.AsReadOnly();
}
