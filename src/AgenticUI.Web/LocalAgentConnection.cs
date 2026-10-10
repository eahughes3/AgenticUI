using AgenticUI.AgentServer;
using Microsoft.Extensions.AI;

namespace AgenticUI.Web;

/// <summary>
/// Creates independently owned local clients for the Blazor scenario pages.
/// </summary>
public sealed class LocalAgentConnection(AgentCatalog catalog)
{
    public IChatClient CreateChatClient(AgentScenario scenario)
    {
        var agent = scenario switch
        {
            AgentScenario.AgenticChat => catalog.CreateAgenticChat(),
            AgentScenario.BackendTools => catalog.CreateBackendToolRendering(),
            AgentScenario.HumanInTheLoop => catalog.CreateHumanInTheLoop(),
            AgentScenario.FrontendTools => catalog.CreateToolBasedGenerativeUI(),
            AgentScenario.AgenticGenerativeUI => catalog.CreateAgenticGenerativeUI(),
            AgentScenario.SharedState => catalog.CreateSharedState(),
            AgentScenario.PredictiveState => catalog.CreatePredictiveState(),
            AgentScenario.Reasoning => catalog.CreateReasoning(),
            AgentScenario.Workflow => catalog.CreateWorkflow(),
            AgentScenario.SelectiveApproval => catalog.CreateSelectiveApproval(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        return new LocalAgentChatClient(agent, scenario);
    }
}
