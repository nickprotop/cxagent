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

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string ModelId => _model;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    /// <summary>The shared client. NO TIMEOUT — cancellation is the bound, for the reasons set out on
    /// <see cref="OpenAiCompatibleProvider"/>'s copy of this field. Kept identical rather than
    /// left at .NET's 100-second default, so the two providers cannot behave differently under the
    /// same Escape.</summary>
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };


    public AnthropicProvider(string providerId, string displayName, string model, string apiKey,
        int maxTokens = 4096, string? baseUrl = null, HttpClient? client = null, RetryPolicy? retryPolicy = null)
    {
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
        var body = AnthropicWire.BuildRequestBody(_model, _maxTokens, messages, tools, stream: false).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return AnthropicWire.ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = AnthropicWire.BuildRequestBody(_model, _maxTokens, messages, tools, stream: true).ToJsonString();
        using var resp = await LlmHttpRetry.SendWithRetryAsync(
            _client, () => BuildRequest(body), ProviderId, _retry, ct);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        bool emittedFinal = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0 || !line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();

            var (textDelta, toolCall, isFinal) = AnthropicWire.ParseStreamEvent(data);
            if (textDelta is not null || toolCall is not null)
                yield return new LlmStreamChunk(textDelta, toolCall, IsFinal: false);
            if (isFinal)
            {
                emittedFinal = true;
                yield return new LlmStreamChunk(null, null, IsFinal: true);
                break;
            }
        }
        if (!emittedFinal)
            yield return new LlmStreamChunk(null, null, IsFinal: true);
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
