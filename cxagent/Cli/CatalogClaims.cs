using System.Text.Json;

namespace CxAgent.Cli;

/// <summary>A tool as the entry lists it, gated normalised to "false", "true" or "dynamic".</summary>
internal sealed record ClaimedTool(string Name, string Gated);

/// <summary>One zip the entry says to download: its platform (null for a single source), where, its hash, the binary inside.</summary>
internal sealed record ClaimedDownload(string? Rid, string Url, string? Sha256, string File);

/// <summary>
/// What a catalog entry claims about a plugin — every field verify checks against the zips. Read from
/// the JSON itself rather than through CatalogEntry, which leaves out what an install never needs
/// (spawns, platforms) and keeps only this machine's download.
/// </summary>
internal sealed record CatalogClaims(string Name, string Version, string Kind, int PluginContract,
    IReadOnlyList<string> Platforms, bool Spawns, IReadOnlyList<ClaimedTool> Tools,
    IReadOnlyList<ClaimedDownload> Downloads, IReadOnlyDictionary<string, string> Text, bool FirstParty)
{
    private static readonly string[] TextFields = ["description", "publisher", "license", "repository"];

    /// <summary>The entries in <paramref name="json"/>: one entry object, or a <c>{"plugins": [...]}</c> catalog.</summary>
    public static IReadOnlyList<CatalogClaims>? Read(string json, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var entries = root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array
                ? plugins.EnumerateArray().ToList()
                : root.ValueKind == JsonValueKind.Object ? [root] : [];
            error = entries.Count == 0 ? "no entry: expected an entry object or a {\"plugins\": [...]} catalog." : null;
            return error is null ? entries.Select(Of).ToList() : null;
        }
        catch (JsonException ex)
        {
            error = $"not JSON: {ex.Message}";
            return null;
        }
    }

    private static CatalogClaims Of(JsonElement e)
    {
        var compat = e.TryGetProperty("compatibility", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var file = Str(e, "file") ?? "";

        var downloads = new List<ClaimedDownload>();
        if (e.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Object)
            foreach (var rid in sources.EnumerateObject())
                downloads.Add(new ClaimedDownload(rid.Name, UrlOf(rid.Value) ?? "", Str(rid.Value, "sha256"), Str(rid.Value, "file") ?? file));
        else if (e.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            downloads.Add(new ClaimedDownload(null, UrlOf(source) ?? "", Str(source, "sha256"), file));

        return new CatalogClaims(
            Str(e, "name") ?? "",
            Str(e, "version") ?? "",
            Str(e, "kind") ?? "",
            compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("pluginContract", out var pc) && pc.TryGetInt32(out var n) ? n : 0,
            compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("platforms", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? [.. ps.EnumerateArray().Select(p => p.GetString() ?? "")]
                : [],
            e.TryGetProperty("spawns", out var sp) && sp.ValueKind == JsonValueKind.True,
            e.TryGetProperty("tools", out var ts) && ts.ValueKind == JsonValueKind.Array
                ? [.. ts.EnumerateArray().Select(t => new ClaimedTool(Str(t, "name") ?? "", GatedOf(t)))]
                : [],
            downloads,
            TextFields.ToDictionary(f => f, f => Str(e, f) ?? ""),
            e.TryGetProperty("directory", out _));
    }

    // A RELEASE SOURCE NAMES ITS FILE BY "latest", as CatalogReader.DownloadOf reads it; a url source by "url".
    private static string? UrlOf(JsonElement source) => Str(source, "latest") ?? Str(source, "url");

    private static string GatedOf(JsonElement tool) =>
        !tool.TryGetProperty("gated", out var g) ? "false"
        : g.ValueKind == JsonValueKind.True ? "true"
        : g.ValueKind == JsonValueKind.String ? g.GetString() ?? "false"
        : "false";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
