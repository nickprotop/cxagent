using System.Text.Json;

namespace CxAgent.Core.Llm;

/// <summary>
/// A provider that can say whether its model sees images.
///
/// <para>THREE ANSWERS, AND UNKNOWN IS NOT NO. Null means nobody could say — an endpoint without a route
/// that tells, a probe that failed — and an image is then sent, so the endpoint answers for itself. A
/// wrong guess is not one failed turn: an endpoint that cannot see refuses the whole request, and keeps
/// refusing every later turn the image stays in the context for, until <c>images: false</c> is set on
/// the entry. False sends each image's placeholder instead, so the conversation goes on.</para>
/// </summary>
public interface IImageCapability
{
    /// <summary>The answer once known, or null while nothing has said.</summary>
    bool? KnownCanSeeImages { get; }

    /// <summary>The configuration's word, else the endpoint's, asked once and kept once it answers.</summary>
    /// <param name="ct">Cancels the wait.</param>
    Task<bool?> CanSeeImagesAsync(CancellationToken ct = default);
}

/// <summary>
/// What each kind of endpoint says about images, and the one probe a provider keeps.
/// </summary>
/// <param name="timeout">How long a probe may take; three seconds unless a test says.</param>
/// <param name="clock">The time; the system clock unless a test sets one.</param>
internal sealed class ImageCapability(TimeSpan? timeout = null, TimeProvider? clock = null)
{
    /// <summary>How long a probe may take before the image goes unasked.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _askAgainAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _answered;
    private bool? _known;

    /// <summary>The answer once the endpoint gave one.</summary>
    public bool? Known => Volatile.Read(ref _answered) ? _known : null;

    /// <summary>
    /// The endpoint's answer, asking once. A probe that fails is not kept, so the next image asks again; a
    /// reply that says nothing is kept as "unknown", since asking again would hear the same.
    ///
    /// <para>A TIMEOUT WAITS A MINUTE before it is asked again, as <see cref="CapabilityCache"/> does for
    /// thinking: every request carrying an image waits on this, and an endpoint that hangs on its probe
    /// route would add the whole bound to each of them.</para>
    /// </summary>
    /// <param name="probe">Asks the endpoint; throws or times out when it cannot answer.</param>
    /// <param name="ct">Cancels the wait.</param>
    public async Task<bool?> GetAsync(Func<CancellationToken, Task<bool?>> probe, CancellationToken ct)
    {
        if (Volatile.Read(ref _answered)) return _known;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_answered) return _known;
            if (_clock.GetUtcNow() < _askAgainAt) return null;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(_timeout);
            try
            {
                _known = await probe(bounded.Token).ConfigureAwait(false);
                Volatile.Write(ref _answered, true);
                return _known;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                _askAgainAt = _clock.GetUtcNow() + CapabilityCache.AfterATimeout;
                return null;
            }
            catch (Exception) { return null; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// llama.cpp's <c>/props</c>: <c>modalities.vision</c>, true only when the server loaded a vision
    /// projector (<c>--mmproj</c>). Measured on a Qwen served without one: <c>{"vision": false}</c>.
    /// </summary>
    /// <param name="json">The response body.</param>
    public static bool? ParseLlamaProps(string json) => Read(json, root =>
        root.TryGetProperty("modalities", out var m) && m.TryGetProperty("vision", out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null);

    /// <summary>Ollama's <c>/api/show</c>: <c>"vision"</c> among the model's <c>capabilities</c>.</summary>
    /// <param name="json">The response body.</param>
    public static bool? ParseOllamaShow(string json) => Read(json, root =>
        root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == "vision")
            : null);

    /// <summary>
    /// OpenRouter's <c>/models</c>: <c>"image"</c> among this model's <c>architecture.input_modalities</c>.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="model">The model's id, as the list names it.</param>
    public static bool? ParseOpenRouterModels(string json, string? model) => Read(json, root =>
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var id) || id.GetString() != model) continue;
            return m.TryGetProperty("architecture", out var a) && a.TryGetProperty("input_modalities", out var inputs)
                && inputs.ValueKind == JsonValueKind.Array
                ? inputs.EnumerateArray().Any(e => e.GetString() == "image")
                : null;
        }
        return null;
    });

    private static bool? Read(string json, Func<JsonElement, bool?> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether a request carries any image, so a conversation without one never asks.</summary>
    public static bool AnyImages(IEnumerable<Models.ChatMessage> messages) =>
        messages.Any(m => m.Images is { Count: > 0 });
}
