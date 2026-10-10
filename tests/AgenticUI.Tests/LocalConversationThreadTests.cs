using AgenticUI.Web;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticUI.Tests;

public sealed class LocalConversationThreadTests
{
    [Fact]
    public void ThreadCommitsUpdatesAndCapturesNativeSessionId()
    {
        var thread = new LocalConversationThread();
        thread.AppendUserMessage(new ChatMessage(ChatRole.User, "hello"));
        thread.AppendUpdate(new ChatResponseUpdate(ChatRole.Assistant, "hi")
        {
            ConversationId = "session"
        });
        Assert.Empty(thread.GetUpdates());
        thread.CompleteTurn();
        Assert.True(thread.IsStateful);
        Assert.Equal("session", thread.ConversationId);
        Assert.Equal(2, thread.GetUpdates().Count);
        Assert.Equal("hello", thread.GetUpdates()[0].Text);
        Assert.Equal("hi", thread.GetUpdates()[1].Text);
    }

    [Fact]
    public void UncommittedTurnsAndSessionChangesAreRejected()
    {
        var thread = new LocalConversationThread();
        thread.AppendUserMessage(new ChatMessage(ChatRole.User, "hello"));
        Assert.Throws<InvalidOperationException>(() =>
            thread.AppendUserMessage(new ChatMessage(ChatRole.User, "again")));
        thread.AppendUpdate(new ChatResponseUpdate { ConversationId = "first" });
        Assert.Throws<InvalidOperationException>(() =>
            thread.AppendUpdate(new ChatResponseUpdate { ConversationId = "other" }));
    }

    [Fact]
    public void ThreadsHaveIndependentIdentityAndHistory()
    {
        var first = new LocalConversationThread();
        var second = new LocalConversationThread();
        first.AppendUserMessage(new ChatMessage(ChatRole.User, "private"));
        first.CompleteTurn();
        Assert.NotEqual(first.ThreadId, second.ThreadId);
        Assert.Empty(second.GetUpdates());
    }
}
