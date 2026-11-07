using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>
/// The three things <c>orchestrator.maxTurns</c> can say, as the General tab offers them.
///
/// <para>THREE CHOICES, NOT ONE NUMBER. Absent and <c>0</c> mean different things — absent leaves the session
/// uncapped and sub-agents at their built-in ceiling, <c>0</c> uncaps both — and a field where empty and zero
/// differ only by a hint is one people read wrong.</para>
/// </summary>
public enum TurnCap
{
    /// <summary>No key: none for the session, the built-in ceiling for sub-agents.</summary>
    Default,

    /// <summary><c>0</c>: no cap for anyone.</summary>
    None,

    /// <summary>A number: that cap for the session and every sub-agent whose type sets none.</summary>
    Number,
}

/// <summary>
/// The General tab's settings, as edited — the app-wide keys that are not providers, servers or agent types.
/// Holds no UI, so what is read and what is written can be tested without a window.
///
/// <para>WRITES ONLY WHAT CHANGED. A key the user did not touch keeps its exact form in the file, an explicit
/// default included; one set back to its default is removed, with its block when nothing else is in it.</para>
/// </summary>
public sealed class GeneralDraft
{
    private bool _originalDetach;
    private bool _originalMemory;
    private TurnCap _originalCap;
    private int? _originalCapNumber;
    private int? _originalCompact;
    private IReadOnlyList<string>? _originalTools;
    private string _originalTavilyKey = "";
    private Core.Sandbox.SandboxSettings _originalSandbox = Core.Sandbox.SandboxSettings.AppDefault;

    private GeneralDraft() { }

    /// <summary>The settings as config has them.</summary>
    /// <param name="settings">The loaded config.</param>
    /// <param name="tavilyKeyAsWritten">The Tavily key as the file has it, placeholder and all — see
    /// <see cref="ProvidersRead.TavilyKeyAsWritten"/>. Never the loaded, expanded value.</param>
    public static GeneralDraft From(ProviderSettings settings, string? tavilyKeyAsWritten = null)
    {
        var turns = settings.Orchestrator.MaxTurns;
        var terms = ToolBoxes.Read(settings.Tools?.Terms);
        var draft = new GeneralDraft
        {
            DetachOnTimeout = settings.ShellDetachOnTimeout ?? true,
            MemoryEnabled = settings.Memory ?? true,
            Cap = turns switch { null => TurnCap.Default, 0 => TurnCap.None, _ => TurnCap.Number },
            // A CAP OF 300 TO START FROM when there is none: the sub-agents' built-in ceiling, the number most
            // likely to be wanted by someone choosing "Cap at".
            CapText = turns is > 0 ? turns.Value.ToString() : Core.Agents.AgentHost.DefaultTurnCeiling.ToString(),
            CompactText = settings.Orchestrator.ContextCompressThreshold?.ToString() ?? "",
            Base = terms.Base,
            Removed = [.. terms.Removed],
            Added = [.. terms.Added],
            TavilyKey = tavilyKeyAsWritten ?? "",
        };
        var sandbox = settings.Sandbox ?? Core.Sandbox.SandboxSettings.AppDefault;
        draft.SandboxEnabled = sandbox.Enabled;
        draft.SandboxNetwork = sandbox.Network;
        draft.WritableText = Joined(sandbox.Writable);
        draft.HiddenText = Joined(sandbox.Hidden);
        draft.HiddenEnvText = Joined(sandbox.HiddenEnv);
        draft.PassEnvText = Joined(sandbox.PassEnv);
        draft._originalSandbox = draft.Sandbox;
        draft._originalDetach = draft.DetachOnTimeout;
        draft._originalMemory = draft.MemoryEnabled;
        draft._originalCap = draft.Cap;
        draft._originalCapNumber = turns;
        draft._originalCompact = settings.Orchestrator.ContextCompressThreshold;
        draft._originalTools = ToolBoxes.Canonical(settings.Tools?.Terms);
        draft._originalTavilyKey = draft.TavilyKey;
        return draft;
    }

    /// <summary>Whether the model's shell commands run inside the sandbox where it is available.</summary>
    public bool SandboxEnabled { get; set; } = true;

    /// <summary>Whether a contained command reaches the network.</summary>
    public bool SandboxNetwork { get; set; } = true;

    /// <summary>Extra writable folders, one per line as typed.</summary>
    public string WritableText { get; set; } = "";

    /// <summary>Extra folders or files seen empty, one per line as typed.</summary>
    public string HiddenText { get; set; } = "";

    /// <summary>Extra variable names or patterns removed, one per line as typed.</summary>
    public string HiddenEnvText { get; set; } = "";

    /// <summary>Variable names kept despite a pattern, one per line as typed.</summary>
    public string PassEnvText { get; set; } = "";

    /// <summary>The sandbox settings as they would be written.</summary>
    private Core.Sandbox.SandboxSettings Sandbox => new(SandboxEnabled, SandboxNetwork,
        Split(WritableText), Split(HiddenText), Split(HiddenEnvText), Split(PassEnvText));

    private bool SandboxChanged => !SameSandbox(Sandbox, _originalSandbox);

    private static bool SameSandbox(Core.Sandbox.SandboxSettings a, Core.Sandbox.SandboxSettings b) =>
        a.Enabled == b.Enabled && a.Network == b.Network
        && a.Writable.SequenceEqual(b.Writable) && a.Hidden.SequenceEqual(b.Hidden)
        && a.HiddenEnv.SequenceEqual(b.HiddenEnv) && a.PassEnv.SequenceEqual(b.PassEnv);

    /// <summary>A list as text, one entry per line — the way it is typed back.</summary>
    private static string Joined(IReadOnlyList<string> items) => string.Join("\n", items);

    /// <summary>Typed text as a list: ONE ENTRY PER LINE, blanks dropped. Lines, not commas, because a comma is a
    /// legal character in a path and a separator inside one would cut it in two.</summary>
    private static IReadOnlyList<string> Split(string text) =>
        [.. text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Sets <c>sandbox.enabled</c> alone, for <c>/sandbox on</c> and <c>off</c>: on is the default, so it removes the
    /// key (and the block, when that empties it); off writes false. Every other sandbox key is left as it is.
    /// </summary>
    /// <param name="root">The config's root.</param>
    /// <param name="enabled">The new setting.</param>
    public static void SetSandboxEnabled(JsonObject root, bool enabled) =>
        Set(root, "sandbox", "enabled", enabled ? null : JsonValue.Create(false));

    /// <summary>A command past its timeout is handed to the background (true) or killed (false).</summary>
    public bool DetachOnTimeout { get; set; }

    /// <summary>Whether the agent keeps notes across sessions — config's <c>memory.enabled</c>, on when absent.</summary>
    public bool MemoryEnabled { get; set; } = true;

    public TurnCap Cap { get; set; }

    /// <summary>The cap's number as typed; read only when <see cref="Cap"/> is <see cref="TurnCap.Number"/>.</summary>
    public string CapText { get; set; } = "";

    /// <summary>The compaction threshold as typed; empty is the default share of the window.</summary>
    public string CompactText { get; set; } = "";

    /// <summary><c>inherited</c> or <c>all</c> — see <see cref="ToolTerms"/>.</summary>
    public string Base { get; set; } = "inherited";

    public List<string> Removed { get; private init; } = [];
    public List<string> Added { get; private init; } = [];

    /// <summary>What would be written to <c>maxTurns</c>: null for no key.</summary>
    private int? CapNumber => Cap switch
    {
        TurnCap.None => 0,
        TurnCap.Number => Positive(CapText),
        _ => null,
    };

    private int? Compact => CompactText.Trim().Length == 0 ? null : Positive(CompactText);

    /// <summary>The tools as they would be written, or null for no key — every tool offered.</summary>
    private IReadOnlyList<string>? Tools => ToolBoxes.Written(Base, Removed, Added);

    private bool DetachChanged => DetachOnTimeout != _originalDetach;
    private bool MemoryChanged => MemoryEnabled != _originalMemory;
    private bool CapChanged => Cap != _originalCap || (Cap == TurnCap.Number && CapNumber != _originalCapNumber);
    private bool CompactChanged => Compact != _originalCompact || (Compact is null && CompactText.Trim().Length > 0);
    private bool ToolsChanged => !ToolBoxes.Same(Tools, _originalTools);
    private bool TavilyKeyChanged => TavilyKey.Trim() != _originalTavilyKey.Trim();

    /// <summary>The Tavily key as it will be written: empty means none, and searches use DuckDuckGo.</summary>
    public string TavilyKey { get; set; } = "";

    /// <summary>The Tavily key the file holds now — what the line shows once nothing new has been typed.</summary>
    public string SavedTavilyKey => _originalTavilyKey;

    /// <summary>
    /// What the key prompt holds. EMPTY MEANS UNCHANGED, as on the Providers tab: opening the prompt empties
    /// it, and reading that as "no key" deleted a saved key the next time anything else on the tab was saved.
    /// Removing the key is <see cref="ClearTavilyKey"/>'s job alone.
    /// </summary>
    public void TypeTavilyKey(string text) => TavilyKey = text.Trim().Length == 0 ? _originalTavilyKey : text;

    /// <summary>Remove the key on save, so searches use DuckDuckGo.</summary>
    public void ClearTavilyKey() => TavilyKey = "";

    public bool IsDirty => DetachChanged || MemoryChanged || CapChanged || CompactChanged || ToolsChanged || TavilyKeyChanged || SandboxChanged;

    /// <summary>
    /// What was just written becomes what the file says, so the draft is clean without being rebuilt — a rebuild
    /// would replace the field that had the keyboard, and a replaced field is left out of the Tab order.
    /// </summary>
    public void Saved()
    {
        _originalDetach = DetachOnTimeout;
        _originalMemory = MemoryEnabled;
        _originalCap = Cap;
        _originalCapNumber = CapNumber;
        _originalCompact = Compact;
        _originalTools = Tools;
        _originalTavilyKey = TavilyKey;
        _originalSandbox = Sandbox;
    }

    /// <summary>Why this cannot be saved as it stands, in words for the user; null when it can.</summary>
    public string? Problem =>
        Cap == TurnCap.Number && CapNumber is null ? "The turn cap must be a whole number of 1 or more."
        : CompactText.Trim().Length > 0 && Compact is null ? "Compact above must be a whole number of tokens, 1 or more — or empty for the default."
        : Split(WritableText).Concat(Split(HiddenText)).FirstOrDefault(p => !Core.Sandbox.SandboxSettings.IsAnchored(p)) is { } relative
            ? $"Sandbox folders must start with / or ~ — '{relative}' would mean a different place on each launch."
        : null;

    /// <summary>Writes what changed into the config's root.</summary>
    public void Apply(JsonObject root)
    {
        // ABSENT IS THE DEFAULT, true — so going back to it removes the key rather than writing it.
        if (DetachChanged)
        {
            if (DetachOnTimeout) root.Remove("shellDetachOnTimeout");
            else root["shellDetachOnTimeout"] = false;
        }

        // ON IS THE DEFAULT, so on removes the key, and the block with it when nothing else is there.
        if (MemoryChanged) Set(root, "memory", "enabled", MemoryEnabled ? null : JsonValue.Create(false));

        if (CapChanged) Set(root, "orchestrator", "maxTurns", CapNumber is { } n ? JsonValue.Create(n) : null);
        if (CompactChanged) Set(root, "orchestrator", "contextCompressThreshold", Compact is { } c ? JsonValue.Create(c) : null);
        if (ToolsChanged)
            Set(root, "llmAgent", "tools", Tools is { } terms ? new JsonArray([.. terms.Select(t => (JsonNode)JsonValue.Create(t))]) : null);
        // EMPTY REMOVES IT, and the block with it when nothing else is left: no key is DuckDuckGo.
        if (TavilyKeyChanged)
            Set(root, "webSearch", "tavilyApiKey", TavilyKey.Trim() is { Length: > 0 } key ? JsonValue.Create(key) : null);

        // EACH KEY AT ITS DEFAULT IS REMOVED, so a block back at the defaults disappears and a key this tab does not
        // know is kept: absent is on, network on, nothing added.
        if (SandboxChanged)
        {
            var s = Sandbox;
            Set(root, "sandbox", "enabled", s.Enabled ? null : JsonValue.Create(false));
            Set(root, "sandbox", "network", s.Network ? null : JsonValue.Create(false));
            Set(root, "sandbox", "writable", Array(s.Writable));
            Set(root, "sandbox", "hidden", Array(s.Hidden));
            Set(root, "sandbox", "hiddenEnv", Array(s.HiddenEnv));
            Set(root, "sandbox", "passEnv", Array(s.PassEnv));
        }
    }

    private static JsonArray? Array(IReadOnlyList<string> items) =>
        items.Count == 0 ? null : new JsonArray([.. items.Select(i => (JsonNode)JsonValue.Create(i))]);

    /// <summary>One key in a block, or removed when <paramref name="value"/> is null — with the block, when that empties it.</summary>
    private static void Set(JsonObject root, string block, string key, JsonNode? value)
    {
        var obj = root[block] as JsonObject;
        if (value is null)
        {
            if (obj is null) return;
            obj.Remove(key);
            if (obj.Count == 0) root.Remove(block);
            return;
        }
        if (obj is null) root[block] = obj = new JsonObject();
        obj[key] = value;
    }

    private static int? Positive(string text) => int.TryParse(text.Trim(), out var n) && n >= 1 ? n : null;
}
