using CxAgent.Core.Agents;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>
/// An agent type being edited.
///
/// <para>A BUILT-IN WRITES WHAT AN OVERRIDE MAY SET — provider, max turns, tools — and nothing else:
/// its briefing and description ship with cxagent and are shown, not edited. A type of the user's own
/// writes everything, and must have a briefing.</para>
/// </summary>
public sealed class AgentDraft
{
    private AgentDraft(string name, bool builtin, AgentTypeConfig? original)
    {
        Name = name;
        IsBuiltin = builtin;
        Original = original;
        Description = original?.Description;
        Briefing = original?.Briefing;
        Provider = original?.Provider;
        MaxTurns = original?.MaxTurns;
        var terms = ToolTerms.Read(original?.Tools?.Terms);
        Base = terms.Base;
        Removed = [.. terms.Removed];
        Added = [.. terms.Added];
        _originalTools = original?.Tools?.Terms;
    }

    /// <summary>A shipped type, and its override in config if there is one.</summary>
    public static AgentDraft ForBuiltin(AgentTypeDefinition shipped, AgentTypeConfig? overrideCfg) =>
        new(shipped.Name, builtin: true, overrideCfg) { Shipped = shipped };

    /// <summary>A type of the user's own, as config has it.</summary>
    public static AgentDraft ForOwn(string name, AgentTypeConfig cfg) => new(name, builtin: false, cfg);

    /// <summary>A new type, not in the file yet.</summary>
    public static AgentDraft ForNew(string name) => new(name, builtin: false, null) { IsNew = true };

    public string Name { get; set; }
    public bool IsBuiltin { get; }
    public bool IsNew { get; private init; }
    public AgentTypeDefinition? Shipped { get; private init; }
    public AgentTypeConfig? Original { get; }

    public string? Description { get; set; }
    public string? Briefing { get; set; }

    /// <summary>The instance it runs on, or null for the session's own.</summary>
    public string? Provider { get; set; }
    public int? MaxTurns { get; set; }
    public string Base { get; set; }
    public List<string> Removed { get; }
    public List<string> Added { get; }

    private readonly IReadOnlyList<string>? _originalTools;

    /// <summary>The tools as they would be written, or null for no <c>tools</c> key — the parent's set.</summary>
    private IReadOnlyList<string>? Tools =>
        Base == "inherited" && Removed.Count == 0 && Added.Count == 0
            ? null
            : ToolTerms.Write(new ToolTerms(Base, Removed, Added));

    private bool ToolsChanged =>
        !(Tools ?? []).SequenceEqual(_originalTools ?? [], StringComparer.Ordinal);

    public bool IsDirty =>
        IsNew || Provider != Original?.Provider || MaxTurns != Original?.MaxTurns || ToolsChanged
        || (!IsBuiltin && (Blank(Description) != Blank(Original?.Description) || Blank(Briefing) != Blank(Original?.Briefing)));

    /// <summary>The edit: what changed set, what was emptied cleared.</summary>
    public AgentTypeFields ToFields()
    {
        var clear = new HashSet<string>();
        if (Provider is null && Original?.Provider is not null) clear.Add("provider");
        if (MaxTurns is null && Original?.MaxTurns is not null) clear.Add("maxTurns");
        if (ToolsChanged && Tools is null) clear.Add("tools");
        if (!IsBuiltin && Blank(Description) is null && Original?.Description is not null) clear.Add("description");
        return new AgentTypeFields
        {
            Provider = Provider != Original?.Provider ? Provider : null,
            MaxTurns = MaxTurns != Original?.MaxTurns ? MaxTurns : null,
            Tools = ToolsChanged ? Tools : null,
            Description = !IsBuiltin && Blank(Description) is { } d && (IsNew || d != Blank(Original?.Description)) ? d : null,
            Briefing = !IsBuiltin && Blank(Briefing) is { } b && (IsNew || b != Blank(Original?.Briefing)) ? b : null,
            Clear = clear,
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
