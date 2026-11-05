using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;

namespace CxAgent.UI.Config;

/// <summary>
/// One hook as F8 edits it. The fields the tab shows are editable; everything else the hook carries — keys
/// cxagent does not edit, a type it does not run yet — rides in <see cref="Raw"/> and is written back as
/// it was read.
/// </summary>
public sealed class HookEntry
{
    public HookSource Source { get; set; }
    public string Event { get; set; } = HookEvents.PreToolUse;
    public string? Matcher { get; set; }
    public string Type { get; set; } = "command";
    public string? Command { get; set; }
    public string? Shell { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool OnFailureBlock { get; set; }
    public string? Name { get; set; }
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public bool ContinueOnBlock { get; set; }
    public string? Url { get; set; }
    public string? Server { get; set; }
    public string? Tool { get; set; }

    /// <summary>The hook's JSON as read; null for a hook added here.</summary>
    public JsonObject? Raw { get; init; }

    /// <summary>Whether this entry is one the tab edits field by field: any of the five types. An unknown type is shown and kept.</summary>
    public bool Editable => HookDefinition.TypeRuns(Type);

    /// <summary>The keys each type is written with — and so the keys a change of type leaves behind.</summary>
    private static readonly Dictionary<string, string[]> TypeKeys = new()
    {
        ["command"] = ["command", "shell"],
        ["prompt"] = ["prompt", "model", "continueOnBlock"],
        ["agent"] = ["prompt", "model"],
        ["http"] = ["url", "headers", "allowedEnvVars"],
        ["mcp_tool"] = ["server", "tool", "input"],
    };

    internal static HookEntry From(HookDefinition h) => new()
    {
        Source = h.Source,
        Event = h.Event,
        Matcher = h.Matcher,
        Type = h.Type,
        Command = h.Command,
        Shell = h.Shell,
        TimeoutSeconds = h.TimeoutSeconds,
        OnFailureBlock = h.OnFailureBlock,
        Name = h.Name,
        Prompt = h.Prompt,
        Model = h.Model,
        ContinueOnBlock = h.ContinueOnBlock,
        Url = h.Url,
        Server = h.Server,
        Tool = h.Tool,
        Raw = h.Raw is { } raw ? JsonNode.Parse(raw.GetRawText()) as JsonObject : null,
    };

    /// <summary>The definition this entry describes now — for the state words F8 shows beside it.</summary>
    public HookDefinition ToDefinition() =>
        new(Event, string.IsNullOrWhiteSpace(Matcher) ? null : Matcher, Type, Source)
        {
            Name = Name, Command = Command, Shell = Shell, TimeoutSeconds = TimeoutSeconds, OnFailureBlock = OnFailureBlock,
            Prompt = Prompt, Model = Model, ContinueOnBlock = ContinueOnBlock, Url = Url, Server = Server, Tool = Tool,
        };

    /// <summary>The hook's object as it will be written: what was read, with the edited fields laid over it.</summary>
    internal JsonObject ToJson()
    {
        var o = Raw?.DeepClone().AsObject() ?? new JsonObject();
        o["type"] = Type;
        if (!Editable) return o;
        // ANOTHER TYPE'S KEYS GO when the type changes: a command left on an http hook would read as a mistake to
        // the next person editing the file, and to any tool that checks it.
        foreach (var key in TypeKeys.Where(kv => kv.Key != Type).SelectMany(kv => kv.Value).Except(TypeKeys[Type]))
            o.Remove(key);
        Set(o, "name", Name);
        switch (Type)
        {
            case "command":
                Set(o, "command", Command);
                Set(o, "shell", Shell);
                break;
            case "prompt" or "agent":
                Set(o, "prompt", Prompt);
                Set(o, "model", Model);
                if (Type == "prompt" && ContinueOnBlock) o["continueOnBlock"] = true; else o.Remove("continueOnBlock");
                break;
            case "http":
                Set(o, "url", Url);
                break;
            case "mcp_tool":
                Set(o, "server", Server);
                Set(o, "tool", Tool);
                break;
        }
        if (TimeoutSeconds is { } t) o["timeout"] = t; else o.Remove("timeout");
        if (OnFailureBlock) o["onFailure"] = "block"; else o.Remove("onFailure");
        return o;
    }

    private static void Set(JsonObject o, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) o.Remove(key); else o[key] = value.Trim();
    }
}

/// <summary>
/// Every hook F8 can edit — the user's config, the repository's shared file and its local one — as one
/// list, and what each file would hold if saved now.
///
/// <para>A FILE IS WRITTEN ONLY WHEN WHAT IT WOULD HOLD CHANGED. Saving an edit to the user's hooks must not
/// rewrite the repository's file: that is a change in someone else's working tree, and a pending one in
/// git, for nothing.</para>
/// </summary>
public sealed class HooksDraft
{
    private readonly Dictionary<HookSource, string> _original = [];

    internal HooksDraft(IEnumerable<HookEntry> entries, string? repoRoot, IReadOnlySet<HookSource> unreadable,
        IReadOnlyDictionary<string, string>? loaded = null, string? defaultModel = null)
    {
        DefaultModel = _originalDefault = defaultModel;
        Loaded = loaded ?? new Dictionary<string, string>();
        Entries = [.. entries];
        RepoRoot = repoRoot;
        Unreadable = unreadable;
        foreach (var s in Editable) _original[s] = Block(s).ToJsonString();
    }

    /// <summary>The sources F8 writes. The app's own (library) hooks are not in any file.</summary>
    public static IReadOnlyList<HookSource> Editable { get; } = [HookSource.User, HookSource.Project, HookSource.Local];

    public List<HookEntry> Entries { get; }

    /// <summary><c>hooks.defaultModel</c> in the system config: the model prompt and agent hooks ask when they name none.</summary>
    public string? DefaultModel { get; set; }

    private readonly string? _originalDefault;

    /// <summary>Each project file's fingerprint when this draft was read — what the user was shown.</summary>
    public IReadOnlyDictionary<string, string> Loaded { get; }

    /// <summary>The sources the last save wrote.</summary>
    public HashSet<HookSource> Written { get; } = [];

    /// <summary>The repository's top, or null outside one — when the project choices are not offered.</summary>
    public string? RepoRoot { get; }

    /// <summary>Files that could not be read: saving into one would replace what the user wrote with what F8 understood of it.</summary>
    public IReadOnlySet<HookSource> Unreadable { get; }

    public bool CanSaveTo(HookSource source) =>
        source == HookSource.User || (RepoRoot is not null && source is HookSource.Project or HookSource.Local);

    public HookEntry Add(HookSource source)
    {
        var entry = new HookEntry { Source = source };
        Entries.Add(entry);
        return entry;
    }

    public void Remove(HookEntry entry) => Entries.Remove(entry);

    /// <summary>The sources whose file would change on save.</summary>
    public IReadOnlyList<HookSource> Changed =>
        [.. Editable.Where(s => Block(s).ToJsonString() != _original[s]
                                || (s == HookSource.User && DefaultModel != _originalDefault))];

    public bool IsDirty => Changed.Count > 0;

    /// <summary>
    /// The <c>hooks</c> block this source's file would hold: events in the contract's order, and hooks that
    /// share an event and a matcher grouped as one matcher group, as a hand-written file would have them.
    /// </summary>
    public JsonObject Block(HookSource source)
    {
        var block = new JsonObject();
        foreach (var byEvent in Entries.Where(e => e.Source == source).GroupBy(e => e.Event)
                     .OrderBy(g => Order(g.Key)))
        {
            var groups = new JsonArray();
            foreach (var byMatcher in byEvent.GroupBy(e => string.IsNullOrWhiteSpace(e.Matcher) ? null : e.Matcher!.Trim()))
            {
                var group = new JsonObject();
                if (byMatcher.Key is { } m) group["matcher"] = m;
                group["hooks"] = new JsonArray([.. byMatcher.Select(e => (JsonNode)e.ToJson())]);
                groups.Add(group);
            }
            block[byEvent.Key] = groups;
        }
        return block;
    }

    private static int Order(string ev)
    {
        var i = HookEvents.All.ToList().IndexOf(ev);
        return i < 0 ? int.MaxValue : i;
    }
}
