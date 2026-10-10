using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticUI.Tests;

#pragma warning disable MAAI001 // Exercise the installed experimental native agent adapter.
public sealed class NativeContractTests
{
    [Fact]
    public async Task NativeAdapterStreamsExecutedToolsAndResumesApproval()
    {
        var executions = 0;
        var model = new ScriptedChatClient((messages, _, _) =>
            messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()
                ? ScriptedChatClient.Stream(new TextContent("done"))
                : ScriptedChatClient.Stream(new FunctionCallContent("call-1", "count", new Dictionary<string, object?>())));
        var function = AIFunctionFactory.Create(() => ++executions, "count");
        var agent = model.AsAIAgent(tools: [new ApprovalRequiredAIFunction(function)]);
        var session = await agent.CreateSessionAsync();
        using var client = agent.AsIChatClient(session);
        var first = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]).ToListAsync();
        var approval = Assert.Single(first.SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(0, executions);
        var second = await client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, [approval.CreateResponse(true)])],
            new ChatOptions { ConversationId = first[0].ConversationId }).ToListAsync();
        Assert.Equal(1, executions);
        Assert.Contains(second.SelectMany(u => u.Contents), c => c is FunctionResultContent);
        Assert.Contains(second.SelectMany(u => u.Contents), c => c is TextContent { Text: "done" });
    }

    [Fact]
    public async Task NativeAdapterInvokesRunSuppliedFrontendToolOnce()
    {
        var executions = 0;
        var model = new ScriptedChatClient((messages, _, _) =>
            messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()
                ? ScriptedChatClient.Stream(new TextContent("done"))
                : ScriptedChatClient.Stream(new FunctionCallContent("call-1", "local", new Dictionary<string, object?>())));
        var agent = model.AsAIAgent();
        using var client = agent.AsIChatClient(await agent.CreateSessionAsync());
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")],
            new ChatOptions { Tools = [AIFunctionFactory.Create(() => ++executions, "local")] }).ToListAsync();
        Assert.Equal(1, executions);
        Assert.Contains(updates.SelectMany(u => u.Contents), c => c is FunctionCallContent);
        Assert.Contains(updates.SelectMany(u => u.Contents), c => c is FunctionResultContent);
    }
}
