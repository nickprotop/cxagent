using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

namespace CxAgent.Web;

/// <summary>What a browser's config save came to.</summary>
/// <param name="Saved">Whether the file was written.</param>
/// <param name="Errors">Why not, in the loader's words, or what kept it from going live.</param>
internal sealed record WebConfigSave(bool Saved, IReadOnlyList<string> Errors);

/// <summary>
/// config.json for a browser with <c>web.allow.config</c>: the file as text, without what a browser must not see or
/// change, and a save judged by the loader startup runs, then made live as the terminal's F8 makes its saves live.
///
/// <para>THE FILE, NOT A FORM PER TAB. F8 is six tabs of forms; the file is what they all write, and editing it here
/// reaches every setting they reach, judged by the same loader before a byte is written.</para>
///
/// <para>SECRETS STAY ON THE SERVER. A key, token or password is shown as its last four characters; a hidden value
/// sent back unchanged is written back as it was, so a page never holds a key it did not type.</para>
///
/// <para>THE BROWSER'S OWN LIMITS ARE NOT IN IT: <c>web.allow</c>, <c>web.users</c> and <c>web.roots</c> are left out of
/// what is shown and put back from the file on save, whatever the page sent — a login must not widen its own access.</para>
/// </summary>
internal sealed class WebConfig(AppPaths paths, SessionManager manager, Action<ResolvedConfig>? saved)
{
    // CHARACTERS AS THEY ARE: the default writer escapes every non-ASCII one, so a description's apostrophe and a
    // hidden secret's marker read as \u0027 and \u00B7 on the page. This is shown to a person, never put in markup.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The keys under <c>web</c> only the terminal changes.</summary>
    private static readonly string[] Protected = ["allow", "users", "roots"];

    // A NAME THAT HOLDS A SECRET: apiKey, tavilyApiKey, token, password, Authorization, client_secret …
    private static readonly Regex SecretName = new("(api[_-]?key|token|secret|password|authorization)$", RegexOptions.IgnoreCase);

    private string ConfigPath => Path.Combine(paths.ConfigDir, "config.json");

    // THE SECRETS THE PAGE WAS SHOWN, by the placeholder it was shown: a value sent back as its placeholder is this.
    private readonly Dictionary<string, string> _hidden = new(StringComparer.Ordinal);

    /// <summary>The file as a browser may see it, or null with why it cannot be read.</summary>
    public string? Read(out string? why)
    {
        why = null;
        JsonObject root;
        try
        {
            root = File.Exists(ConfigPath) ? JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            why = $"config.json could not be read: {e.Message}";
            return null;
        }
        if (root["web"] is JsonObject web)
        {
            foreach (var key in Protected) web.Remove(key);
            if (web.Count == 0) root.Remove("web");
        }
        lock (_hidden)
        {
            _hidden.Clear();
            Hide(root);
        }
        return root.ToJsonString(Indented);
    }

    private void Hide(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && SecretName.IsMatch(name) && !IsReference(text))
                    {
                        var shown = $"(hidden · …{(text.Length > 4 ? text[^4..] : "")})";
                        _hidden[shown] = text;
                        o[name] = shown;
                    }
                    else Hide(value);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Hide(item);
                break;
        }
    }

    // A PLACEHOLDER FOR A SECRET IS NOT ONE: {env:NAME} and $NAME say where the key comes from, which is worth seeing.
    private static bool IsReference(string text) =>
        text.Length == 0 || text.StartsWith("{env:", StringComparison.Ordinal) || text.StartsWith('$');

    private void Unhide(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && _hidden.TryGetValue(text, out var real)) o[name] = real;
                    else Unhide(value);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var text) && _hidden.TryGetValue(text, out var real)) a[i] = real;
                    else Unhide(a[i]);
                }
                break;
        }
    }

    /// <summary>
    /// The live sessions <paramref name="text"/> would move to a different model or endpoint — the move that re-sends a
    /// conversation, which F8 asks about before it saves. Empty when none would; null when the text is not JSON.
    /// </summary>
    public IReadOnlyList<string>? Moved(string text)
    {
        JsonObject sent, now;
        try
        {
            sent = JsonNode.Parse(text) as JsonObject ?? [];
            now = File.Exists(ConfigPath) ? JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
        lock (_hidden) Unhide(sent);
        var changed = Change(now, sent).ModelChanged;
        return [.. manager.Sessions.Where(s => s.InstanceName is { } i && changed.Contains(i)).Select(s => s.InstanceName!).Distinct()];
    }

    /// <summary>
    /// Writes <paramref name="text"/> as config.json — secrets put back, the browser's limits kept from the file —
    /// when the loader accepts it, then makes it live: sessions on an instance whose model or endpoint changed are
    /// moved as <paramref name="choice"/> says, others in place.
    /// </summary>
    public WebConfigSave Save(string text, CacheChoice choice)
    {
        JsonObject sent;
        try
        {
            sent = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("the top level is not an object");
        }
        catch (JsonException e)
        {
            return new WebConfigSave(false, [$"Not valid JSON: {e.Message}"]);
        }
        lock (_hidden) Unhide(sent);

        JsonObject? before = null;
        var write = ConfigFile.TryApply(new ConfigTarget(ConfigPath, CxAgent.UI.Startup.Env()), root =>
        {
            before = root.DeepClone().AsObject();
            var kept = new JsonObject();
            if (root["web"] is JsonObject web)
                foreach (var key in Protected)
                    if (web[key] is { } value) kept[key] = value.DeepClone();

            root.Clear();
            foreach (var (name, value) in sent) root[name] = value?.DeepClone();

            // THE FILE'S OWN LIMITS, whatever the page sent for them.
            var into = root["web"] as JsonObject;
            if (into is not null) foreach (var key in Protected) into.Remove(key);
            if (kept.Count > 0)
            {
                if (into is null) root["web"] = into = [];
                foreach (var (name, value) in kept) into[name] = value?.DeepClone();
            }
            if (into is { Count: 0 }) root.Remove("web");
        });
        if (!write.Saved) return new WebConfigSave(false, write.Errors);

        var fresh = ConfigResolver.Resolve(paths, CxAgent.UI.Startup.Env(), useMock: false);
        if (!fresh.HasProvider) return new WebConfigSave(true, fresh.Errors);
        manager.ApplyConfig(fresh, Change(before, sent), choice);
        saved?.Invoke(fresh);
        return new WebConfigSave(true, []);
    }

    /// <summary>
    /// What the save changed, as F8's tabs say it: every instance whose entry differs is edited, and one whose kind,
    /// model or endpoint differs has changed model — its sessions lose their prompt cache when they move.
    /// </summary>
    internal static ConfigChange Change(JsonObject? before, JsonObject after)
    {
        var old = before?["providers"] as JsonObject ?? [];
        var now = after["providers"] as JsonObject ?? [];
        var edited = new HashSet<string>(StringComparer.Ordinal);
        var model = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in old.Select(p => p.Key).Union(now.Select(p => p.Key)))
        {
            var a = old[name];
            var b = now[name];
            if (JsonNode.DeepEquals(a, b)) continue;
            edited.Add(name);
            if (a is null || b is null || !JsonNode.DeepEquals(a["model"], b["model"]) || !JsonNode.DeepEquals(a["baseUrl"], b["baseUrl"])
                || !JsonNode.DeepEquals(a["kind"], b["kind"]))
                model.Add(name);
        }
        return new ConfigChange(edited, new Dictionary<string, string>(), true, "config saved in a browser") { ModelChanged = model };
    }
}
