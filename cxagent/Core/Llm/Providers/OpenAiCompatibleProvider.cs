using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm.Providers;

/// <summary>
/// OpenAI Chat Completions wire driver. Its configurable baseUrl covers OpenAI, OpenRouter, Groq,
/// Together, vLLM, llama.cpp, LM Studio and any other OpenAI-compatible endpoint. Normalizes
/// StopReason/Usage at this boundary; the core never sees a raw vendor value.
/// </summary>
public class OpenAiCompatibleProvider : ILlmProvider, IModelCatalog
{
    private readonly HttpClient _client;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly IReadOnlyDictionary<string, string>? _extraHeaders;
    private readonly RetryPolicy _retry;

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string ModelId => _model;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    protected static readonly HttpClient Shared = new();

    /// <summary>
    /// The injected HttpClient, or null when this instance fell back to <see cref="Shared"/>.
    /// Clones re-pass the original argument so a clone never silently swaps out a test's fake handler.
    /// </summary>
    private readonly HttpClient? _injectedClient;
    private readonly RetryPolicy? _injectedRetry;

    public OpenAiCompatibleProvider(string providerId, string displayName, string model,
        string baseUrl, string? apiKey, IReadOnlyDictionary<string, string>? extraHeaders = null,
        HttpClient? client = null, RetryPolicy? retryPolicy = null)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _extraHeaders = extraHeaders;
        _client = client ?? Shared;
        _retry = retryPolicy ?? RetryPolicy.Default;
        _injectedClient = client;
        _injectedRetry = retryPolicy;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Virtual so <see cref="OllamaProvider"/> can return its own type — a base-typed clone would
    /// silently lose Ollama's native /api/tags model enumeration.
    /// </remarks>
    public virtual ILlmProvider WithModel(string model) =>
        new OpenAiCompatibleProvider(ProviderId, DisplayName, model, _baseUrl, _apiKey,
            _extraHeaders, _injectedClient, _injectedRetry);

    private HttpRequestMessage BuildRequest(string bodyJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (_extraHeaders is not null)
            foreach (var h in _extraHeaders) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return req;
    }

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, CancellationToken ct)
    {
        var body = OpenAiWire.BuildRequestBody(_model, messages, tools, stream: false).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return OpenAiWire.ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = OpenAiWire.BuildRequestBody(_model, messages, tools, stream: true).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? finish = null;
        string? line;
        LlmUsage? usage = null;
        // A streamed tool call's `arguments` arrives as fragments across many chunks (a real capture
        // had 103 for one create_plan), so it must be reassembled here rather than parsed per-chunk.
        var toolAcc = new OpenAiWire.StreamingToolCallAccumulator();
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0 || !line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            // The stream_options.include_usage opt-in appends a final chunk with an EMPTY choices array
            // and a populated usage object — check for it before the normal choices[0] delta parse.
            if (OpenAiWire.TryParseUsageOnlyChunk(data, out var chunkUsage))
            {
                usage = chunkUsage;
                continue;
            }

            var (textDelta, chunkFinish, delta) = OpenAiWire.ParseStreamDelta(data);
            if (chunkFinish is not null) finish = chunkFinish;

            // Returns EVERY completed call on the terminating chunk, each with its own arguments
            // joined. A turn may carry several; yielding only the first is how the rest were lost.
            var completedCalls = toolAcc.Accept(delta, chunkFinish);

            if (textDelta is not null && completedCalls.Count == 0)
                yield return new LlmStreamChunk(textDelta, null, IsFinal: false);

            for (var i = 0; i < completedCalls.Count; i++)
                yield return new LlmStreamChunk(
                    i == 0 ? textDelta : null, completedCalls[i], IsFinal: false);
        }
        yield return new LlmStreamChunk(null, null, IsFinal: true, usage);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (_extraHeaders is not null)
                foreach (var h in _extraHeaders) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

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
