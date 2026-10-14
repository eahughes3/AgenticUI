using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticUI.AgentServer;

/// <summary>A page-owned native agent session, with local state-event projection.</summary>
public sealed class LocalAgentChatClient(AIAgent agent, AgentScenario scenario) : IChatClient
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private IChatClient? _client;
    private bool _running;
    private bool _disposed;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationToken lifetimeToken;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("An agent conversation can only run one request at a time.");
            }

            _running = true;
            lifetimeToken = _lifetime.Token;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
            if (_client is null)
            {
                var session = await agent.CreateSessionAsync(linked.Token).ConfigureAwait(false);
#pragma warning disable MAAI001 // Installed MAF's native IChatClient adapter is experimental.
                _client = agent.AsIChatClient(session, allowNonChatClientAgents: scenario == AgentScenario.Workflow);
#pragma warning restore MAAI001
            }

            var calls = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var update in _client.GetStreamingResponseAsync(
                messages, LocalRunOptions.Prepare(options, _client), linked.Token).ConfigureAwait(false))
            {
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                {
                    calls[call.CallId] = call.Name;
                }

                foreach (var result in update.Contents.OfType<FunctionResultContent>())
                {
                    if (result.Exception is { } error)
                    {
                        throw new InvalidOperationException($"Agent tool {result.CallId} failed.", error);
                    }
                }

                yield return update;
                foreach (var result in update.Contents.OfType<FunctionResultContent>())
                {
                    if (calls.TryGetValue(result.CallId, out var name) &&
                        ProjectState(name, result.Result) is { } stateEvent)
                    {
                        yield return new ChatResponseUpdate
                        {
                            ConversationId = update.ConversationId,
                            RawRepresentation = stateEvent
                        };
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (_disposed)
                {
                    ReleaseResources();
                }
            }
        }
    }

    private BaseEvent? ProjectState(string tool, object? result)
    {
        var snapshot = (scenario == AgentScenario.SharedState && tool == "generate_recipe") ||
            (scenario == AgentScenario.AgenticGenerativeUI && tool == "create_plan");
        var delta = scenario == AgentScenario.AgenticGenerativeUI && tool == "update_plan_step";
        if (!snapshot && !delta)
        {
            return null;
        }

        var json = result switch
        {
            JsonElement element => element.Clone(),
            null => throw new InvalidOperationException($"State tool {tool} returned no state."),
            _ => JsonSerializer.SerializeToElement(result,
                AgentServerSerializerContext.Default.GetTypeInfo(result.GetType()) ??
                throw new InvalidOperationException($"State tool {tool} returned an unsupported type."))
        };
        return snapshot
            ? new StateSnapshotEvent { Snapshot = json }
            : new StateDeltaEvent { Delta = json };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : agent.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            if (!_running)
            {
                ReleaseResources();
            }
        }
    }

    private void ReleaseResources()
    {
        _client?.Dispose();
        _client = null;
        _lifetime.Dispose();
    }
}
