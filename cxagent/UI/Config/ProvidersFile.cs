using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;

namespace CxAgent.UI.Config;

/// <summary>The file as the dialog reads it, or why it cannot be read.</summary>
/// <param name="Settings">What the loader made of it; null when it cannot be read.</param>
/// <param name="Errors">The loader's reasons, when it cannot.</param>
public sealed record ProvidersRead(ProviderSettings? Settings, IReadOnlyList<string> Errors)
{
    /// <summary>
    /// <c>webSearch.tavilyApiKey</c> exactly as the file has it — a <c>{env:NAME}</c> placeholder left
    /// a placeholder. The dialog edits this, not the loaded value: a draft built from the expanded key
    /// would write the secret a placeholder stood for back into the file on the next save.
    /// </summary>
    public string? TavilyKeyAsWritten { get; init; }
}

/// <summary>
/// Reads config.json for the configuration dialog.
///
/// <para>NO FILE, OR NO <c>providers</c> IN IT, IS A FIRST RUN, NOT A BROKEN FILE. The loader refuses
/// both, because a session cannot start without a provider — but the dialog is exactly where one is
/// added, and the theme picker writes a file holding only a theme before any provider exists. Every
/// other refusal is the loader's, shown as it gave it.</para>
/// </summary>
public static class ProvidersFile
{
    public static ProvidersRead Read(AppPaths paths, IReadOnlyDictionary<string, string> env)
    {
        var path = Path.Combine(paths.ConfigDir, "config.json");
        if (!File.Exists(path)) return new ProvidersRead(Empty(), []);

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProvidersRead(null, [$"config.json could not be read: {ex.Message}"]);
        }

        string? tavilyKey = null;
        try
        {
            if (JsonNode.Parse(text) is JsonObject root)
            {
                if (root["webSearch"] is JsonObject ws && ws["tavilyApiKey"] is JsonValue key
                    && key.TryGetValue<string>(out var written))
                    tavilyKey = written;
                if (root["providers"] is null)
                {
                    root["providers"] = new JsonObject();
                    text = root.ToJsonString();
                }
            }
        }
        catch (JsonException ex) { return new ProvidersRead(null, [$"config.json is not valid JSON: {ex.Message}"]); }

        try { return new ProvidersRead(ProviderConfigLoader.Parse(text, env, paths.ConfigDir), []) { TavilyKeyAsWritten = tavilyKey }; }
        catch (ProviderConfigException ex) { return new ProvidersRead(null, ex.Errors); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return new ProvidersRead(null, [$"config.json has a value of the wrong type: {ex.Message}"]);
        }
    }

    private static ProviderSettings Empty() => new(
        new Dictionary<string, ProviderInstanceConfig>(), null, [], new Dictionary<string, RoutingTarget>());
}
