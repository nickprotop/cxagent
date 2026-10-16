namespace CxAgent.Core.Llm;

/// <summary>
/// OPTIONAL provider capability: enumerate the models this endpoint offers.
///
/// Deliberately NOT part of ILlmProvider — the HAL stays minimal (chat + stream), and a driver that
/// cannot enumerate simply doesn't implement this. The wizard does `provider is IModelCatalog cat`
/// and shows a dropdown when it can, a free-text field when it can't.
///
/// Contract: NEVER throws. A network/parse failure returns an empty list, because failing to list
/// models must degrade the wizard to typing a model id, not abort provider setup.
/// </summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}

/// <summary>One model an endpoint serves, with what the endpoint says about it.</summary>
/// <param name="Id">The model id a request names.</param>
/// <param name="ContextWindow">Its input window in tokens, when the endpoint says.</param>
/// <param name="Price">Input/output price per million tokens, when the endpoint says — OpenRouter does.</param>
public sealed record ModelInfo(string Id, int? ContextWindow = null, string? Price = null);

/// <summary>An endpoint's models, or why it gave none.</summary>
/// <param name="Models">What it serves; empty when it could not be asked.</param>
/// <param name="Error">Why the list is empty — a refused key, a status, an unreachable host — or null.</param>
public sealed record ModelCatalogResult(IReadOnlyList<ModelInfo> Models, string? Error = null);

/// <summary>
/// OPTIONAL: the models with what the endpoint knows about each, and the reason when it could not be
/// asked. A sibling of <see cref="IModelCatalog"/> rather than a change to it: that contract's empty
/// list on failure is what a caller that only wants ids relies on, while a caller checking whether an
/// endpoint works needs to tell "refused" from "serves nothing".
///
/// <para>NEVER THROWS either, except to cancel: a failure is <see cref="ModelCatalogResult.Error"/>.</para>
/// </summary>
public interface IModelCatalogDetail
{
    Task<ModelCatalogResult> ListModelDetailsAsync(CancellationToken ct);
}

/// <summary>Reading a <c>/models</c> answer into <see cref="ModelInfo"/> rows.</summary>
public static class ModelCatalogParse
{
    /// <summary>
    /// The <c>data</c> array of an OpenAI-shaped or Anthropic <c>/models</c> answer. Reads the window
    /// from OpenRouter's <c>context_length</c> or Anthropic's <c>max_input_tokens</c>, and OpenRouter's
    /// per-token <c>pricing</c> as dollars per million.
    /// </summary>
    public static IReadOnlyList<ModelInfo> Data(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];
        var models = new List<ModelInfo>();
        foreach (var e in data.EnumerateArray())
        {
            if (!e.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } name) continue;
            int? window = Int(e, "context_length") ?? Int(e, "max_input_tokens");
            models.Add(new ModelInfo(name, window, Price(e)));
        }
        return models;
    }

    private static int? Int(System.Text.Json.JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0
            ? n : null;

    /// <summary>"$3/$15 per M" from per-token strings; null when either side is missing or free-form.</summary>
    private static string? Price(System.Text.Json.JsonElement e)
    {
        if (!e.TryGetProperty("pricing", out var p) || p.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        static decimal? PerMillion(System.Text.Json.JsonElement p, string key) =>
            p.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d * 1_000_000m : null;
        if (PerMillion(p, "prompt") is not { } input || PerMillion(p, "completion") is not { } output) return null;
        static string Money(decimal d) => "$" + d.ToString(d == Math.Round(d) ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"{Money(input)}/{Money(output)} per M";
    }
}
