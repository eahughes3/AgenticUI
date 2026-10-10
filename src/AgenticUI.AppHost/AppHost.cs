using Aspire.Hosting;
using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

// Microsoft Foundry configuration. The app consumes an existing Foundry inference endpoint but
// doesn't manage the Foundry account or its child resources.
// The deployment names default to gpt-5-mini and can be overridden with Parameters:foundry-model
// and Parameters:foundry-reasoning-model. Note that the AddParameter overload taking a value treats
// it as a constant rather than a default, so read configuration explicitly to honor the override.
//var foundryEndpoint = builder.AddParameter("foundry-endpoint");
//var foundryModel = builder.AddParameter("foundry-model",
//    value: builder.Configuration["Parameters:foundry-model"] ?? "gpt-5-mini");
//var foundryReasoningModel = builder.AddParameter("foundry-reasoning-model",
//    value: builder.Configuration["Parameters:foundry-reasoning-model"] ?? "gpt-5-mini");

var foundryName = builder.AddParameter("foundryName", "ervin-mp3gmu7n-eastus2");
var foundryRegion = builder.AddParameter("foundryRegion", "westus2");
var resourceGroup = builder.AddParameter("foundryResourceGroup", "rg-avinosoft-ai");

var foundry = builder.AddFoundry("aiFoundry")
    .AsExisting(foundryName, resourceGroup);

var chat = foundry.AddDeployment("chat", FoundryModel.OpenAI.Gpt56Luna);

// The AG-UI agent server: hosts one AG-UI endpoint per demo scenario (MAF + AG-UI C# SDK).
var agentServer = builder.AddProject<Projects.AgenticUI_AgentServer>("agentserver")
    .WithReference(chat)
    .WithReference(foundry);


// The Blazor front end: consumes the AG-UI endpoints via the Blazor AI components.
builder.AddProject<Projects.AgenticUI_Web>("web")
    .WithReference(agentServer)
    .WaitFor(agentServer);

builder.Build().Run();
