using System.Text.Json.Nodes;

namespace CxAgent.Core.Config;

/// <summary>
/// The per-instance keys the loader reads, as an edit: a null member is LEFT AS IT IS, and a key named
/// in <see cref="Clear"/> is removed. Two kinds of "nothing" because they are two different requests —
/// "I did not touch the key" and "take the key away" — and a single null cannot carry both.
/// </summary>
public sealed record InstanceFields
{
    public string? Kind { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxConcurrentAgents { get; init; }
    public bool? CacheControl { get; init; }
    public string? ThinkingDisplay { get; init; }
    public string? Thinking { get; init; }
    public bool? Reasoning { get; init; }

    /// <summary>JSON key names to remove from the instance.</summary>
    public IReadOnlySet<string> Clear { get; init; } = new HashSet<string>();

    /// <summary>Writes the set members onto <paramref name="instance"/>, then removes <see cref="Clear"/>.</summary>
    internal void ApplyTo(JsonObject instance)
    {
        Set(instance, "kind", Kind);
        Set(instance, "model", Model);
        Set(instance, "apiKey", ApiKey);
        Set(instance, "baseUrl", BaseUrl);
        Set(instance, "description", Description);
        if (ExtraHeaders is { } headers)
            instance["extraHeaders"] = new JsonObject(headers.Select(kv =>
                KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
        if (ContextWindow is { } window) instance["contextWindow"] = window;
        if (MaxConcurrentAgents is { } max) instance["maxConcurrentAgents"] = max;
        if (CacheControl is { } cache) instance["cacheControl"] = cache;
        Set(instance, "thinkingDisplay", ThinkingDisplay);
        Set(instance, "thinking", Thinking);
        if (Reasoning is { } reasoning) instance["reasoning"] = reasoning;
        foreach (var key in Clear) instance.Remove(key);
    }

    private static void Set(JsonObject o, string key, string? value)
    {
        if (value is not null) o[key] = value;
    }
}
