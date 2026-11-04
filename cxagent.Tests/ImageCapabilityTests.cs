using System.Net;
using System.Text;
using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Whether a model sees images: the configuration's word first, then what the endpoint says, else
/// unknown, which lets the image go and the endpoint answer for itself.
/// </summary>
public class ImageCapabilityTests
{
    [Theory]
    [InlineData("""{"modalities":{"vision":true,"audio":false}}""", true)]
    [InlineData("""{"modalities":{"vision":false,"audio":false}}""", false)]
    [InlineData("""{"chat_template":"x"}""", null)]
    [InlineData("not json", null)]
    public void LlamaProps_SayWhetherTheServerLoadedAVisionProjector(string json, bool? sees)
    {
        Assert.Equal(sees, ImageCapability.ParseLlamaProps(json));
    }

    [Theory]
    [InlineData("""{"capabilities":["completion","vision"]}""", true)]
    [InlineData("""{"capabilities":["completion","tools"]}""", false)]
    [InlineData("""{"details":{}}""", null)]
    public void OllamaShow_ListsVisionAmongItsCapabilities(string json, bool? sees)
    {
        Assert.Equal(sees, ImageCapability.ParseOllamaShow(json));
    }

    [Theory]
    [InlineData("""{"data":[{"id":"m","architecture":{"input_modalities":["text","image"]}}]}""", true)]
    [InlineData("""{"data":[{"id":"m","architecture":{"input_modalities":["text"]}}]}""", false)]
    [InlineData("""{"data":[{"id":"other","architecture":{"input_modalities":["image"]}}]}""", null)]
    public void OpenRouterModels_ListImageAmongTheModelsInputs(string json, bool? sees)
    {
        Assert.Equal(sees, ImageCapability.ParseOpenRouterModels(json, "m"));
    }

    private sealed class Serves(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsolutePath);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string Reply = """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private static OpenAiCompatibleProvider Llama(Serves serves, bool? pin = null) => new(new OpenAiProviderOptions
    {
        ProviderId = "local", DisplayName = "local", Model = "m", BaseUrl = "http://h/v1",
        Client = new HttpClient(serves), Images = pin,
    });

    private static List<ChatMessage> WithAnImage() =>
    [
        new() { Role = "user", Content = "what is [Image #1 4×2]?",
            Images = [new AttachedImage("[Image #1 4×2]", "h", 4, 2) { Bytes = [1, 2, 3] }] },
    ];

    [Fact]
    public async Task ThePin_OverridesTheProbe()
    {
        var serves = new Serves(_ => """{"modalities":{"vision":false}}""");

        Assert.True(await Llama(serves, pin: true).CanSeeImagesAsync());
        Assert.DoesNotContain("/props", serves.Asked);
    }

    [Fact]
    public async Task Anthropic_IsYesWithoutAsking()
    {
        var provider = new AnthropicProvider("a", "a", "claude", "key");

        Assert.True(provider.KnownCanSeeImages);
        Assert.True(await provider.CanSeeImagesAsync());
    }

    /// <summary>
    /// Measured on the user's local Qwen: llama.cpp without a vision projector refuses a request with an
    /// image, 500, and every later turn too, since the image stays in the context. Asked first, it gets
    /// the placeholder and the conversation carries on.
    /// </summary>
    [Fact]
    public async Task ATextOnlyModel_GetsThePlaceholderInsteadOfTheImage()
    {
        var serves = new Serves(r => r.RequestUri!.AbsolutePath == "/props" ? """{"modalities":{"vision":false}}""" : Reply);

        await Llama(serves).ChatAsync(WithAnImage(), null, CancellationToken.None);

        var sent = Assert.Single(serves.Bodies);
        var content = System.Text.Json.Nodes.JsonNode.Parse(sent)!["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Equal("what is [Image #1 4×2]?\n[image not sent: this model cannot see images — Image #1, 4×2]", content);
        Assert.DoesNotContain("image_url", sent);
    }

    /// <summary>
    /// An endpoint with no route that says — /props answers nothing about images — refusing the image
    /// itself, as a text-only model behind vLLM or LM Studio does with a 400.
    /// </summary>
    private sealed class RefusesImages : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/props")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"chat_template":"x"}""") };
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return body.Contains("image_url")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"image input is not supported"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
        }
    }

    private static OpenAiCompatibleProvider Unaskable(HttpMessageHandler handler, bool? pin = null) => new(new OpenAiProviderOptions
    {
        ProviderId = "local", DisplayName = "local", Model = "m", BaseUrl = "http://h/v1",
        Client = new HttpClient(handler), Images = pin, Retry = new RetryPolicy(1, TimeSpan.Zero),
    });

    [Fact]
    public async Task ARefusedImage_GoesAgainAsItsPlaceholder_AndTheAnswerIsKept()
    {
        var handler = new RefusesImages();
        var provider = Unaskable(handler);

        var reply = await provider.ChatAsync(WithAnImage(), null, CancellationToken.None);

        Assert.Equal("ok", reply.Text);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("[image not sent: this model cannot see images", handler.Bodies[1]);
        Assert.False(provider.KnownCanSeeImages);
    }

    /// <summary>An endpoint whose /props says nothing about images, refusing every chat with one answer.</summary>
    private sealed class AlwaysRefuses(string error) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/props")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"chat_template":"x"}""") };
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(error) };
        }
    }

    /// <summary>
    /// A context that outgrew the window is refused with a 400 too. It is the agent's to recover by
    /// compacting, and no answer about images: a vision model must not be marked blind by it.
    /// </summary>
    [Fact]
    public async Task AnOverflowRefusal_IsNotTakenAsBlindness()
    {
        var handler = new AlwaysRefuses("""{"error":{"message":"This model's maximum context length is 128000 tokens"}}""");
        var provider = Unaskable(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.ChatAsync(WithAnImage(), null, CancellationToken.None));

        Assert.Single(handler.Bodies);
        Assert.Null(provider.KnownCanSeeImages);
    }

    /// <summary>
    /// A request refused again without its images was refused for something else: the first error is the
    /// one shown, and nothing is learned about images.
    /// </summary>
    [Fact]
    public async Task ARefusalThatOutlivesThePlaceholders_IsTheOriginalError_AndLearnsNothing()
    {
        var provider = Unaskable(new AlwaysRefuses("""{"error":"unsupported parameter: reasoning_effort"}"""));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.ChatAsync(WithAnImage(), null, CancellationToken.None));

        Assert.Contains("reasoning_effort", error.ToString());
        Assert.Null(provider.KnownCanSeeImages);
    }

    /// <summary>
    /// Images whose files were not kept go as notes already; a refusal of that request says nothing about
    /// images, and sending the same body again would only be refused again.
    /// </summary>
    [Fact]
    public async Task ARefusalOfARequestWithNoImageBytes_IsNotRetried()
    {
        var handler = new AlwaysRefuses("{}");
        var provider = Unaskable(handler);
        List<ChatMessage> gone =
        [
            new() { Role = "user", Content = "what is [Image #1 4×2]?", Images = [new AttachedImage("[Image #1 4×2]", "h", 4, 2)] },
        ];

        await Assert.ThrowsAnyAsync<Exception>(() => provider.ChatAsync(gone, null, CancellationToken.None));

        Assert.Single(handler.Bodies);
        Assert.Null(provider.KnownCanSeeImages);
    }

    /// <summary>
    /// A refusal learned while a probe is still out stays learned: the probe's answer, arriving after, is the
    /// endpoint's word on its route and not the newer evidence of a request it refused.
    /// </summary>
    [Fact]
    public async Task ALearnedAnswer_OutlivesAProbeStillOut()
    {
        var capability = new ImageCapability();
        var answer = new TaskCompletionSource<bool?>();
        var probing = capability.GetAsync(_ => answer.Task, CancellationToken.None);

        capability.Learn(false);
        answer.SetResult(true);
        await probing;

        Assert.False(capability.Known);
    }

    /// <summary>
    /// The endpoint's own word stays its own: after a refusal is learned, asking the endpoint still says
    /// what the endpoint says — nothing, for one with no route — so the Providers tab never shows a lesson
    /// as the server's answer.
    /// </summary>
    [Fact]
    public async Task AskingTheEndpoint_AfterARefusalWasLearned_IsStillTheEndpointsWord()
    {
        var provider = Unaskable(new RefusesImages());
        await provider.ChatAsync(WithAnImage(), null, CancellationToken.None);
        Assert.False(provider.KnownCanSeeImages);

        Assert.Null(await provider.AskEndpointAsync());
    }

    /// <summary>The turn says why the model was not shown the image, in the stream it is reading.</summary>
    [Fact]
    public async Task ARefusedImage_InAStream_SaysSo()
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in Unaskable(new StreamRefusesImages()).ChatStreamAsync(WithAnImage(), null, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Contains(chunks, c => c.Notice?.Contains("images: false") == true);
    }

    /// <summary>A model said to see images that refuses one has refused something else; that is its error.</summary>
    [Fact]
    public async Task ARefusalFromAModelKnownToSee_IsTheError()
    {
        var handler = new RefusesImages();

        await Assert.ThrowsAnyAsync<Exception>(() => Unaskable(handler, pin: true).ChatAsync(WithAnImage(), null, CancellationToken.None));
        Assert.Single(handler.Bodies);
    }

    private sealed class StreamRefusesImages : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/props")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            var body = await request.Content!.ReadAsStringAsync(ct);
            return body.Contains("image_url")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"),
                };
        }
    }

    [Fact]
    public async Task AMessageWithoutImages_NeverAsks()
    {
        var serves = new Serves(_ => Reply);

        await Llama(serves).ChatAsync([new ChatMessage { Role = "user", Content = "hi" }], null, CancellationToken.None);

        Assert.DoesNotContain("/props", serves.Asked);
    }

    [Fact]
    public async Task Ollama_AsksItsShowRoute_ForTheModel()
    {
        var serves = new Serves(r => r.RequestUri!.AbsolutePath == "/api/show" ? """{"capabilities":["completion","vision"]}""" : Reply);
        var provider = new OllamaProvider("o", "o", "llava", "http://h:11434/v1", client: new HttpClient(serves));

        Assert.True(await provider.CanSeeImagesAsync());
        Assert.Contains("\"model\":\"llava\"", serves.Bodies.Single());
    }

    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    private static string Config(string images) =>
        """{"providers":{"p":{"kind":"openai-compatible","model":"m","baseUrl":"http://h/v1","images":""" + images + """}},"defaultProvider":"p"}""";

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void TheConfig_ReadsImages(string value, bool images)
    {
        Assert.Equal(images, ProviderConfigLoader.Parse(Config(value), NoEnv).Providers["p"].Images);
    }

    [Fact]
    public void TheConfig_WithoutImages_AsksTheServer()
    {
        var json = """{"providers":{"p":{"kind":"openai-compatible","model":"m","baseUrl":"http://h/v1"}},"defaultProvider":"p"}""";

        Assert.Null(ProviderConfigLoader.Parse(json, NoEnv).Providers["p"].Images);
    }

    [Fact]
    public void TheConfig_RefusesImagesThatIsNotABoolean()
    {
        var ex = Assert.Throws<ProviderConfigException>(() => ProviderConfigLoader.Parse(Config("\"yes\""), NoEnv));

        Assert.Contains("provider 'p': 'images' must be true or false.", ex.Errors);
    }

    /// <summary>
    /// An endpoint that hangs on its probe is left for a minute before it is asked again: every request
    /// carrying an image would otherwise wait out the probe's whole bound first.
    /// </summary>
    [Fact]
    public async Task AProbeThatTimesOut_IsNotAskedAgainAtOnce()
    {
        var capability = new ImageCapability(TimeSpan.FromMilliseconds(20));
        var asked = 0;
        async Task<bool?> Hangs(CancellationToken ct) { asked++; await Task.Delay(Timeout.Infinite, ct); return true; }

        Assert.Null(await capability.GetAsync(Hangs, CancellationToken.None));
        Assert.Null(await capability.GetAsync(Hangs, CancellationToken.None));

        Assert.Equal(1, asked);
    }
}
