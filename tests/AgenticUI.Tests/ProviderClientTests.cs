using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using AgenticUI.AgentServer;
using AgenticUI.Web;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticUI.Tests;

public sealed class ProviderClientTests
{
    [Fact]
    public async Task ResponsesClientUsesCompatibleSdkAndPreservesAzureAuthentication()
    {
        using var handler = new ResponsesHandler();
        using var http = new HttpClient(handler);
        var azure = new AzureOpenAIClient(new Uri("https://test.openai.azure.com/"),
            new ApiKeyCredential("test-key"), new AzureOpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(http)
            });
        using var client = Foundry.CreateResponsesChatClient(azure, "test-reasoning");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions { Reasoning = new ReasoningOptions { Output = ReasoningOutput.Full } });

        Assert.Equal("The answer.", response.Text);
        Assert.Equal("https://test.openai.azure.com/openai/v1/responses", handler.Endpoint);
        Assert.Equal("test-key", handler.ApiKey);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("test-reasoning", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("detailed", request.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChatCompletionsDisablesReasoningWithoutMutatingCallerOptions(bool streaming, bool supplyOptions)
    {
        var tool = AIFunctionFactory.Create(() => 42, "get_random_number");
        var options = supplyOptions ? new ChatOptions
        {
            Tools = [tool],
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full }
        } : null;
        var model = new ScriptedChatClient((_, actual, _) =>
        {
            Assert.NotNull(actual);
            Assert.NotSame(options, actual);
            Assert.Equal(ReasoningEffort.None, actual.Reasoning!.Effort);
            Assert.Equal(ReasoningOutput.None, actual.Reasoning.Output);
            if (supplyOptions)
            {
                Assert.Same(tool, Assert.Single(actual.Tools!));
            }
            return ScriptedChatClient.Stream(new TextContent("done"));
        });
        using var client = new ReasoningDisabledChatClient(model);
        ChatMessage[] messages = [new(ChatRole.User, "random")];

        var response = streaming
            ? await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync()
            : await client.GetResponseAsync(messages, options);

        Assert.Equal("done", response.Text);
        if (supplyOptions)
        {
            Assert.Equal(ReasoningEffort.High, options!.Reasoning!.Effort);
            Assert.Equal(ReasoningOutput.Full, options.Reasoning.Output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolRequestsSerializeReasoningEffortNone(bool streaming)
    {
        using var handler = new ChatHandler(streaming);
        using var http = new HttpClient(handler);
        var azure = new AzureOpenAIClient(new Uri("https://test.openai.azure.com/"),
            new ApiKeyCredential("test-key"), new AzureOpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(http)
            });
        using var client = new ReasoningDisabledChatClient(azure.GetChatClient("chat").AsIChatClient());
        ChatMessage[] messages = [new(ChatRole.User, "random")];
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => 42, "get_random_number")] };

        var response = streaming
            ? await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync()
            : await client.GetResponseAsync(messages, options);

        Assert.Equal("done", response.Text);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("none", request.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("get_random_number", request.RootElement.GetProperty("tools")[0]
            .GetProperty("function").GetProperty("name").GetString());
        Assert.Null(options.Reasoning);
    }

    private sealed class ChatHandler(bool streaming) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = streaming
                ? """
                    data: {"id":"chat_test","object":"chat.completion.chunk","created":1,"model":"chat","choices":[{"index":0,"delta":{"role":"assistant","content":"done"},"finish_reason":null}]}

                    data: {"id":"chat_test","object":"chat.completion.chunk","created":1,"model":"chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]


                    """
                : """
                    {"id":"chat_test","object":"chat.completion","created":1,"model":"chat","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}
                    """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }

    private sealed class ResponsesHandler : HttpMessageHandler
    {
        public string? Endpoint { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri!.AbsoluteUri;
            ApiKey = request.Headers.GetValues("api-key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "id": "resp_test", "object": "response", "created_at": 1,
                      "status": "completed", "model": "test-reasoning",
                      "output": [{
                        "id": "msg_test", "type": "message", "status": "completed", "role": "assistant",
                        "content": [{"type": "output_text", "text": "The answer.", "annotations": []}]
                      }]
                    }
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}
