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
    private TurnCap _originalCap;
    private int? _originalCapNumber;
    private int? _originalCompact;
    private IReadOnlyList<string>? _originalTools;

    private GeneralDraft() { }

    /// <summary>The settings as config has them.</summary>
    public static GeneralDraft From(ProviderSettings settings)
    {
        var turns = settings.Orchestrator.MaxTurns;
        var terms = ToolBoxes.Read(settings.Tools?.Terms);
        var draft = new GeneralDraft
        {
            DetachOnTimeout = settings.ShellDetachOnTimeout ?? true,
            Cap = turns switch { null => TurnCap.Default, 0 => TurnCap.None, _ => TurnCap.Number },
            // A CAP OF 300 TO START FROM when there is none: the sub-agents' built-in ceiling, the number most
            // likely to be wanted by someone choosing "Cap at".
            CapText = turns is > 0 ? turns.Value.ToString() : Core.Agents.AgentHost.DefaultTurnCeiling.ToString(),
            CompactText = settings.Orchestrator.ContextCompressThreshold?.ToString() ?? "",
            Base = terms.Base,
            Removed = [.. terms.Removed],
            Added = [.. terms.Added],
        };
        draft._originalDetach = draft.DetachOnTimeout;
        draft._originalCap = draft.Cap;
        draft._originalCapNumber = turns;
        draft._originalCompact = settings.Orchestrator.ContextCompressThreshold;
        draft._originalTools = ToolBoxes.Canonical(settings.Tools?.Terms);
        return draft;
    }

    /// <summary>A command past its timeout is handed to the background (true) or killed (false).</summary>
    public bool DetachOnTimeout { get; set; }

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
    private bool CapChanged => Cap != _originalCap || (Cap == TurnCap.Number && CapNumber != _originalCapNumber);
    private bool CompactChanged => Compact != _originalCompact || (Compact is null && CompactText.Trim().Length > 0);
    private bool ToolsChanged => !ToolBoxes.Same(Tools, _originalTools);

    public bool IsDirty => DetachChanged || CapChanged || CompactChanged || ToolsChanged;

    /// <summary>
    /// What was just written becomes what the file says, so the draft is clean without being rebuilt — a rebuild
    /// would replace the field that had the keyboard, and a replaced field is left out of the Tab order.
    /// </summary>
    public void Saved()
    {
        _originalDetach = DetachOnTimeout;
        _originalCap = Cap;
        _originalCapNumber = CapNumber;
        _originalCompact = Compact;
        _originalTools = Tools;
    }

    /// <summary>Why this cannot be saved as it stands, in words for the user; null when it can.</summary>
    public string? Problem =>
        Cap == TurnCap.Number && CapNumber is null ? "The turn cap must be a whole number of 1 or more."
        : CompactText.Trim().Length > 0 && Compact is null ? "Compact above must be a whole number of tokens, 1 or more — or empty for the default."
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

        if (CapChanged) Set(root, "orchestrator", "maxTurns", CapNumber is { } n ? JsonValue.Create(n) : null);
        if (CompactChanged) Set(root, "orchestrator", "contextCompressThreshold", Compact is { } c ? JsonValue.Create(c) : null);
        if (ToolsChanged)
            Set(root, "llmAgent", "tools", Tools is { } terms ? new JsonArray([.. terms.Select(t => (JsonNode)JsonValue.Create(t))]) : null);
    }

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
