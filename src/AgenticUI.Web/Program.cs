using AgenticUI.Web;
using AgenticUI.Web.Components;
using AgenticUI.AgentServer;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var openai = builder.AddAzureOpenAIClient(connectionName: "chat")
    .AddChatClient("chat");

builder.Services.AddSingleton<AgentCatalog>(services =>
{
    var chatClient = services.GetRequiredService<IChatClient>();
    return new AgentCatalog(chatClient, chatClient);
});


//builder.Services.AddSingleton<AgentCatalog>(services =>
//{
//    var configuration = services.GetRequiredService<IConfiguration>();
//    var endpoint = configuration["CHAT_URI"];
//    var deployment = configuration["CHAT_MODELNAME"];
//    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
//    {
//        throw new InvalidOperationException(
//            "Configure CHAT_URI and CHAT_MODELNAME on the Blazor host, or reference the Foundry chat deployment from Aspire.");
//    }

//    BearerTokenPolicy tokenPolicy = new(new DefaultAzureCredential(), "https://ai.azure.com/.default");
//#pragma warning disable OPENAI001 // Preserve the existing Foundry bearer-authenticated OpenAI client.
//    var client = new ChatClient(deployment, tokenPolicy,
//        new OpenAIClientOptions { Endpoint = new Uri($"{endpoint}openai/v1/") });
//#pragma warning restore OPENAI001
//    var chatClient = client.AsIChatClient();
//    return new AgentCatalog(chatClient, chatClient);
//});

builder.Services.AddScoped<LocalAgentConnection>();
builder.Services.AddScoped<BackendMcpConnection>();
// MCP POSTs execute tools, so never automatically replay them through HTTP resilience.
#pragma warning disable EXTEXP0001 // Remove the shared defaults' resilience handler for the streaming MCP transport.
builder.Services.AddHttpClient("backend-mcp", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
