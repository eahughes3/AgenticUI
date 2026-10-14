# Copilot instructions

## Build and test

The repository targets .NET 11 RC1; `global.json` selects the SDK. From the repository root:

```powershell
dotnet build AgenticUI.slnx
dotnet test tests\AgenticUI.Tests\AgenticUI.Tests.csproj
dotnet test tests\AgenticUI.Tests\AgenticUI.Tests.csproj --filter "FullyQualifiedName~LocalAgentChatClientTests.ApprovalExecutesOnlyAfterExplicitAcceptance"
```

For the full app, sign in with Azure CLI using an identity authorized for the configured Foundry deployment, then run `aspire start --non-interactive` and open the `web` resource in the Aspire dashboard. The AppHost references an existing Foundry account/deployment; it does not provision one.

## Architecture

- `AgenticUI.Web` is the only web host: a Blazor Interactive Server app. Browser interaction uses SignalR; model inference goes to Foundry over HTTPS.
- `AgenticUI.AgentServer` is a class library, despite its name. `AgentCatalog` creates Microsoft Agent Framework agents, and `LocalAgentConnection` gives scenario pages independent `LocalAgentChatClient` instances. The native MAF `AsIChatClient` adapter runs inside the web process; there is no agent HTTP API or AG-UI transport.
- Each local chat client owns a native agent session. Pages configure `LocalConversationThread` as stateful so the UI sends only new messages instead of replaying session history. Clients and conversations are page/circuit-scoped and in-memory.
- `AgenticUI.AppHost` orchestrates the web and MCP projects and references the existing Foundry deployment. `AgenticUI.ServiceDefaults` supplies service discovery, health checks, resilience, and OpenTelemetry.
- `AgentiUI.McpServerApp` defaults to stdio for IDE clients. Aspire starts it in HTTP mode at `/mcp`; only the Backend Tools scenario discovers its tools. MCP is separate from the in-process agent path.
- `AGUI.Abstractions` is used for local state event/request data only. Tool results can be projected into local snapshot/delta updates and consumed by typed `UIAgent` state mappers.

## Repository-specific conventions

- A scenario is wired across `AgentScenario`, an `AgentCatalog` factory, `LocalAgentConnection`, its Razor page, and `Components/Layout/NavMenu.razor`. Keep these mappings and the scenario's test coverage in sync.
- Scenario pages own and dispose their UI agent and chat client. Use a `LocalConversationThread` when constructing a `UIAgent`; preserve the per-page session boundary rather than sharing conversation state through the singleton catalog.
- Use `AIFunctionFactory.Create` for agent tools. Add tool argument/result types to `AgentServerSerializerContext` when source-generated JSON metadata is required. Approval-gated tools use `ApprovalRequiredAIFunction`; UI actions that require a user interaction use the Blazor AI component's `RegisterUIAction` path.
- Shared/predictive UI state is passed locally through `ChatOptions.RawRepresentationFactory` as `RunAgentInput`, not serialized as a provider or HTTP payload. Keep state-only updates in `RawRepresentation` and apply them through the page's `StateMapper`; use snapshots for complete state and deltas for incremental state changes.
- Tests use `ScriptedChatClient` and deterministic tool/model responses rather than live Foundry calls. When changing native adapter, tool, approval, state, or thread behavior, update the relevant contract/integration tests and verify behavior against the installed preview package APIs.
- Preview Blazor AI and MAF adapter APIs are integration-sensitive. Treat project package references as authoritative for versions and recheck the relevant contract tests when upgrading them.

See `README.md` for local run/configuration details and `docs/findings.md` for state mapping, tool, approval, and rendering constraints.
