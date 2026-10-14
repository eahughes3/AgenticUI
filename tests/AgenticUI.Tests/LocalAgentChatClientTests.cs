using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AgenticUI.AgentServer;
using AgenticUI.Web;
using AgenticUI.Web.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticUI.Tests;

public sealed class LocalAgentChatClientTests
{
    [Fact]
    public async Task StateOnlyUpdatesReachTypedUIAgentDuringRun()
    {
        var call = 0;
        UIAgent<PlanState>? ui = null;
        var model = new ScriptedChatClient((_, _, _) =>
        {
            ++call;
            if (call > 1)
            {
                Assert.Equal("First", Assert.Single(ui!.State.Value.Steps).Description);
            }
            return call == 1
                ? ScriptedChatClient.Stream(new FunctionCallContent("plan", "create_plan",
                    new Dictionary<string, object?> { ["steps"] = JsonSerializer.SerializeToElement(new[] { "First" }) }))
                : ScriptedChatClient.Stream(new TextContent("done"));
        });
        using var client = new LocalAgentChatClient(
            new AgentCatalog(model, model).CreateAgenticGenerativeUI(), AgentScenario.AgenticGenerativeUI);
        using var agent = new UIAgent<PlanState>(client, options =>
        {
            options.Thread = new LocalConversationThread();
            options.StateMapper = context =>
            {
                if (context.Update.RawRepresentation is StateSnapshotEvent snapshot)
                {
                    context.SetState(snapshot.Snapshot.Deserialize<PlanState>()!);
                }
            };
        });
        ui = agent;
        await foreach (var _ in agent.SendMessageAsync(new ChatMessage(ChatRole.User, "plan"))) { }
        Assert.Equal("pending", Assert.Single(agent.State.Value.Steps).Status);
    }

    [Fact]
    public async Task PlanSnapshotsAndDeltasArriveBeforeFinalResponse()
    {
        var call = 0;
        var model = new ScriptedChatClient((_, _, _) => ++call switch
        {
            1 => ScriptedChatClient.Stream(new FunctionCallContent("plan", "create_plan",
                new Dictionary<string, object?> { ["steps"] = JsonSerializer.SerializeToElement(new[] { "First", "Second" }) })),
            2 => ScriptedChatClient.Stream(new FunctionCallContent("step", "update_plan_step",
                new Dictionary<string, object?> { ["index"] = 0, ["status"] = JsonSerializer.SerializeToElement("completed") })),
            _ => ScriptedChatClient.Stream(new TextContent("done"))
        });
        var catalog = new AgentCatalog(model, model);
        using var client = new LocalAgentChatClient(catalog.CreateAgenticGenerativeUI(), AgentScenario.AgenticGenerativeUI);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "plan")]))
        {
            if (update.RawRepresentation is StateSnapshotEvent)
            {
                Assert.Equal(1, call);
            }
            if (update.RawRepresentation is StateDeltaEvent)
            {
                Assert.Equal(2, call);
            }
            updates.Add(update);
        }
        var snapshot = Assert.Single(updates.Select(u => u.RawRepresentation).OfType<StateSnapshotEvent>());
        Assert.Equal("First", snapshot.Snapshot.GetProperty("steps")[0].GetProperty("description").GetString());
        Assert.Equal("pending", snapshot.Snapshot.GetProperty("steps")[0].GetProperty("status").GetString());
        var delta = Assert.Single(updates.Select(u => u.RawRepresentation).OfType<StateDeltaEvent>());
        Assert.Equal("/steps/0/status", delta.Delta[0].GetProperty("path").GetString());
        Assert.Equal("completed", delta.Delta[0].GetProperty("value").GetString());
        Assert.Equal(2, updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>().Count());
    }

    [Fact]
    public async Task RecipeSnapshotHasCompleteTypedStateAndUsesCurrentLocalEdits()
    {
        var model = new ScriptedChatClient((messages, options, _) =>
        {
            Assert.Null(options!.RawRepresentationFactory);
            Assert.Contains(messages, m => m.Text.Contains("user-edited recipe"));
            return messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()
                ? ScriptedChatClient.Stream(new TextContent("done"))
                : ScriptedChatClient.Stream(new FunctionCallContent("recipe", "generate_recipe",
                    new Dictionary<string, object?>
                    {
                        ["recipe"] = JsonSerializer.SerializeToElement(new
                        {
                            title = "Vegan pasta", skill_level = "Beginner", cooking_time = "15 min",
                            special_preferences = new[] { "Vegan" }, ingredients = new[]
                            {
                                new { icon = "pasta", name = "Pasta", amount = "12 oz" }
                            },
                            instructions = new[] { "Cook pasta." }
                        })
                    }));
        });
        using var client = new LocalAgentChatClient(new AgentCatalog(model, model).CreateSharedState(), AgentScenario.SharedState);
        var input = new RunAgentInput
        {
            ThreadId = "recipe-thread",
            State = JsonSerializer.SerializeToElement(new { recipe = new { title = "user-edited recipe" } })
        };
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "make vegan")],
            new ChatOptions { RawRepresentationFactory = _ => input }).ToListAsync();
        var snapshot = Assert.Single(updates.Select(u => u.RawRepresentation).OfType<StateSnapshotEvent>());
        var recipe = snapshot.Snapshot.GetProperty("recipe");
        Assert.Equal("Vegan pasta", recipe.GetProperty("title").GetString());
        Assert.Equal("Vegan", recipe.GetProperty("special_preferences")[0].GetString());
        Assert.Equal("12 oz", recipe.GetProperty("ingredients")[0].GetProperty("amount").GetString());
        var first = model.Requests[0];
        Assert.Contains("current recipe state", first[^2].Text);
        Assert.Equal("make vegan", first[^1].Text);
    }

    [Fact]
    public async Task EachDocumentRunSeesLatestStateImmediatelyBeforeNewUserMessage()
    {
        var model = new ScriptedChatClient((_, _, _) => ScriptedChatClient.Stream(new TextContent("ok")));
        using var client = new LocalAgentChatClient(new AgentCatalog(model, model).CreatePredictiveState(), AgentScenario.PredictiveState);
        ChatOptions Options(string document) => new()
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                ThreadId = "document-thread",
                State = JsonSerializer.SerializeToElement(new { document })
            }
        };
        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")], Options("old"));
        var secondOptions = Options("user edit");
        secondOptions.ConversationId = first.ConversationId;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")], secondOptions);
        var last = model.Requests[^1];
        Assert.Contains("user edit", last[^2].Text);
        Assert.Equal("second", last[^1].Text);
        Assert.Single(last, m => m.Text == "first");
        Assert.Single(last, m => m.Text == "second");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalExecutesOnlyAfterExplicitAcceptance(bool accepted)
    {
        var executions = 0;
        var iteration = 0;
        var model = new ScriptedChatClient((_, _, _) => ++iteration == 1
            ? ScriptedChatClient.Stream(new FunctionCallContent("meeting", "book_meeting", new Dictionary<string, object?>()))
            : ScriptedChatClient.Stream(new TextContent("finished")));
        var agent = model.AsAIAgent(tools: [new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(() => ++executions, "book_meeting"))]);
        using var client = new LocalAgentChatClient(agent, AgentScenario.HumanInTheLoop);
        var first = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "book")]).ToListAsync();
        var approval = Assert.Single(first.SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(0, executions);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, [approval.CreateResponse(accepted)])],
            new ChatOptions { ConversationId = first[0].ConversationId });
        Assert.Equal(accepted ? 1 : 0, executions);
    }

    [Fact]
    public async Task SeparateClientsDoNotShareMessagesOrSessions()
    {
        var model = new ScriptedChatClient((_, _, _) => ScriptedChatClient.Stream(new TextContent("ok")));
        var catalog = new AgentCatalog(model, model);
        using var first = new LocalAgentChatClient(catalog.CreateAgenticChat(), AgentScenario.AgenticChat);
        using var second = new LocalAgentChatClient(catalog.CreateAgenticChat(), AgentScenario.AgenticChat);
        var a = await first.GetResponseAsync([new ChatMessage(ChatRole.User, "private-a")]);
        var b = await second.GetResponseAsync([new ChatMessage(ChatRole.User, "private-b")]);
        Assert.NotEqual(a.ConversationId, b.ConversationId);
        Assert.DoesNotContain(model.Requests[1], m => m.Text.Contains("private-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "wrong-session")], new ChatOptions { ConversationId = a.ConversationId }));
    }

    [Fact]
    public async Task ConcurrentClientsInvokeOnlyTheirOwnPageTools()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ChatResponseUpdate> Respond(IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.SetResult();
            }
            await bothEntered.Task.WaitAsync(token);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("local-call", "local", new Dictionary<string, object?>())]);
        }

        var model = new ScriptedChatClient((messages, _, token) => Respond(messages, token));
        var catalog = new AgentCatalog(model, model);
        using var first = new LocalAgentChatClient(catalog.CreateToolBasedGenerativeUI(), AgentScenario.FrontendTools);
        using var second = new LocalAgentChatClient(catalog.CreateToolBasedGenerativeUI(), AgentScenario.FrontendTools);
        var firstCalls = 0;
        var secondCalls = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            first.GetResponseAsync([new ChatMessage(ChatRole.User, "private-a")],
                new ChatOptions { Tools = [AIFunctionFactory.Create(() => ++firstCalls, "local")] }, timeout.Token),
            second.GetResponseAsync([new ChatMessage(ChatRole.User, "private-b")],
                new ChatOptions { Tools = [AIFunctionFactory.Create(() => ++secondCalls, "local")] }, timeout.Token));
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        Assert.Equal(4, model.Requests.Count);
        Assert.All(model.Requests, request =>
            Assert.Single(request, m => m.Text is "private-a" or "private-b"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposalOrCallerCancellationStopsPendingRun(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ChatResponseUpdate> Wait(
            [EnumeratorCancellation] CancellationToken token)
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unreachable");
        }
        var model = new ScriptedChatClient((_, _, token) => Wait(token));
        using var client = new LocalAgentChatClient(model.AsAIAgent(), AgentScenario.AgenticChat);
        using var cancellation = new CancellationTokenSource();
        var run = client.GetResponseAsync([new ChatMessage(ChatRole.User, "wait")], cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose)
        {
            client.Dispose();
        }
        else
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        if (dispose)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]));
        }
    }

    [Fact]
    public async Task ConcurrentRequestsOnOneConversationAreRejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ChatResponseUpdate> Wait([EnumeratorCancellation] CancellationToken token)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
        var model = new ScriptedChatClient((_, _, token) => Wait(token));
        using var client = new LocalAgentChatClient(model.AsAIAgent(), AgentScenario.AgenticChat);
        var first = client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]));
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task ToolFailuresAreSurfacedToCaller()
    {
        var model = new ScriptedChatClient((messages, _, _) =>
            messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()
                ? ScriptedChatClient.Stream(new TextContent("not success"))
                : ScriptedChatClient.Stream(new FunctionCallContent("failure", "fail", new Dictionary<string, object?>())));
        string Fail() => throw new InvalidOperationException("tool error");
        using var client = new LocalAgentChatClient(
            model.AsAIAgent(tools: [AIFunctionFactory.Create(Fail, "fail")]), AgentScenario.BackendTools);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]));
        Assert.Contains("tool error", error.InnerException!.Message);
    }
}
