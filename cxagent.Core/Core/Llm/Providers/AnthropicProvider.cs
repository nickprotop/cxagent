using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm.Providers;

/// <summary>Native Anthropic Messages API driver. Normalizes stop_reason/usage at this boundary.</summary>
public class AnthropicProvider : ILlmProvider, IModelCatalog
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _client;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _maxTokens;
    private readonly string _baseUrl;
    private readonly RetryPolicy _retry;
    private readonly bool _cache;

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string ModelId => _model;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    /// <summary>Whether requests ask Anthropic to cache the prompt — <c>cacheControl</c> in config.</summary>
    public bool CacheControl => _cache;

    /// <summary>The shared client. NO TIMEOUT — cancellation is the bound, for the reasons set out on
    /// <see cref="OpenAiCompatibleProvider"/>'s copy of this field. Kept identical rather than
    /// left at .NET's 100-second default, so the two providers cannot behave differently under the
    /// same Escape.</summary>
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };


    /// <summary>
    /// The per-response output cap. It bounds what the model THINKS as well as what it writes:
    /// current Claude models think by default, and thinking counts against max_tokens, so a cap of a
    /// few thousand cuts a long answer — a sub-agent's final report — mid-sentence. Only generated
    /// tokens are billed, so a high cap costs nothing on a short reply; every request this driver
    /// sends that can be long is streamed, so it cannot hit an HTTP timeout either.
    /// </summary>
    public const int DefaultMaxTokens = 32_000;

    public AnthropicProvider(string providerId, string displayName, string model, string apiKey,
        int maxTokens = DefaultMaxTokens, string? baseUrl = null, HttpClient? client = null, RetryPolicy? retryPolicy = null,
        bool cacheControl = false)
    {
        _cache = cacheControl;
        ProviderId = providerId;
        DisplayName = displayName;
        _model = model;
        _apiKey = apiKey;
        _maxTokens = maxTokens;
        _baseUrl = (baseUrl ?? "https://api.anthropic.com/v1").TrimEnd('/');
        _client = client ?? Shared;
        _retry = retryPolicy ?? RetryPolicy.Default;
    }

    private HttpRequestMessage BuildRequest(string bodyJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        return req;
    }

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, CancellationToken ct)
    {
        var body = AnthropicWire.BuildRequestBody(_model, _maxTokens, messages, tools, stream: false, cache: _cache).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return AnthropicWire.ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = AnthropicWire.BuildRequestBody(_model, _maxTokens, messages, tools, stream: true, cache: _cache).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var acc = new AnthropicWire.StreamAccumulator();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0 || !line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();

            var item = acc.Accept(data);
            if (item.Thinking is not null)
                yield return new LlmStreamChunk(null, null, IsFinal: false, ThinkingDelta: item.Thinking,
                    ThinkingRedacted: item.Redacted);
            if (item.Text is not null || item.Call is not null)
                yield return new LlmStreamChunk(item.Text, item.Call, IsFinal: false);
            if (acc.IsDone) break;
        }
        foreach (var call in acc.Flush())
            yield return new LlmStreamChunk(null, call, IsFinal: false);

        // The stop reason and usage ride the final chunk, as they do for the OpenAI driver.
        yield return new LlmStreamChunk(null, null, IsFinal: true, acc.Usage, acc.StopReason);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

            using var resp = await _client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return data.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))!
                .ToList()!;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<string>(); }
    }
}
