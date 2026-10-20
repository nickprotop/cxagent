using System.Text.RegularExpressions;

namespace CxAgent.Core.Skills;

/// <summary>Where a scan starts, and the one folder outside the project it also reads.</summary>
/// <param name="WorkingDirectory">Where the walk up to the repo root starts.</param>
/// <param name="ConfigDirectory">cxagent's config folder, for its <c>commands</c> and <c>skills</c>.</param>
public sealed record CatalogRoots(string WorkingDirectory, string? ConfigDirectory);

public enum InvocableKind { Command, Skill }

/// <summary>Which of the two places a file came from: the project, or cxagent's config dir.</summary>
public enum InvocableScope { Project, Config }

/// <summary>One command file or skill — the winner for its name, or a loser on the shadowed list.</summary>
/// <param name="Name">What follows the <c>/</c>: the file name without <c>.md</c>, or the skill's folder.</param>
/// <param name="Kind">Command file or skill.</param>
/// <param name="Path">The file itself.</param>
/// <param name="Folder">The commands or skills folder it was found under.</param>
/// <param name="Label">A command's subfolder path (<c>frontend</c>), or null.</param>
/// <param name="Description">From frontmatter, or a command file's first non-empty line.</param>
/// <param name="ArgumentHint">What the palette shows after the name, or null.</param>
/// <param name="UserInvocable">False when <c>user-invocable: false</c> keeps it from being typed.</param>
/// <param name="ModelInvocable">False for a command file, or a skill marked <c>disable-model-invocation</c>.</param>
/// <param name="IgnoredFields">Frontmatter keys written for another application and not obeyed.</param>
/// <param name="Body">The text after the frontmatter, before expansion.</param>
/// <param name="SkillDirectory">A skill's own folder, or null for a command file.</param>
public sealed record Invocable(string Name, InvocableKind Kind, string Path, string Folder, string? Label,
    string Description, string? ArgumentHint, bool UserInvocable, bool ModelInvocable,
    IReadOnlyList<string> IgnoredFields, string Body, string? SkillDirectory)
{
    /// <summary>
    /// Project, config or home. SAID IN THE PALETTE because nearest-first means a cloned repo's
    /// <c>review.md</c> replaces yours without a word, and the row is where that is seen before it runs.
    /// </summary>
    public InvocableScope Scope { get; init; }

    /// <summary>Whether the user can run it by typing <c>/name</c>.</summary>
    public bool Typeable => UserInvocable && InvocableCatalog.IsTypeableName(Name);

    public SkillInfo AsSkill() => new(Name, Description, SkillDirectory ?? Folder, Body);
}

/// <summary>A file that lost its name to a nearer one.</summary>
public sealed record ShadowedEntry(Invocable Loser, Invocable Winner);

/// <summary>Everything one scan found.</summary>
/// <param name="Entries">The winner for each name, nearest first.</param>
/// <param name="Shadowed">Every later file with a name a nearer one took.</param>
/// <param name="Problems">Files skipped or reported, and why.</param>
public sealed record InvocableCatalogResult(IReadOnlyList<Invocable> Entries,
    IReadOnlyList<ShadowedEntry> Shadowed, IReadOnlyList<SkillProblem> Problems)
{
    public static InvocableCatalogResult Empty { get; } = new([], [], []);

    /// <summary>What the model sees: winning skills it may load, sorted so the prompt prefix holds.</summary>
    public IReadOnlyList<SkillInfo> ModelSkills =>
        [.. Entries.Where(e => e.Kind == InvocableKind.Skill && e.ModelInvocable)
                   .Select(e => e.AsSkill())
                   .OrderBy(s => s.Name, StringComparer.Ordinal)];

    /// <summary>The entry the user gets by typing <c>/name</c>, or null.</summary>
    public Invocable? Typed(string name) =>
        Entries.FirstOrDefault(e => e.Typeable && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Command files and skills, read from every folder that can hold them, one entry per name.
///
/// <para>ONE SCAN, ONE PRECEDENCE RULE. A skill can be typed by name and a command file is a prompt,
/// so the two are one concept with two layouts; two scanners would carry two rules that have to agree
/// for ever. The model's skill list and the palette are filtered views of this.</para>
///
/// <para>NEAREST FIRST, PER NAME. A package's own file beats the repo's, and the repo's beats the
/// config dir's. Within a level a command beats a skill, then cxagent's skills folder beats the
/// vendor-neutral one: a file written for this tool says more about what is meant here. Per name
/// rather than per folder, so a repo with its own skills still leaves yours available.</para>
///
/// <para>NOT CLAUDE CODE'S <c>.claude</c> FOLDERS, in the project or at home. Their files are written
/// for that tool — its tool names, its <c>allowed-tools</c> grants, its subagents — and followed
/// here they produce instructions the model cannot carry out. A repo that wants a command in both
/// tools keeps a copy, or a symlink, under <c>.cxagent</c>.</para>
///
/// <para>THE WALK STOPS AT THE REPO ROOT, the boundary instruction files use: folders above it can be
/// written by other people on a shared machine, and a command is text the model acts on.</para>
/// </summary>
public static partial class InvocableCatalog
{
    private const string SkillFileName = "SKILL.md";

    // EVERY KEY THAT IS RECOGNISED AND DELIBERATELY NOT OBEYED. allowed-tools is a permission grant
    // written for another application's tools, and model names that application's models; both are
    // listed so the user can see what cxagent did with a file that carries them.
    private static readonly string[] Ignored = ["allowed-tools", "model"];

    /// <summary>
    /// Whether a name can be typed as one word after <c>/</c>. A name with a dot or a space would
    /// split at the wrong place in the composer, so it is not offered as a command.
    /// </summary>
    public static bool IsTypeableName(string name) => TypeableName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TypeableName();

    private enum Layout { Commands, Skills }

    // ROOT: the folder a project source's links must stay inside — the repository root (see decision 16 of the path
    // resolver spec) — or null for the user's own folders, whose links are theirs to make.
    private sealed record Source(string Folder, Layout Layout, InvocableScope Scope, string? Root);

    public static InvocableCatalogResult Find(CatalogRoots roots)
    {
        var problems = new List<SkillProblem>();
        var winners = new Dictionary<string, Invocable>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var shadowed = new List<ShadowedEntry>();

        // EVERY SOURCE IS READ, EVEN AFTER ONE SUPPLIED A NAME: a broken file in a losing folder is
        // still a file the user wrote and expected to work, and silence about it is indistinguishable
        // from it never having been found.
        foreach (var source in Sources(roots, problems))
            foreach (var entry in Read(source, problems))
            {
                if (winners.TryGetValue(entry.Name, out var winner))
                {
                    shadowed.Add(new ShadowedEntry(entry, winner));
                    continue;
                }

                winners[entry.Name] = entry;
                order.Add(entry.Name);
            }

        return new InvocableCatalogResult([.. order.Select(n => winners[n])], shadowed, problems);
    }

    private static List<Source> Sources(CatalogRoots roots, List<SkillProblem> refused)
    {
        var sources = new List<Source>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // THE PROJECT'S FOLDERS, nearest first, and the top of them: the root a project source's links stay in.
        var levels = WalkedLevels(Permissions.PathResolver.RealFolder(roots.WorkingDirectory));
        var repoRoot = Permissions.PathResolver.RealFolder(levels[^1]);

        void Add(string folder, Layout layout, InvocableScope scope)
        {
            // ONCE, AT ITS FIRST POSITION. A symlinked folder — .agents/skills pointing at
            // .cxagent/skills — reaches its target twice. Reading it twice would shadow every file in
            // it by itself.
            var real = RealPath(folder);
            if (real is null) return;

            // A PROJECT SOURCE THAT IS ITSELF A LINK OUT OF THE REPOSITORY brings in a folder that is not the
            // project's — `.agents/skills` pointed at another product's skills, or anywhere — so it is refused whole.
            if (scope == InvocableScope.Project && !Permissions.PathResolver.Contains(repoRoot, real))
            {
                refused.Add(new SkillProblem(folder, $"links outside the repository (→ {real}) — not read"));
                return;
            }
            if (seen.Add(real))
                sources.Add(new Source(folder, layout, scope, scope == InvocableScope.Project ? repoRoot : null));
        }

        foreach (var dir in levels)
        {
            Add(Path.Combine(dir, ".cxagent", "commands"), Layout.Commands, InvocableScope.Project);
            Add(Path.Combine(dir, ".cxagent", "skills"), Layout.Skills, InvocableScope.Project);
            Add(Path.Combine(dir, ".agents", "skills"), Layout.Skills, InvocableScope.Project);
        }

        if (!string.IsNullOrWhiteSpace(roots.ConfigDirectory))
        {
            Add(Path.Combine(roots.ConfigDirectory, "commands"), Layout.Commands, InvocableScope.Config);
            Add(Path.Combine(roots.ConfigDirectory, "skills"), Layout.Skills, InvocableScope.Config);
        }

        return sources;
    }

    /// <summary>The folder's resolved path when it exists, else null — a missing folder is not a source.</summary>
    private static string? RealPath(string folder)
    {
        try
        {
            // EVERY LINK ON THE PATH, not only the last — the one resolver's real folder — so two spellings of one
            // folder are one source however the links fall.
            return Directory.Exists(folder) ? Permissions.PathResolver.RealFolder(folder) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // THE PROJECT'S FOLDERS, nearest first — the same walk the instruction files take; see ProjectLevels.
    private static List<string> WalkedLevels(string start) => Llm.ProjectLevels.From(start);

    private static IEnumerable<Invocable> Read(Source source, List<SkillProblem> problems) =>
        source.Layout == Layout.Commands ? ReadCommands(source, problems) : ReadSkills(source, problems);

    private static List<Invocable> ReadCommands(Source source, List<SkillProblem> problems)
    {
        var files = new List<string>();
        CommandFilesIn(source, files, problems);
        files.Sort(StringComparer.Ordinal);

        var parsed = new List<Invocable>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!IsTypeableName(name))
            {
                problems.Add(new SkillProblem(file,
                    "a command name may only use letters, digits, - and _"));
                continue;
            }

            if (Confined(file, source, problems) is not { } text) continue;

            var front = Frontmatter.Parse(text);
            if (front.Error is { } error) { problems.Add(new SkillProblem(file, error)); continue; }

            var relative = Path.GetRelativePath(source.Folder, Path.GetDirectoryName(file)!);
            parsed.Add(new Invocable(
                Name: name,
                Kind: InvocableKind.Command,
                Path: file,
                Folder: source.Folder,
                Label: relative == "." ? null : relative.Replace('\\', '/'),
                Description: front.Fields.TryGetValue("description", out var d) && d.Length > 0
                    ? d : FirstLine(front.Body),
                ArgumentHint: front.Fields.GetValueOrDefault("argument-hint"),
                UserInvocable: true,
                ModelInvocable: false,
                IgnoredFields: [.. Ignored.Where(front.Fields.ContainsKey)],
                Body: front.Body,
                SkillDirectory: null) { Scope = source.Scope });
        }

        // TWO SUBFOLDERS DECLARING ONE NAME AT ONE LEVEL: neither is more "the" one, so both are
        // skipped and both reported, rather than letting directory order pick silently.
        var clashes = parsed.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        foreach (var clash in clashes)
            problems.Add(new SkillProblem(clash.Path,
                $"more than one '{clash.Name}' in {source.Folder} — rename one"));

        return [.. parsed.Where(p => !clashes.Contains(p))];
    }

    /// <summary>
    /// Every command file under a source folder, by the one bounded walk: a link that leaves the source's root, or
    /// loops back into a folder above it, is reported and not followed; past eight levels a folder is not read.
    ///
    /// <para>A FOLDER THAT CANNOT BE READ COSTS ITSELF, NOT ITS NEIGHBOURS: the walk keeps what can be read and
    /// reports the one that could not.</para>
    ///
    /// <para>A BOUND ON FAN-OUT, not only depth. Following every folder link, a folder of n links to itself costs
    /// n^8 entries — four of them, eight seconds and 349,525 problems on every turn.</para>
    /// </summary>
    private static void CommandFilesIn(Source source, List<string> files, List<SkillProblem> problems)
    {
        try
        {
            var walk = Permissions.PathResolver.Walk(new Permissions.WalkRequest(source.Folder, "*.md",
                source.Root is { } root ? [root] : null, MaxVisited: MaxCommandEntries, MaxDepth: MaxCommandDepth));
            files.AddRange(walk.Files);
            foreach (var folder in walk.TooDeep)
                problems.Add(new SkillProblem(folder, $"deeper than {MaxCommandDepth} folders — not read"));
            foreach (var link in walk.Unfollowed)
                problems.Add(new SkillProblem(link.Path,
                    link.Cycle ? "a link back into a folder above it — not followed"
                    : link.Target is null ? "a link that cannot be resolved — not read"
                    : "a link out of the repository — not read"));
            foreach (var folder in walk.Unreadable)
                problems.Add(new SkillProblem(folder.Path, $"could not be read: {folder.Reason}"));
            if (walk.Truncated)
                problems.Add(new SkillProblem(source.Folder, $"more than {MaxCommandEntries} entries — the rest not read"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new SkillProblem(source.Folder, $"could not be read: {ex.Message}"));
        }
    }

    private const int MaxCommandEntries = 10_000;

    // THE MOST OF ONE COMMAND OR SKILL FILE READ: far past any real one, and a bound on a file that never ends.
    private const int MaxInvocableBytes = 1024 * 1024;

    /// <summary>
    /// A command or skill file's text, read by the confined read: its real path inside the source's root (the
    /// repository, for a project source — the user's own folders have none), only a file with a size, at most a
    /// megabyte. Null, with the reason recorded, when it is refused or cannot be read.
    /// </summary>
    private static string? Confined(string path, Source source, List<SkillProblem> problems)
    {
        var read = Permissions.PathResolver.ReadConfined(new Permissions.ConfinedRead(path, source.Root, MaxInvocableBytes));
        if (read.Problem is { } problem) problems.Add(new SkillProblem(path, problem));
        return read.Text;
    }
    private const int MaxCommandDepth = 8;

    private static List<Invocable> ReadSkills(Source source, List<SkillProblem> problems)
    {
        List<string> subdirectories;
        try { subdirectories = [.. Directory.EnumerateDirectories(source.Folder).Order(StringComparer.Ordinal)]; }
        catch (Exception ex)
        {
            problems.Add(new SkillProblem(source.Folder, $"could not be read: {ex.Message}"));
            return [];
        }

        var skills = new List<Invocable>();
        foreach (var subdirectory in subdirectories)
        {
            var path = Path.Combine(subdirectory, SkillFileName);
            if (!File.Exists(path)) continue;   // not a skill folder; not a problem either

            if (Confined(path, source, problems) is not { } text) continue;

            var name = Path.GetFileName(subdirectory.TrimEnd(Path.DirectorySeparatorChar,
                                                             Path.AltDirectorySeparatorChar));
            if (SkillCatalog.ParseSkill(text, path, name, subdirectory, problems) is not { } skill) continue;

            var front = Frontmatter.Parse(text);
            skills.Add(new Invocable(
                Name: name,
                Kind: InvocableKind.Skill,
                Path: path,
                Folder: source.Folder,
                Label: null,
                Description: skill.Description,
                ArgumentHint: front.Fields.GetValueOrDefault("argument-hint"),
                UserInvocable: Frontmatter.Flag(front.Fields, "user-invocable", otherwise: true),
                ModelInvocable: !Frontmatter.Flag(front.Fields, "disable-model-invocation", otherwise: false),
                IgnoredFields: [.. Ignored.Where(front.Fields.ContainsKey)],
                Body: skill.Body,
                SkillDirectory: subdirectory) { Scope = source.Scope });
        }

        return skills;
    }

    private static string FirstLine(string body) =>
        body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
}
