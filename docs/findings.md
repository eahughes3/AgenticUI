# Integration findings

## In-process hosting

Blazor AI components consume `IChatClient`, not an HTTP endpoint. `AgenticUI.AgentServer` is a class library consumed by `AgenticUI.Web`; there is no `AddAGUIServer`, `MapAGUIServer`, `AGUIChatClient`, named remote-agent HTTP client, or SSE stream.

The installed MAF 1.24.0 native `AsIChatClient` adapter is experimental. Contract tests verify that it preserves executed function calls/results, invokes run-supplied executable tools, and transports native `ToolApprovalRequestContent`/approval responses without AG-UI transport.

Each `LocalAgentChatClient` owns a native session and supports one in-flight request. Every UIAgent is configured with `LocalConversationThread`, which captures the session ID and reports stateful history. The preview component does not infer statefulness when no thread is configured: it would resend full history, duplicating messages and approval requests. The explicit thread makes subsequent turns send only new messages. Clients are page-owned; the catalog shares provider infrastructure, not conversation state. Reset/disposal cancels active work and discards the session.

## Automatic tools and interactive actions

The native agent pipeline invokes executable frontend functions supplied in `ChatOptions.Tools`. The accent-color function belongs to its originating Blazor page/circuit; it does not execute in the browser. Backend and frontend tools use the same native invocation pipeline, with no second `FunctionInvokingChatClient` wrapped around the agent.

`RegisterUIAction` declares an interactive action instead of an immediately executable function. A `UIActionBlock` remains pending until the UI calls `InvokeAsync()`. Predictive document editing uses that path to collect the user's acceptance/rejection before continuing.

Approval-required backend functions use MAF's native approval content. They do not execute before explicit approval; rejection leaves them unexecuted.

## State input, context, and projection

AG-UI is retained only as local data structures (`AGUI.Abstractions`). Existing shared-state and predictive-state pages create a `RunAgentInput` through `ChatOptions.RawRepresentationFactory`. The local client captures it into local run options and clears the provider raw factory, so it is not an HTTP payload or an OpenAI request representation.

`RecipeStateAgent` and `DocumentStateAgent` insert the latest state immediately before the current user message. This keeps local edits authoritative relative to older conversation/tool content.

The local stream projects backend results as they arrive:

| Tool | Local projection |
| --- | --- |
| `generate_recipe` | Complete recipe `StateSnapshotEvent` |
| `create_plan` | Complete plan `StateSnapshotEvent` |
| `update_plan_step` | `StateDeltaEvent` carrying the existing JSON Patch array |

Native tool call/result content remains available for block rendering and history. State-only updates are projected into `RawRepresentation` without adding presentation text to model messages. Plan statuses use lowercase `pending`/`completed`, and deltas are delivered before subsequent inference completes.

## Predictive state

The document proposal is mapped explicitly to `SetPredictiveState`, then resolved with `AcceptPredictiveState` or `RejectPredictiveState`. Review validation rejects stale or unreviewed proposals. AgentContext rejects pending predictive state before publishing Idle/Error.

Completed native tool arguments must be checked against the installed provider/component representation, rather than assuming the HTTP adapter's JSON deserialization is still present. The mapper accepts supported string representations explicitly.

This sample still maps completed proposals, not partial streaming argument fragments. Rejecting a proposal restores the previous document, but prompt instructions alone cannot guarantee that a live model will never propose it again.

## Rendering

Reasoning is provider-neutral: `ReasoningActivityHandler` consumes `TextReasoningContent` directly. No `REASONING_*` transport conversion is needed. Normal scenarios retain Chat Completions with explicit `reasoning_effort: none` so function tools work. The reasoning scenario uses the v1 Responses API, reusing the Azure client's configured authentication and transport. The Responses client is constructed directly because Azure.AI.OpenAI 2.9.0-beta.1's Responses factory calls a constructor removed in OpenAI 2.14.0. Host navigation and client request tests cover this runtime compatibility boundary.

`FormattedChatClient` still accumulates Markdown and supplies `RichTextContent` snapshots through the existing parser. This retains the earlier rendering tradeoff: presentation snapshots may be kept in history alongside ordinary text. The preview public block API does not expose the replacement/finalization hooks that would eliminate the wrapper.

Workflow and selective-approval agent definitions remain in the catalog. Workflow lifecycle events are not automatically projected into new UI activity types; no new workflow page was added.

## Lifetimes and limitations

`AgentBoundary` recreates its context/descendants when the agent changes. Predictive reset creates both a new UIAgent and a new local client/session, clearing pending work and previous document state.

The browser still connects to Blazor through SignalR; the model provider still calls Foundry over HTTPS. Normal web routes, static assets, antiforgery, and health checks remain. Only the agent HTTP transport has been removed.

There is no durable conversation store, cross-server session migration, or circuit-loss recovery. A new page/client owns a fresh conversation.

The package manifests are authoritative: MAF 1.24.0, AGUI.Abstractions 1.0.0, and Components.AI 0.1.0-preview.1.26459.102 on .NET 11 RC1. Preview/native adapter APIs may change; deterministic integration tests should be rerun after upgrades.
