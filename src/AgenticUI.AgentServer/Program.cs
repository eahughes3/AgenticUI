using AgenticUI.AgentServer;
using AGUI.Server;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel.Primitives;
using Azure.Identity;


var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Make the tool argument/result types available to System.Text.Json.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(AgentServerSerializerContext.Default));

// Register AG-UI server support (augments the ASP.NET Core JSON options with the AG-UI event types).
builder.Services.AddAGUIServer();

var app = builder.Build();

app.MapDefaultEndpoints();

// Build the per-scenario agents backed by Microsoft Foundry.
var endpoint = app.Configuration["CHAT_URI"];
var deployment = app.Configuration["CHAT_MODELNAME"];

var foundryConfigured = !string.IsNullOrWhiteSpace(
    app.Configuration["CHAT_URI"] ?? app.Configuration["CHAT_MODELNAME"]);

BearerTokenPolicy tokenPolicy = new(
    new DefaultAzureCredential(),
    "https://ai.azure.com/.default");

if (foundryConfigured)
{
    // Build the per-scenario agents backed by Microsoft Foundry.
    //var foundry = Foundry.ReadOptions(app.Configuration);
    //var chatClient = Foundry.CreateChatClient(foundry);

    var chatClient = new ChatClient(
    model: deployment,
    authenticationPolicy: tokenPolicy,
    options: new OpenAIClientOptions
    {
        Endpoint = new Uri($"{endpoint}openai/v1/")
    });

    var reasoningChatClient = chatClient.AsIChatClient();

    var agents = new AgentCatalog(chatClient, reasoningChatClient);

    // Map one AG-UI endpoint per scenario. Each is an HTTP POST that streams AG-UI events (SSE).
    app.MapAGUIServer("/agentic_chat", agents.CreateAgenticChat());
    app.MapAGUIServer("/backend_tool_rendering", agents.CreateBackendToolRendering());
    app.MapAGUIServer("/human_in_the_loop", agents.CreateHumanInTheLoop());
    app.MapAGUIServer("/tool_based_generative_ui", agents.CreateToolBasedGenerativeUI());
    app.MapAGUIServer("/agentic_generative_ui", agents.CreateAgenticGenerativeUI())
        .WithMetadata(new AGUIStreamOptions()
            .MapResultAsStateSnapshot("create_plan")   // full plan -> STATE_SNAPSHOT
            .MapResultAsStateDelta("update_plan_step")); // JSON Patch -> STATE_DELTA
    app.MapAGUIServer("/shared_state", agents.CreateSharedState())
        .WithMetadata(new AGUIStreamOptions().MapResultAsStateSnapshot("generate_recipe"));
    app.MapAGUIServer("/predictive_state", agents.CreatePredictiveState());
    app.MapAGUIServer("/reasoning", agents.CreateReasoning());
    app.MapAGUIServer("/workflow", agents.CreateWorkflow());
    app.MapAGUIServer("/selective_approval", agents.CreateSelectiveApproval());

}

app.MapGet("/", () => Results.Ok(new
{
    service = "AgenticUI AG-UI agent server",
    model = app.Configuration["CHAT_MODEL"] ?? FoundryOptions.DefaultModel,
    reasoningModel = app.Configuration["CHAT_REASONING_MODEL"] ?? FoundryOptions.DefaultReasoningModel,
    endpoints = new[]
    {
        "/agentic_chat",
        "/backend_tool_rendering",
        "/human_in_the_loop",
        "/tool_based_generative_ui",
        "/agentic_generative_ui",
        "/shared_state",
        "/reasoning",
        "/predictive_state",
        "/workflow",
        "/selective_approval"
    }
}));

app.Run();
