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

    /// <summary>
    /// The value at <paramref name="key"/>, or <paramref name="defaultValue"/> when it is absent —
    /// or PRESENT AND NULL.
    ///
    /// <para>A model that has nothing to say for an optional argument frequently emits
    /// <c>"pattern": null</c> rather than omitting the key. TryGetValue then succeeds, so the
    /// default was never applied and callers got a null where they had asked for <c>"*"</c>:
    /// <c>list_files {"path":"/src","pattern":null}</c> reached NormalizeGlob and threw a
    /// NullReferenceException, which reaches the model as "Object reference not set to an instance
    /// of an object" — a message it cannot act on.</para>
    /// </summary>
    public T Get<T>(string key, T defaultValue)
    {
        if (!Values.TryGetValue(key, out var v)) return defaultValue;
        if (v is null || (v is JsonElement { ValueKind: JsonValueKind.Null })) return defaultValue;
        return Convert<T>(v);
    }

    /// <summary>
    /// Converts an untyped value to <typeparamref name="T"/>, TOLERATING the type slips an LLM
    /// actually makes.
    ///
    /// <para>This was <c>e.Deserialize&lt;T&gt;()</c>, which is a STRICT reader, not a converting
    /// one. It is right for the SQLite round-trip (where types were correct when written) and wrong
    /// for the LLM (where they were never guaranteed). Models routinely stringify scalars —
    /// <c>"timeout_seconds": "30"</c>, <c>"regex": "true"</c> — and each one threw a JsonException
    /// whose message names a JSON PATH (<c>Path: $ | LineNumber: 0</c>) rather than the parameter.
    /// The model is told something failed but not which argument to change, so it retries the same
    /// shape. That is the same failure as the <c>**/*.cs</c> glob bug: a correctable mistake
    /// reported as an incomprehensible one.</para>
    ///
    /// <para>Only cross-kind scalar coercion is added. A JSON object asked for as a string is still
    /// an error — it is a genuinely different intent, not a slip — and it now reports as one.</para>
    /// </summary>
    private static T Convert<T>(object? v)
    {
        switch (v)
        {
            case T t: return t;
            case null: return default!;
            case JsonElement e: return FromJson<T>(e);
            case JsonNode n: return FromJson<T>(n.Deserialize<JsonElement>());
            default: return (T)System.Convert.ChangeType(v, typeof(T));
        }
    }

    private static T FromJson<T>(JsonElement e)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        // A string holding a scalar: parse it as that scalar rather than refusing it.
        if (e.ValueKind == JsonValueKind.String && target != typeof(string))
        {
            var raw = e.GetString();
            if (raw is null) return default!;
            if (target == typeof(int) && int.TryParse(raw, out var i)) return (T)(object)i;
            if (target == typeof(long) && long.TryParse(raw, out var l)) return (T)(object)l;
            if (target == typeof(double) && double.TryParse(raw, out var d)) return (T)(object)d;
            if (target == typeof(bool) && bool.TryParse(raw, out var b)) return (T)(object)b;
        }

        // A number or bool asked for as a string: render it back to its JSON text. `"limit": 10`
        // against a string param is the mirror image of the case above and just as recoverable.
        if (target == typeof(string) &&
            e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return (T)(object)e.GetRawText();

        // A number where a bool belongs (0/1) — common from models that learned SQL or C.
        if (target == typeof(bool) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n))
            return (T)(object)(n != 0);

        return e.Deserialize<T>()!;
    }
}
