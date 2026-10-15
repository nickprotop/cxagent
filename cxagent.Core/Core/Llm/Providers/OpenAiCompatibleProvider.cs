using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm.Providers;

/// <summary>
/// OpenAI Chat Completions wire driver. Its configurable baseUrl covers OpenAI, OpenRouter, Groq,
/// Together, vLLM, llama.cpp, LM Studio and any other OpenAI-compatible endpoint. Normalizes
/// StopReason/Usage at this boundary; the core never sees a raw vendor value.
/// </summary>
public class OpenAiCompatibleProvider : ILlmProvider, IModelCatalog, IThinkingProvider
{
    private readonly HttpClient _client;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly IReadOnlyDictionary<string, string>? _extraHeaders;
    private readonly RetryPolicy _retry;
    /// <summary>See OpenAiProviderOptions.CacheControl. Read by ChatAsync and ChatStreamAsync when
    /// they build a request body.</summary>
    private readonly bool _cacheControl;
    /// <summary>Kept whole so <see cref="WithThinking"/> can clone with one field changed.</summary>
    private readonly OpenAiProviderOptions _options;
    /// <summary>Shared with every clone: the server's answer does not depend on the level.</summary>
    private readonly CapabilityCache _caps;

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string ModelId => _model;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    /// <summary>
    /// The shared client. NO TIMEOUT — the cancellation token is the bound.
    ///
    /// <para>A bare <c>new HttpClient()</c> carries .NET's 100-second default, which was never a
    /// decision anyone made here. It does not bound a long generation: every request goes out with
    /// <see cref="System.Net.Http.HttpCompletionOption.ResponseHeadersRead"/>, so the clock covers
    /// only time-to-first-byte and stops once headers arrive. What it DOES kill is a request still
    /// waiting its turn — and that is exactly the case sub-agents create, since a single-slot local
    /// endpoint queues concurrent children at the socket while their clocks run.</para>
    ///
    /// <para>Worse, it is INDISTINGUISHABLE FROM ESCAPE. A client timeout raises
    /// <c>TaskCanceledException</c>, which is an <c>OperationCanceledException</c>, so a child killed
    /// by a busy endpoint reports "cancelled" and the user reads it as their own keypress.</para>
    ///
    /// <para>Cancellation already covers the hang it was nominally there for: the token reaches
    /// <c>SendAsync</c>, <c>ReadAsStreamAsync</c> and every <c>ReadLineAsync</c> of the stream loop,
    /// so Escape cuts a wedged server at any point. A human who can see a stuck row is a better judge
    /// of "this is not working" than a fixed number that cannot tell waiting from working.</para>
    /// </summary>
    protected static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// The injected HttpClient, or null when this instance fell back to <see cref="Shared"/>.
    /// Clones re-pass the original argument so a clone never silently swaps out a test's fake handler.
    /// </summary>

    public OpenAiCompatibleProvider(OpenAiProviderOptions options) : this(options, new CapabilityCache()) { }

    private OpenAiCompatibleProvider(OpenAiProviderOptions options, CapabilityCache caps)
    {
        _options = options;
        _caps = caps;
        ProviderId = options.ProviderId;
        DisplayName = options.DisplayName;
        _model = options.Model;
        _baseUrl = options.BaseUrl.TrimEnd('/');
        _apiKey = options.ApiKey;
        _extraHeaders = options.ExtraHeaders;
        _client = options.Client ?? Shared;
        _retry = options.Retry ?? RetryPolicy.Default;
        _cacheControl = options.CacheControl;
    }

    public string? ThinkingLevel => _options.Thinking;
    public ThinkingOptions? KnownThinkingOptions => _caps.Known;
    public ILlmProvider WithThinking(string? level) =>
        new OpenAiCompatibleProvider(_options with { Thinking = level }, _caps);

    /// <summary>
    /// What this model offers, asked in the endpoint's own way: llama.cpp's <c>/props</c>,
    /// OpenRouter's model list, or — for OpenAI, which cannot be asked — the configuration's word.
    /// </summary>
    /// <param name="ct">Cancels the probe.</param>
    public Task<ThinkingOptions> GetThinkingOptionsAsync(CancellationToken ct = default) =>
        _options.Dialect switch
        {
            ReasoningDialect.OpenAi => Task.FromResult(_options.Reasoning
                ? new ThinkingOptions([ThinkingLevels.Minimal, ThinkingLevels.Low, ThinkingLevels.Medium, ThinkingLevels.High])
                : ThinkingOptions.None("OpenAI cannot say which models reason — set \"reasoning\": true on this instance if this one does")),
            ReasoningDialect.OpenRouter => _caps.GetAsync(c => ProbeAsync("/models", ThinkingCapabilities.ParseOpenRouterModels, c), ct),
            _ => _caps.GetAsync(c => ProbeAsync("/props", (json, _) => ThinkingCapabilities.ParseLlamaProps(json), c), ct),
        };

    /// <summary>
    /// One bounded GET for what the endpoint takes. llama.cpp serves <c>/props</c> at the server
    /// root, beside — not under — <c>/v1</c>; OpenRouter's list sits under its API base.
    /// </summary>
    private async Task<ThinkingOptions?> ProbeAsync(string path, Func<string, string?, ThinkingOptions?> parse,
        CancellationToken c)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(c);
        cts.CancelAfter(ThinkingCapabilities.Timeout);
        var root = path == "/props" && _baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? _baseUrl[..^3] : _baseUrl;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}{path}");
        Authorize(req);
        using var resp = await _client.SendAsync(req, cts.Token).ConfigureAwait(false);
        // A reply is an answer even when it says nothing useful: a server without the route (Ollama,
        // most gateways) is cached as "none" and asked once. A temporary refusal — a llama.cpp
        // server still loading its model — an exception or a timeout is asked again.
        if (ThinkingCapabilities.IsTemporary(resp.StatusCode)) return null;
        var unsaid = ThinkingOptions.None("the endpoint did not say what it accepts");
        return resp.IsSuccessStatusCode
            ? parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), _model) ?? unsaid
            : unsaid;
    }

    /// <summary>
    /// The fields that set the level, in this endpoint's dialect — or null, sending nothing, unless
    /// a level is set AND the endpoint said it takes that level: an unknown field is a 400 on strict
    /// servers, so a guess is never sent.
    /// </summary>
    private async Task<JsonObject?> ThinkingFieldsAsync(CancellationToken ct)
    {
        if (_options.Thinking is not { } level) return null;
        var options = await GetThinkingOptionsAsync(ct);
        if (!options.Any) return null;
        if (level == ThinkingLevels.Lowest) level = options.Levels[0];
        if (!options.Levels.Contains(level)) return null;

        return _options.Dialect switch
        {
            ReasoningDialect.OpenAi => new JsonObject { ["reasoning_effort"] = level },
            ReasoningDialect.OpenRouter => new JsonObject
            {
                ["reasoning"] = level switch
                {
                    ThinkingLevels.Off => new JsonObject { ["effort"] = "none" },
                    ThinkingLevels.On => new JsonObject { ["enabled"] = true },
                    _ => new JsonObject { ["effort"] = level },
                },
            },
            _ => level switch
            {
                ThinkingLevels.Off => new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } },
                ThinkingLevels.On => new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true } },
                _ => null,
            },
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Virtual so <see cref="OllamaProvider"/> can return its own type — a base-typed clone would
    /// silently lose Ollama's native /api/tags model enumeration.
    /// </remarks>
    private HttpRequestMessage BuildRequest(string bodyJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        Authorize(req);
        return req;
    }

    /// <summary>
    /// The key and the configured headers, on every request this provider sends — the levels probe
    /// as much as a chat. ONE PLACE because a request that misses them is refused by a gateway that
    /// admits or routes by header — and a refused probe leaves the model offering no levels.
    /// </summary>
    private void Authorize(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (_extraHeaders is not null)
            foreach (var h in _extraHeaders) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, CancellationToken ct)
    {
        var body = OpenAiWire.BuildRequestBody(_model, messages, tools, stream: false,
            cacheSystemPrompt: _cacheControl, thinking: await ThinkingFieldsAsync(ct)).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return OpenAiWire.ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = OpenAiWire.BuildRequestBody(_model, messages, tools, stream: true,
            cacheSystemPrompt: _cacheControl, thinking: await ThinkingFieldsAsync(ct)).ToJsonString();
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

            var d = OpenAiWire.ParseStreamDelta(data);
            if (d.Finish is not null) finish = d.Finish;

            // Returns EVERY completed call on the terminating chunk, each with its own arguments
            // joined. A turn may carry several; yielding only the first is how the rest were lost.
            var completedCalls = toolAcc.Accept(d.Delta, d.Finish);

            // THINKING FIRST: a server that sends reasoning and content in one delta reasoned before it wrote.
            if (d.Thinking is not null)
                yield return new LlmStreamChunk(null, null, IsFinal: false, ThinkingDelta: d.Thinking);

            if (d.Text is not null && completedCalls.Count == 0)
                yield return new LlmStreamChunk(d.Text, null, IsFinal: false);

            for (var i = 0; i < completedCalls.Count; i++)
                yield return new LlmStreamChunk(
                    i == 0 ? d.Text : null, completedCalls[i], IsFinal: false);
        }
        // The normalized stop reason rides the final chunk so the agent loop can AND it with its
        // own tool-call check. Trusting either alone is a known failure: some servers report "stop"
        // on a turn that carried tool calls.
        yield return new LlmStreamChunk(null, null, IsFinal: true, usage,
            OpenAiWire.NormalizeStopReason(finish));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            Authorize(req);

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
