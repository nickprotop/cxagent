using System.Text.Json.Nodes;
using CxAgent.Core.Agents;

namespace CxAgent.Core.Config;

/// <summary>The keys of one <c>agents.&lt;type&gt;</c> entry, as an edit: null leaves a key, <see cref="Clear"/> removes it.</summary>
public sealed record AgentTypeFields
{
    public string? Description { get; init; }
    public string? Briefing { get; init; }
    public string? Provider { get; init; }
    public int? MaxTurns { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlySet<string> Clear { get; init; } = new HashSet<string>();

    internal void ApplyTo(JsonObject type)
    {
        if (Description is { } d) type["description"] = d;
        if (Briefing is { } b) type["briefing"] = b;
        if (Provider is { } p) type["provider"] = p;
        if (MaxTurns is { } m) type["maxTurns"] = m;
        if (Tools is { } t) type["tools"] = new JsonArray([.. t.Select(x => (JsonNode?)x)]);
        foreach (var key in Clear) type.Remove(key);
    }
}

/// <summary>
/// Changes to agent types on the config document.
///
/// <para>A BUILT-IN IS TOLD WHERE IT RUNS, NOT WHAT IT IS TOLD. Its briefing is a contract with the code
/// around it — the planner writes the file the spawner checks for — so an override may set
/// <c>provider</c>, <c>maxTurns</c> and <c>tools</c>, exactly what the loader honours on a shipped type,
/// and a briefing or description for one is refused here rather than written and then ignored.</para>
/// </summary>
public static class AgentEdits
{
    public static void SetOverride(JsonObject root, string builtin, AgentTypeFields fields)
    {
        if (!BuiltinAgentTypes.IsBuiltin(builtin)) throw new ConfigEditException($"'{builtin}' is not a built-in agent type.");
        if (fields.Briefing is not null || fields.Description is not null)
            throw new ConfigEditException($"'{builtin}' ships with cxagent: its briefing and description are not set here.");
        var types = Types(root);
        if (types[builtin] is not JsonObject entry) { entry = new JsonObject(); types[builtin] = entry; }
        fields.ApplyTo(entry);
        if (entry.Count == 0) types.Remove(builtin);
    }

    /// <summary>Back to the shipped type — or to the plain default child — : the override is removed.</summary>
    public static void ResetBuiltin(JsonObject root, string builtin) => Types(root).Remove(builtin);

    /// <summary>
    /// The default child, <c>general</c> — what a spawn that names no type gets. Provider, turns and tools
    /// like a built-in, and an OPTIONAL briefing: a standing instruction for every such spawn, which a
    /// shipped type may not take but this one, having none of its own, can.
    /// </summary>
    public static void SetGeneral(JsonObject root, AgentTypeFields fields)
    {
        var types = Types(root);
        var name = AgentTypeCatalog.DefaultTypeName;
        if (types[name] is not JsonObject entry) { entry = new JsonObject(); types[name] = entry; }
        fields.ApplyTo(entry);
        if (entry.Count == 0) types.Remove(name);
    }

    public static void AddType(JsonObject root, string name, AgentTypeFields fields)
    {
        if (BuiltinAgentTypes.IsBuiltin(name)) throw new ConfigEditException($"'{name}' is a built-in agent type's name.");
        var types = Types(root);
        if (types.ContainsKey(name)) throw new ConfigEditException($"'{name}' already exists.");
        if (string.IsNullOrWhiteSpace(fields.Briefing)) throw new ConfigEditException("A type of your own needs a briefing.");
        var entry = new JsonObject();
        fields.ApplyTo(entry);
        types[name] = entry;
    }

    public static void UpdateType(JsonObject root, string name, AgentTypeFields fields)
    {
        var entry = Own(root, name);
        if (fields.Briefing is { } b && string.IsNullOrWhiteSpace(b) || fields.Clear.Contains("briefing"))
            throw new ConfigEditException("A type of your own needs a briefing.");
        fields.ApplyTo(entry);
    }

    public static void RenameType(JsonObject root, string from, string to)
    {
        if (from == to) return;
        if (BuiltinAgentTypes.IsBuiltin(to)) throw new ConfigEditException($"'{to}' is a built-in agent type's name.");
        var entry = Own(root, from);
        var types = Types(root);
        if (types.ContainsKey(to)) throw new ConfigEditException($"'{to}' already exists.");
        types.Remove(from);
        types[to] = entry;
    }

    public static void RemoveType(JsonObject root, string name)
    {
        Own(root, name);
        Types(root).Remove(name);
    }

    private static JsonObject Types(JsonObject root)
    {
        if (root["agents"] is JsonObject a) return a;
        var created = new JsonObject();
        root["agents"] = created;
        return created;
    }

    private static JsonObject Own(JsonObject root, string name)
    {
        if (BuiltinAgentTypes.IsBuiltin(name)) throw new ConfigEditException($"'{name}' is a built-in agent type; reset it instead.");
        return Types(root)[name] as JsonObject ?? throw new ConfigEditException($"No agent type called '{name}'.");
    }
}

/// <summary>
/// A type's tools as the editor shows them: a base, and what is taken from it or added back.
/// </summary>
/// <param name="Base"><c>inherited</c> or <c>all</c>.</param>
/// <param name="Removed">Tools taken away from the base.</param>
/// <param name="Added">Tools added back on top of it.</param>
public sealed record ToolTerms(string Base, IReadOnlyList<string> Removed, IReadOnlyList<string> Added)
{
    /// <summary>The fewest terms that say it: the base, then each tool removed, then each added back.</summary>
    public static IReadOnlyList<string> Write(ToolTerms terms) =>
        [terms.Base, .. terms.Removed.Select(t => "-" + t), .. terms.Added.Select(t => "+" + t)];

    /// <summary>
    /// What a saved list says, for the editor. A list without a base starts from <c>inherited</c>, and a
    /// bare name counts as added — the loader reads both the same way.
    /// </summary>
    public static ToolTerms Read(IReadOnlyList<string>? terms)
    {
        var list = terms ?? [];
        var baseTerm = list.FirstOrDefault(t => t is "inherited" or "all") ?? "inherited";
        return new ToolTerms(baseTerm,
            [.. list.Where(t => t.StartsWith('-')).Select(t => t[1..])],
            [.. list.Where(t => t is not ("inherited" or "all") && !t.StartsWith('-')).Select(t => t.TrimStart('+'))]);
    }
}
