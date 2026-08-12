namespace CxAgent.Core.Skills;

/// <summary>One skill: its frontmatter, its directory and its body.</summary>
/// <param name="Name">From the DIRECTORY, not from frontmatter — see <see cref="SkillCatalog"/>.</param>
/// <param name="Directory">The skill's own folder. Returned to the model on load so a later version
/// can point at files beside the SKILL.md without a second capability channel.</param>
public sealed record SkillInfo(string Name, string Description, string Directory, string Body);

/// <summary>A file that looked like a skill and was refused, with the reason a user can act on.</summary>
public sealed record SkillProblem(string Path, string Reason);

/// <summary>
/// What discovery found: the skills, everything it refused, and which directory supplied them.
/// </summary>
/// <param name="SourceDirectory">The winning directory, or NULL when no directory held a usable
/// skill. The null case is not an error — it is what a first attempt at writing a skill looks like,
/// and /skills renders it as "no skills loaded" rather than claiming a directory is in use.</param>
public sealed record SkillCatalogResult(
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<SkillProblem> Problems,
    string? SourceDirectory);

/// <summary>
/// Skills — instructions the model loads when it needs them, instead of paying for them on every
/// turn.
///
/// <para>THE SPLIT THAT MAKES THIS WORTH HAVING: the CATALOG (name + description) rides in the system
/// prompt permanently and costs a few hundred characters; the BODY is fetched by a tool only when the
/// model decides a task matches. Twenty skills of 3k each would be 60k of permanent prefix.</para>
///
/// <para>DISCOVERY RUNS PER TURN, inside the prompt build, exactly as <see cref="Llm.ProjectInstructions"/>
/// does — there is no cache, no snapshot and no refresh command. Unchanged files render byte-identical
/// text and the system message is replaced only when it differs, so the prompt prefix holds; editing a
/// skill costs one prefix and takes effect on the next turn. That is the user's call to make, and an
/// agent that ignored their edit until a restart would be behaving as though it knew better.</para>
/// </summary>
public static class SkillCatalog
{
    /// <summary>
    /// The file that makes a directory a skill. Named by both reference implementations, and by every
    /// published skill on disk, so it is not ours to choose.
    /// </summary>
    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Project locations, in precedence order.
    ///
    /// <para>SPECIFIC BEFORE NEUTRAL. <c>.cxagent/skills</c> is unambiguous; <c>.agents/skills</c>
    /// matches the plural, vendor-neutral <c>AGENTS.md</c> this app already reads, and is what a repo
    /// carrying skills for several agents would plausibly use.</para>
    ///
    /// <para>HIDDEN, because both references hide theirs (<c>.claude/skills</c>, <c>.opencode/skill</c>).
    /// A skills directory is a tool-loading path rather than a document meant to be read, which is the
    /// opposite of AGENTS.md and the reason the unhidden analogy does not carry.</para>
    ///
    /// <para><c>.claude/skills</c> is deliberately NOT read: those files carry <c>allowed-tools</c>,
    /// a tool grant written for a different application with different tools. A user who wants them
    /// says so explicitly with a symlink.</para>
    /// </summary>
    private static readonly string[] ProjectDirectories =
        [Path.Combine(".cxagent", "skills"), Path.Combine(".agents", "skills")];

    /// <summary>
    /// Finds every skill available from this working directory.
    ///
    /// <para>THE WALK IS BOUNDED AT THE REPO ROOT. Start at the working directory and climb while
    /// looking for a <c>.git</c> entry; stop at the directory holding one. With no repo anywhere,
    /// read the working directory ONLY — outside a worktree "the project" has no boundary that means
    /// anything, and climbing would let a scratch directory under the home folder load the home
    /// folder's skills. Unbounded climbing is a real hazard here: a skill is text the model reads AND
    /// ACTS ON, and the directories above the home folder are writable by other people on a shared
    /// machine.</para>
    ///
    /// <para>NEAREST WINS, AND EXACTLY ONE DIRECTORY SUPPLIES EVERYTHING. Two AGENTS.md files stack
    /// sensibly — house style plus package specifics — but two SKILL.md files of the same name are two
    /// versions of one document, and merging them produces a document that contradicts itself. So this
    /// SHADOWS where the instruction walk ACCUMULATES, which is a deliberate departure rather than an
    /// oversight.</para>
    ///
    /// <para>"HOLDS A SKILL" MEANS AT LEAST ONE VALID ONE, not merely that the directory exists. An
    /// abandoned empty <c>.cxagent/skills</c> must not silently disable a populated
    /// <c>.agents/skills</c> below it — a directory that switches everything off while looking like
    /// configuration is the worst failure this could ship.</para>
    /// </summary>
    /// <param name="startDirectory">Where the agent is working.</param>
    /// <param name="globalDirectory">cxagent's own config directory, whose <c>skills/</c> is read when
    /// no project directory supplies any. Null skips it.</param>
    /// <remarks>
    /// Never throws. An unreadable directory, a permission error or a file that vanishes mid-walk
    /// means fewer skills — never a failed turn.
    /// </remarks>
    public static SkillCatalogResult Find(string startDirectory, string? globalDirectory = null)
    {
        var problems = new List<SkillProblem>();
        var candidates = new List<string>();

        try
        {
            foreach (var dir in DirectoriesFrom(startDirectory))
                foreach (var relative in ProjectDirectories)
                    candidates.Add(Path.Combine(dir, relative));
        }
        catch (Exception)
        {
            // Best effort, like every other read in this app that is not the work itself.
        }

        if (!string.IsNullOrWhiteSpace(globalDirectory))
            candidates.Add(Path.Combine(globalDirectory!, "skills"));

        // EVERY CANDIDATE IS READ, EVEN AFTER ONE WINS — because shadowing decides which directory
        // SUPPLIES SKILLS, not which directory may REPORT PROBLEMS. A malformed SKILL.md in a losing
        // directory is still a file the user wrote and expected to work, and staying silent about it
        // is the /mcp failure this app already learned once: a thing that silently never appears is
        // indistinguishable from one that was never configured.
        SkillCatalogResult? winner = null;
        foreach (var candidate in candidates)
        {
            var (skills, found) = ReadDirectory(candidate, problems);
            if (winner is null && found)
                winner = new SkillCatalogResult(skills, problems, candidate);
        }

        return winner is null
            ? new SkillCatalogResult([], problems, null)
            : winner with { Problems = problems };
    }

    /// <summary>
    /// The directories to look in, nearest first: the working directory, then each ancestor up to and
    /// including the one holding <c>.git</c>. No repo means the working directory alone.
    /// </summary>
    private static List<string> DirectoriesFrom(string startDirectory)
    {
        var dirs = new List<string>();
        var foundRepoRoot = false;

        for (var d = new DirectoryInfo(startDirectory); d is not null; d = d.Parent)
        {
            dirs.Add(d.FullName);
            if (IsRepoRoot(d.FullName)) { foundRepoRoot = true; break; }
        }

        if (!foundRepoRoot && dirs.Count > 1) dirs.RemoveRange(1, dirs.Count - 1);
        return dirs;
    }

    /// <summary>
    /// A .git ENTRY, file or directory. A submodule and a linked worktree mark their root with a .git
    /// FILE holding a <c>gitdir:</c> pointer, so testing only for the directory walks straight past
    /// them and back out of the repo — the checkout style this project is itself developed in.
    /// </summary>
    private static bool IsRepoRoot(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>
    /// Every valid skill in one directory, sorted by name, plus whatever it refused.
    /// </summary>
    /// <returns>The skills, and whether this directory holds at least one — which is what "exists"
    /// means for shadowing.</returns>
    private static (IReadOnlyList<SkillInfo> Skills, bool Found) ReadDirectory(
        string directory, List<SkillProblem> problems)
    {
        List<string> subdirectories;
        try
        {
            if (!Directory.Exists(directory)) return ([], false);
            subdirectories = Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception)
        {
            return ([], false);
        }

        var skills = new List<SkillInfo>();
        foreach (var subdirectory in subdirectories)
        {
            var path = Path.Combine(subdirectory, SkillFileName);
            if (!File.Exists(path)) continue;   // not a skill folder; not a problem either

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception) { problems.Add(new SkillProblem(path, "could not be read")); continue; }

            // IDENTITY COMES FROM THE DIRECTORY. Frontmatter `name` is checked against it and
            // reported when it disagrees, never obeyed: a name that can differ from its folder lets
            // two directories declare the same skill, and makes "which file is this?" unanswerable
            // from the catalog alone.
            var name = Path.GetFileName(subdirectory.TrimEnd(Path.DirectorySeparatorChar,
                                                             Path.AltDirectorySeparatorChar));

            if (Parse(text, path, name, subdirectory, problems) is { } skill) skills.Add(skill);
        }

        // SORTED, ORDINALLY. Directory.EnumerateDirectories returns filesystem order, which is not
        // sorted and differs between machines and between runs. The catalog rides in the cached
        // prompt prefix, so an unsorted list would churn that prefix for free — the same reason
        // AppendMcpInstructions sorts its servers.
        skills.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return (skills, skills.Count > 0);
    }

    /// <summary>
    /// Frontmatter and body, or null with a reason recorded.
    ///
    /// <para>HAND-ROLLED, NO YAML DEPENDENCY. Two string fields do not justify a package, and real
    /// skills use flat <c>key: value</c> lines.</para>
    ///
    /// <para>SPLIT ON THE FIRST COLON ONLY. Descriptions are prose and contain colons — one on disk
    /// reads "…default, and only fall back to declarative/fluent `IProjectionFor&lt;T&gt;`…" — so
    /// splitting on every colon truncates exactly the field the model decides from.</para>
    ///
    /// <para>UNKNOWN KEYS ARE IGNORED, NOT FATAL. Published skills carry <c>argument-hint</c> and
    /// <c>allowed-tools</c>; refusing them would make the recommended
    /// <c>ln -s .claude/skills .cxagent/skills</c> import nothing. Ignoring <c>allowed-tools</c> is a
    /// decision with teeth and it is deliberate: it names another application's tools, and this app's
    /// own permission gate governs every call a skill provokes.</para>
    /// </summary>
    private static SkillInfo? Parse(
        string text, string path, string name, string directory, List<SkillProblem> problems)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            problems.Add(new SkillProblem(path, "no frontmatter: the file must start with ---"));
            return null;
        }

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { end = i; break; }

        if (end < 0)
        {
            problems.Add(new SkillProblem(path, "frontmatter is never closed with ---"));
            return null;
        }

        string? description = null, declaredName = null;
        for (var i = 1; i < end; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;                       // blank, comment, or continuation

            var key = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();

            if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)) description = value;
            else if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)) declaredName = value;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            // THE DESCRIPTION IS THE ENTIRE INTERFACE — it is the only thing the model sees before
            // deciding whether to load. A skill without one is invisible in practice, so it is
            // refused with a reason rather than listed as a nameless entry nothing will ever pick.
            problems.Add(new SkillProblem(path, "no description: the model has nothing to match on"));
            return null;
        }

        if (!string.IsNullOrWhiteSpace(declaredName)
            && !string.Equals(declaredName, name, StringComparison.Ordinal))
            problems.Add(new SkillProblem(path,
                $"frontmatter name '{declaredName}' does not match its folder — the folder wins"));

        var body = string.Join("\n", lines.Skip(end + 1)).Trim();
        if (body.Length == 0)
        {
            problems.Add(new SkillProblem(path, "no body: nothing to load"));
            return null;
        }

        // THE BODY IS NOT CAPPED, unlike project instructions. That cap guards the SYSTEM PROMPT,
        // which every turn carries whether or not anyone wanted it. A body is a tool result: paid for
        // only when the model chose to load it, and removable by compaction. Truncating a document
        // the model deliberately asked for, to save a cost it already accepted, trades a real loss
        // for an imaginary saving.
        return new SkillInfo(name, description!, directory, body);
    }
}
