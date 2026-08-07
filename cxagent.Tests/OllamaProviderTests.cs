using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class OllamaProviderTests : IDisposable
{
    private readonly LoopbackServer _srv = new();
    public void Dispose() => _srv.Dispose();

    private static List<ChatMessage> Msgs() => new()
    {
        new ChatMessage { Role = "user", Content = "hi" },
    };

    [Fact]
    public void DefaultsToLocalhostAndIsKeyless()
    {
        var p = new OllamaProvider("ollama", "Ollama llama3", "llama3.3");
        Assert.Equal("ollama", p.ProviderId);
        Assert.True(p.SupportsStreaming);
        Assert.IsAssignableFrom<OpenAiCompatibleProvider>(p);   // genuine subclass, not a wrapper
    }

    [Fact]
    public async Task ChatAsync_WorksAgainstOpenAiWire_NoAuthHeader()
    {
        _srv.EnqueueJson(200, """{"choices":[{"message":{"content":"pong"},"finish_reason":"stop"}]}""");
        var p = new OllamaProvider("ollama", "Ollama llama3", "llama3.3",
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay);
        var r = await p.ChatAsync(Msgs(), null, CancellationToken.None);
        Assert.Equal("pong", r.Text);
        // keyless: no Authorization header sent.
        Assert.False(_srv.LastRequestHeaders.ContainsKey("Authorization"));
    }
}
