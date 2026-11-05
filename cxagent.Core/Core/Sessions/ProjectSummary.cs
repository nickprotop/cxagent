using CxAgent.Core.Hooks;
using CxAgent.Core.Permissions;
using CxAgent.Core.Skills;

namespace CxAgent.Core.Sessions;

/// <summary>What a summary is made from — the facts a session already holds about its folder.</summary>
/// <param name="Trust">The folder's trust, which decides whether the repository's hooks run.</param>
/// <param name="Catalog">Skills and command files found, of every scope; the project's are picked out.</param>
/// <param name="Hooks">The hooks the session read, of every source; the project's are picked out.</param>
/// <param name="WorkingDirectory">The folder the session works in.</param>
public sealed record ProjectFacts(string WorkingDirectory, TrustState Trust, InvocableCatalogResult Catalog, HookSnapshot Hooks);

/// <summary>One kind of thing a repository brings, how many, and where they live.</summary>
/// <param name="State">A word about whether these take effect here, or null when they always do.</param>
/// <param name="Kind">What these are: instruction files, skills, commands, plugins or hooks.</param>
/// <param name="Count">How many.</param>
/// <param name="Folder">Where they live.</param>
/// <param name="Names">Their names or paths.</param>
public sealed record ProjectItem(string Kind, int Count, string Folder, IReadOnlyList<string> Names, string? State);

/// <summary>
/// A repository as cxagent sees it: whether it is trusted, and everything in it that changes what the agent
/// reads or runs.
///
/// <para>ONE SCREEN FOR "WHAT DOES THIS REPO DO TO MY AGENT". Instruction files, skills, commands, plugins
/// and hooks each arrive from a different folder and are each listed somewhere else; a cloned repository
/// can bring all five, and the question a user has on opening one is about all of them at once.</para>
/// </summary>
public sealed record ProjectSummary(string? RepoRoot, TrustState Trust, IReadOnlyList<ProjectItem> Items)
{
    public static readonly IReadOnlyList<string> Kinds = ["Instruction files", "Skills", "Commands", "Plugins", "Hooks"];

    /// <summary>The item of this kind; every kind is always present, with a zero count when there is none.</summary>
    public ProjectItem Item(string kind) => Items.First(i => i.Kind == kind);

    public static ProjectSummary For(ProjectFacts facts)
    {
        var root = HookSources.RepoRoot(facts.WorkingDirectory);
        if (root is null)
            return new(null, facts.Trust, [.. Kinds.Select(k => new ProjectItem(k, 0, "", [], null))]);

        var instructions = Llm.ProjectInstructions.Find(facts.WorkingDirectory);
        var project = facts.Catalog.Entries.Where(e => e.Scope == InvocableScope.Project).ToList();
        var skills = project.Where(e => e.Kind == InvocableKind.Skill).ToList();
        var commands = project.Where(e => e.Kind == InvocableKind.Command).ToList();
        var pluginsDir = Path.Combine(root, ".cxagent", "plugins");
        var plugins = Directory.Exists(pluginsDir)
            ? Directory.EnumerateFileSystemEntries(pluginsDir).Select(Path.GetFileName).OfType<string>().Order().ToList()
            : [];
        var hooks = facts.Hooks.Hooks.Where(h => h.Source is HookSource.Project or HookSource.Local).ToList();
        var trusted = facts.Trust == TrustState.Trusted;

        return new(root, facts.Trust,
        [
            new("Instruction files", instructions.Count, root, [.. instructions.Select(f => Path.GetRelativePath(root, f.Path))], null),
            new("Skills", skills.Count, Path.Combine(root, ".cxagent", "skills"), [.. skills.Select(s => s.Name)], null),
            new("Commands", commands.Count, Path.Combine(root, ".cxagent", "commands"), [.. commands.Select(c => c.Name)], null),
            // PLUGINS ASK BEFORE THEY LOAD, whatever the folder's trust: the hash prompt is their gate.
            new("Plugins", plugins.Count, pluginsDir, plugins, plugins.Count > 0 ? "each asks before it loads" : null),
            new("Hooks", hooks.Count, Path.Combine(root, ".cxagent"), [.. hooks.Select(h => h.DisplayName)],
                hooks.Count == 0 ? null : trusted ? "running" : "not running: folder not trusted"),
        ]);
    }
}
