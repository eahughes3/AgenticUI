using System.Text.Json;
using AgenticUI.AgentServer;
using AgenticUI.Web;
using AgenticUI.Web.Components.Pages.Scenarios;
using AgenticUI.Web.Models;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticUI.Tests;

public sealed class UIAgentParityTests
{
    [Fact]
    public async Task SessionBoundHistoryDoesNotDuplicateEarlierTurns()
    {
        var model = new ScriptedChatClient((messages, _, _) =>
            ScriptedChatClient.Stream(new TextContent($"reply to {messages.Last().Text}")));
        using var client = CreateClient(model, AgentScenario.AgenticChat);
        using var agent = new UIAgent(client, options => options.Thread = new LocalConversationThread());

        await SendAsync(agent, "first");
        var second = await SendAsync(agent, "second");

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(["first", "reply to first", "second"],
            model.Requests[1].Where(m => m.Role != ChatRole.System).Select(m => m.Text));
        Assert.Contains(second.OfType<RichContentBlock>(), b => b.RawText == "reply to second");
    }

    [Fact]
    public async Task NativeWeatherCallAndResultPopulateExistingGeneratedBlock()
    {
        var model = ToolModel("get_weather", new() { ["location"] = "Seattle" });
        using var client = CreateClient(model, AgentScenario.BackendTools);
        using var agent = new UIAgent(client, AddGeneratedToolBlocks);

        var blocks = await SendAsync(agent, "weather");

        var weather = Assert.Single(blocks.OfType<WeatherToolBlock>());
        Assert.Equal("Seattle", weather.Location);
        Assert.Equal("get_weather", weather.ToolName);
        Assert.Equal("call-1", weather.Call!.CallId);
        Assert.True(weather.HasResult);
        Assert.Equal(weather.Call.CallId, weather.Result!.CallId);
        Assert.NotNull(weather.Weather);
        Assert.Equal(20, weather.Weather.Temperature);
        Assert.Equal("sunny", weather.Weather.Conditions);
        Assert.Equal(50, weather.Weather.Humidity);
        Assert.Equal(10, weather.Weather.WindSpeed);
        Assert.Equal(25, weather.Weather.FeelsLike);
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task FrontendFunctionExecutesExactlyOnceAndPopulatesGeneratedBlock()
    {
        var executions = 0;
        string? accent = null;
        var model = ToolModel("set_accent_color", new() { ["color"] = "rebeccapurple" });
        using var client = CreateClient(model, AgentScenario.FrontendTools);
        using var agent = new UIAgent(client, options =>
        {
            options.Thread = new LocalConversationThread();
            options.ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create((string color) =>
                {
                    executions++;
                    accent = color;
                    return $"Accent color set to {color}.";
                }, "set_accent_color")]
            };
            AddGeneratedToolBlocks(options);
        });

        var blocks = await SendAsync(agent, "change color");

        Assert.Equal(1, executions);
        Assert.Equal("rebeccapurple", accent);
        var block = Assert.Single(blocks.OfType<AccentColorToolBlock>());
        Assert.True(block.HasResult);
        Assert.Equal(accent, block.Color);
        Assert.Equal("Accent color set to rebeccapurple.", block.Confirmation);
        Assert.Single(model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task RegisteredUIActionPausesUntilInvokedAndResumesOnlyOnce()
    {
        var executions = 0;
        var model = ToolModel("local_action", new());
        using var client = CreateClient(model, AgentScenario.FrontendTools);
        using var agent = new UIAgent(client, options =>
        {
            options.Thread = new LocalConversationThread();
            options.RegisterUIAction(AIFunctionFactory.Create(() => ++executions, "local_action"));
        });
        UIActionBlock? action = null;

        var blocks = await SendAsync(agent, "perform action", async block =>
        {
            if (block is not UIActionBlock pending)
            {
                return;
            }

            action = pending;
            Assert.Equal(0, executions);
            Assert.Single(model.Requests);
            Assert.False(pending.IsComplete);
            Assert.Null(pending.Result);
            await pending.InvokeAsync();
            await pending.InvokeAsync();
            Assert.Equal(1, executions);
            Assert.True(pending.IsComplete);
        });

        Assert.Same(action, Assert.Single(blocks.OfType<UIActionBlock>()));
        Assert.Equal(1, executions);
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        Assert.Contains(blocks.OfType<RichContentBlock>(), b => b.RawText == "done");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PredictiveDocumentReviewCommitsOrRestoresState(bool accepted)
    {
        var model = ToolModel("propose_document", new() { ["document"] = "# Proposed" });
        using var client = CreateClient(model, AgentScenario.PredictiveState);
        var executions = 0;
        object? completedArgument = null;
        UIAgent<DocumentState> agent = null!;
        agent = new UIAgent<DocumentState>(client, options =>
        {
            options.Thread = new LocalConversationThread();
            options.StateMapper = context =>
            {
                var call = context.Update.Contents.OfType<FunctionCallContent>()
                    .FirstOrDefault(c => c.Name == "propose_document");
                if (call?.Arguments?.TryGetValue("document", out var value) != true)
                {
                    return;
                }

                completedArgument = value;
                context.SetPredictiveState(new DocumentState { Document = ReadDocument(value) });
            };
            options.RegisterUIAction(AIFunctionFactory.Create((string document, bool? accepted = null) =>
            {
                executions++;
                Assert.Equal("# Proposed", document);
                Assert.True(agent.State.HasPendingPredictiveState);
                if (accepted == true)
                {
                    agent.State.AcceptPredictiveState();
                }
                else
                {
                    agent.State.RejectPredictiveState();
                }

                return accepted == true ? "accepted" : "rejected";
            }, "propose_document"));
        }, new DocumentState { Document = "original" });
        using (agent)
        {
            await SendAsync(agent, "edit", async block =>
            {
                if (block is UIActionBlock action)
                {
                    Assert.Equal(0, executions);
                    Assert.Single(model.Requests);
                    Assert.True(agent.State.HasPendingPredictiveState);
                    Assert.Equal("# Proposed", agent.State.Value.Document);
                    Assert.IsType<string>(completedArgument);
                    Assert.Equal("# Proposed", ReadDocument(completedArgument));
                    action.Call.Arguments!["accepted"] = accepted;
                    await action.InvokeAsync();
                }
            });

            Assert.Equal(1, executions);
            Assert.False(agent.State.HasPendingPredictiveState);
            Assert.Equal(accepted ? "# Proposed" : "original", agent.State.Value.Document);
            Assert.Equal(2, model.Requests.Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPredictiveActionOrContinuationRollsBackUncommittedDocument(bool failAction)
    {
        var failure = new InvalidOperationException("scripted predictive failure");
        var model = new ScriptedChatClient((messages, _, _) =>
            HasResult(messages)
                ? throw failure
                : ScriptedChatClient.Stream(new FunctionCallContent("call-1", "propose_document",
                    new Dictionary<string, object?> { ["document"] = "# Proposed" })));
        using var client = CreateClient(model, AgentScenario.PredictiveState);
        using var agent = new UIAgent<DocumentState>(client, options =>
        {
            options.Thread = new LocalConversationThread();
            options.StateMapper = context =>
            {
                var call = context.Update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
                if (call?.Arguments?.TryGetValue("document", out var value) == true)
                {
                    context.SetPredictiveState(new DocumentState { Document = ReadDocument(value) });
                }
            };
            options.RegisterUIAction(AIFunctionFactory.Create((string document) =>
                failAction ? throw failure : "reviewed", "propose_document"));
        }, new DocumentState { Document = "original" });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(agent, "edit", async block =>
        {
            if (block is UIActionBlock action)
            {
                Assert.True(agent.State.HasPendingPredictiveState);
                Assert.Equal("# Proposed", agent.State.Value.Document);
                await action.InvokeAsync();
            }
        }));

        Assert.Contains("scripted predictive failure", error.ToString());
        Assert.False(agent.State.HasPendingPredictiveState);
        Assert.Equal("original", agent.State.Value.Document);
        Assert.Equal(failAction ? 1 : 2, model.Requests.Count);
    }

    [Fact]
    public async Task NativeReasoningContentReachesExistingActivityHandler()
    {
        var ordinary = new ScriptedChatClient((_, _, _) =>
            throw new InvalidOperationException("The reasoning catalog must use its reasoning client."));
        var reasoning = new ScriptedChatClient((_, options, _) =>
        {
            Assert.Equal(ReasoningOutput.Full, options!.Reasoning!.Output);
            return ScriptedChatClient.Stream(new TextReasoningContent("Considering the evidence."),
                new TextContent("The answer."));
        });
        using var client = new LocalAgentConnection(new AgentCatalog(ordinary, reasoning))
            .CreateChatClient(AgentScenario.Reasoning);
        using var agent = new UIAgent(client, options =>
        {
            options.Thread = new LocalConversationThread();
            options.AddBlockHandler(new ReasoningActivityHandler());
        });

        var blocks = await SendAsync(agent, "reason");

        var activity = Assert.Single(blocks.OfType<ReasoningActivityBlock>());
        Assert.Equal("reasoning", activity.ActivityType);
        Assert.Equal("Considering the evidence.", activity.Text);
        Assert.Contains(blocks.OfType<RichContentBlock>(), b => b.RawText == "The answer.");
        Assert.Empty(ordinary.Requests);
        Assert.Single(reasoning.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectiveApprovalExecutesBalanceButWaitsForTransferReview(bool approved)
    {
        var model = new ScriptedChatClient((messages, _, _) =>
        {
            var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            return results.Count switch
            {
                0 => ScriptedChatClient.Stream(new FunctionCallContent("balance", "get_account_balance", new Dictionary<string, object?>())),
                1 => ScriptedChatClient.Stream(new FunctionCallContent("transfer", "transfer_funds",
                    new Dictionary<string, object?> { ["toAccount"] = "savings", ["amount"] = 10m })),
                _ => ScriptedChatClient.Stream(new TextContent("done"))
            };
        });
        using var client = CreateClient(model, AgentScenario.SelectiveApproval);
        using var agent = new UIAgent(client, options => options.Thread = new LocalConversationThread());

        var blocks = await SendAsync(agent, "check and transfer", block =>
        {
            if (block is FunctionApprovalBlock approval)
            {
                Assert.Equal("transfer_funds", approval.ToolName);
                Assert.Equal(ApprovalStatus.Pending, approval.Status);
                Assert.False(approval.HasResult);
                var balance = Assert.Single(model.Requests.Last().SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>());
                Assert.Equal("Your current balance is $1,250.00.", balance.Result?.ToString());
                if (approved)
                {
                    approval.Approve();
                }
                else
                {
                    approval.Reject("declined");
                }
            }

            return Task.CompletedTask;
        });

        var review = Assert.Single(blocks.OfType<FunctionApprovalBlock>());
        Assert.Equal(approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected, review.Status);
        Assert.Equal(3, model.Requests.Count);
        var transfer = Assert.Single(model.Requests.Last().SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>(), r => r.CallId == "transfer");
        Assert.Equal(approved, transfer.Result?.ToString()?.Contains("savings") == true);
        Assert.Contains(blocks.OfType<RichContentBlock>(), b => b.RawText == "done");
    }

    [Fact]
    public async Task SequentialWorkflowRendersResearchAndReportWithoutLiveModel()
    {
        var model = new ScriptedChatClient((_, options, _) =>
            ScriptedChatClient.Stream(new TextContent(
                options!.Instructions!.Contains("Research the user's topic", StringComparison.Ordinal)
                    ? "Research brief."
                    : "Final report.")));
        using var client = CreateClient(model, AgentScenario.Workflow);
        using var agent = new UIAgent(client, options => options.Thread = new LocalConversationThread());

        var blocks = await SendAsync(agent, "research a topic");

        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[1], m => m.Text == "Research brief.");
        Assert.Contains(blocks.OfType<RichContentBlock>(), b => b.RawText.Contains("Final report.", StringComparison.Ordinal));
    }

    private static IChatClient CreateClient(ScriptedChatClient model, AgentScenario scenario) =>
        new LocalAgentConnection(new AgentCatalog(model, model)).CreateChatClient(scenario);

    private static void AddGeneratedToolBlocks(UIAgentOptions options)
    {
        options.Thread = new LocalConversationThread();
        // The Web source generator emits an assembly-internal registration extension.
        var registrations = Assert.Single(typeof(WeatherToolBlock).Assembly.GetTypes(),
            type => type.Name == "GeneratedToolBlockRegistrations");
        var method = registrations.GetMethod("AddGeneratedToolBlocks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, [options]);
    }

    private static ScriptedChatClient ToolModel(string name, Dictionary<string, object?> arguments) =>
        new((messages, _, _) => HasResult(messages)
            ? ScriptedChatClient.Stream(new TextContent("done"))
            : ScriptedChatClient.Stream(new FunctionCallContent("call-1", name, arguments)));

    private static bool HasResult(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();

    // Native calls retain CLR strings; the earlier AG-UI mapper received JsonElement strings.
    private static string ReadDocument(object? value) => value switch
    {
        string document => document,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
        _ => throw new InvalidOperationException("The completed document argument must be a string.")
    };

    private static async Task<List<ContentBlock>> SendAsync(
        UIAgent agent, string text, Func<ContentBlock, Task>? onBlock = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var context = new AgentContext(agent);
        var blocks = new List<ContentBlock>();
        var callbacks = new List<Task>();
        using var subscription = context.RegisterOnBlockAdded((_, block) =>
        {
            blocks.Add(block);
            if (onBlock is not null)
            {
                callbacks.Add(onBlock(block));
            }
        });
        await context.SendMessageAsync(text, timeout.Token).WaitAsync(timeout.Token);
        await Task.WhenAll(callbacks).WaitAsync(timeout.Token);
        if (context.Error is { } error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return blocks;
    }
}
