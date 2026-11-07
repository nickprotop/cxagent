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
/// <para>THE web BLOCK IS NOT IN IT: where the server listens, who must log in, the users, the folders and the switches
/// are left out of what is shown and put back from the file on save, whatever the page sent — a login must not widen
/// its own access.</para>
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

    // A NAME THAT HOLDS A SECRET: apiKey, tavilyApiKey, AWS_SECRET_ACCESS_KEY, OPENAI_KEY, token, password, cookie …
    internal static readonly Regex SecretName =
        new("(key|token|secret|password|passwd|credential|authorization|cookie)s?$", RegexOptions.IgnoreCase);

    private string ConfigPath => Path.Combine(paths.ConfigDir, "config.json");

    // THE SECRETS THE PAGE WAS SHOWN, BY WHERE THEY WERE: a placeholder is put back only at the path it was taken from,
    // so one pasted into a description stays the placeholder text, and two keys ending alike never trade places.
    private readonly Dictionary<string, (string Shown, string Real)> _hidden = new(StringComparer.Ordinal);

    // THE FILE AS THE PAGE READ IT: a save over a file changed since — by F8, /think --save, the marketplace — is refused
    // rather than reverting that change, and the secret it may have rotated with it.
    private string? _readText;

    private static string? FileText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>The file as a browser may see it, or null with why it cannot be read.</summary>
    public string? Read(out string? why)
    {
        why = null;
        JsonObject root;
        string? text;
        try
        {
            text = FileText(ConfigPath);
            root = text is null ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            why = $"config.json could not be read: {e.Message}";
            return null;
        }

        // THE WHOLE web BLOCK IS THE TERMINAL'S: where the server listens, who must log in, the users, the folders and
        // the switches are what a login must not change about itself, so none of it is shown.
        root.Remove("web");
        lock (_hidden)
        {
            _readText = text;
            _hidden.Clear();
            Hide(root, "");
        }
        return root.ToJsonString(Indented);
    }

    private void Hide(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    var at = path + "/" + name;
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && SecretName.IsMatch(name) && !IsReference(text))
                        o[name] = Mask(at, text);
                    else Hide(value, at);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var at = path + "/" + i;
                    // A FLAG'S VALUE: ["--api-key", "sk-…"] in a server's arguments.
                    var flag = i > 0 && a[i - 1] is JsonValue pv && pv.TryGetValue<string>(out var prev)
                        && prev.StartsWith('-') && SecretName.IsMatch(prev.TrimStart('-').Replace("-", "", StringComparison.Ordinal));
                    if (flag && a[i] is JsonValue v && v.TryGetValue<string>(out var text) && !IsReference(text)) a[i] = Mask(at, text);
                    else Hide(a[i], at);
                }
                break;
        }
    }

    private JsonValue Mask(string path, string real)
    {
        var shown = $"(hidden #{_hidden.Count + 1} · …{(real.Length > 4 ? real[^4..] : "")})";
        _hidden[path] = (shown, real);
        return JsonValue.Create(shown);
    }

    // A PLACEHOLDER FOR A SECRET IS NOT ONE: {env:NAME} and $NAME say where the key comes from, which is worth seeing.
    private static bool IsReference(string text) =>
        text.Length == 0 || text.StartsWith("{env:", StringComparison.Ordinal) || text.StartsWith('$');

    private void Unhide(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    var at = path + "/" + name;
                    if (Restored(value, at) is { } real) o[name] = real;
                    else Unhide(value, at);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var at = path + "/" + i;
                    if (Restored(a[i], at) is { } real) a[i] = real;
                    else Unhide(a[i], at);
                }
                break;
        }
    }

    private string? Restored(JsonNode? value, string path) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && _hidden.TryGetValue(path, out var hidden) && hidden.Shown == text
            ? hidden.Real
            : null;

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
        lock (_hidden) Unhide(sent, "");
        var changed = Change(now, sent).ModelChanged;
        return [.. manager.Sessions.Where(s => s.InstanceName is { } i && changed.Contains(i)).Select(s => s.InstanceName!).Distinct()];
    }

    /// <summary>
    /// Writes <paramref name="text"/> as config.json — secrets put back where they were, the web block kept from the
    /// file — when the loader accepts it and the file is still what the page read, then makes it live: sessions on an
    /// instance whose model or endpoint changed are moved as <paramref name="choice"/> says, others in place.
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
        lock (_hidden)
        {
            if (FileText(ConfigPath) != _readText)
                return new WebConfigSave(false, ["config.json changed since this page read it — Reload to see it, then make your change again."]);
            Unhide(sent, "");
        }

        JsonObject? before = null;
        var write = ConfigFile.TryApply(new ConfigTarget(ConfigPath, CxAgent.UI.Startup.Env()), root =>
        {
            before = root.DeepClone().AsObject();
            var web = root["web"]?.DeepClone();
            root.Clear();
            foreach (var (name, value) in sent) root[name] = value?.DeepClone();

            // THE FILE'S OWN web BLOCK, whatever the page sent for it.
            root.Remove("web");
            if (web is not null) root["web"] = web;
        });
        if (!write.Saved) return new WebConfigSave(false, write.Errors);

        var fresh = ConfigResolver.Resolve(paths, CxAgent.UI.Startup.Env(), useMock: false);
        if (!fresh.HasProvider) return new WebConfigSave(true, fresh.Errors);
        manager.ApplyConfig(fresh, Change(before, sent), choice);
        saved?.Invoke(fresh);
        return new WebConfigSave(true, []);
    }

    /// <summary>
    /// A form's save: the edit written through the one config writer, judged by the loader, then made live as a
    /// text save is — <paramref name="change"/> says which sessions it moves, as the F8 tab that owns the form says.
    /// </summary>
    public WebConfigSave Commit(Action<JsonObject> edit, ConfigChange change, CacheChoice choice)
    {
        var write = ConfigFile.TryApply(new ConfigTarget(ConfigPath, CxAgent.UI.Startup.Env()), edit);
        if (!write.Saved) return new WebConfigSave(false, write.Errors);
        var fresh = ConfigResolver.Resolve(paths, CxAgent.UI.Startup.Env(), useMock: false);
        if (!fresh.HasProvider) return new WebConfigSave(true, fresh.Errors);
        manager.ApplyConfig(fresh, change, choice);
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
