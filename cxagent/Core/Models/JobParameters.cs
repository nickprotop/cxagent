using System.Text.Json;
using System.Text.Json.Nodes;

namespace CxAgent.Core.Models;

/// <summary>
/// Plugin-specific job parameters. Values arrive from two untyped sources — the
/// LLM's create_plan args and parameters_json from SQLite — so after a round-trip
/// an int is a JsonElement, not an int. Get&lt;T&gt; MUST convert, never blind-cast:
/// a raw (T)Values[key] throws InvalidCastException on every reload.
/// </summary>
public record JobParameters(Dictionary<string, object?> Values)
{
    public JobParameters() : this(new Dictionary<string, object?>()) { }

    public T Get<T>(string key) => Convert<T>(Values[key]);

    public T Get<T>(string key, T defaultValue) =>
        Values.TryGetValue(key, out var v) ? Convert<T>(v) : defaultValue;

    private static T Convert<T>(object? v) => v switch
    {
        T t => t,
        JsonElement e => e.Deserialize<T>()!,
        JsonNode n => n.Deserialize<T>()!,
        null => default!,
        _ => (T)System.Convert.ChangeType(v, typeof(T))
    };
}
