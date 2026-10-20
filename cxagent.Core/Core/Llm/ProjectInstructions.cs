namespace CxAgent.Core.Llm;

/// <summary>One project's instruction file: where it was found, and what it said.</summary>
public sealed record ProjectInstructionFile(string Path, string Text);

/// <summary>An instruction file found and not read, and why — said to the user once (see <see cref="InstructionNotices"/>).</summary>
public sealed record SkippedInstructionFile(string Path, string Reason);

/// <summary>What a search for instruction files found: the files read, and the ones skipped.</summary>
public sealed record InstructionScan(IReadOnlyList<ProjectInstructionFile> Files, IReadOnlyList<SkippedInstructionFile> Skipped);

/// <summary>
/// Says each skipped instruction file once — not every turn, though the prompt that skips it is built every turn.
///
/// <para>A SKIP THE USER WOULD WANT TO KNOW ABOUT. A shared <c>AGENTS.md</c> linked from outside the repository is an
/// ordinary setup, and silently ignoring it reads as the agent ignoring its instructions.</para>
/// </summary>
public sealed class InstructionNotices(Action<Commands.Message> say)
{
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);

    public void Announce(IReadOnlyList<SkippedInstructionFile> skipped)
    {
        foreach (var file in skipped)
            if (_said.Add(file.Path + "\0" + file.Reason))
                say(new Commands.Message(
                    $"{file.Path} {file.Reason}. Copy it into the repository, or put shared instructions in your "
                    + "global CXAGENT.md.", Commands.Severity.Warning));
    }
}

/// <summary>
/// Project and user instructions — <c>CXAGENT.md</c>, <c>AGENTS.md</c> or <c>CLAUDE.md</c> found by
/// walking up from the working directory, plus <c>CXAGENT.md</c> in cxagent's own config directory.
///
/// <para>WHY A SEPARATE FILE AT ALL. Some instructions are true of a REPO, not of agents in general,
/// and they cannot live in the universal system prompt. The case that forced this: a universal prompt
/// telling a model not to add comments unless asked, while this codebase wants the opposite — heavy
/// explanatory comments carrying the reasoning behind a decision. Both are right for their own tree.
/// Writing either into <see cref="SystemPrompt"/> would be wrong for whoever points the agent
/// somewhere else, and a prompt that is wrong about the project is worse than one that is silent.</para>
///
/// <para>Shape from opencode's <c>session/instruction.ts</c>: walk up, FIRST MATCH WINS. Their
/// comment on that line reads "so we don't stack AGENTS.md/CLAUDE.md from every ancestor" — stacking
/// is how a context quietly fills with advice from three directories up that has nothing to do with
/// the work in hand.</para>
/// </summary>
public static class ProjectInstructions
{
    /// <summary>
    /// Names tried in a PROJECT directory, in order, nearest directory first.
    ///
    /// <para>CXAGENT.md leads so a repo can address THIS agent specifically. Some instructions only
    /// make sense for one tool — "never run pkill, it kills the harness" is about cxagent's own
    /// process model, not about agents in general — and a shared AGENTS.md is the wrong place to put
    /// them because every other agent reads it too.</para>
    ///
    /// <para>AGENTS.md is next: the vendor-neutral convention, and what a repo will already have if
    /// it has anything. CLAUDE.md last, so a repo carrying only that one is still honoured without
    /// having to duplicate it under a second name.</para>
    ///
    /// <para>FIRST MATCH WINS, so this degrades cleanly: a repo with no CXAGENT.md behaves exactly as
    /// if the name did not exist. The extra name costs one File.Exists per directory in the walk.</para>
    /// </summary>
    /// <summary>
    /// The instruction files read from a project, in order of preference — first match wins.
    ///
    /// <para>PUBLIC because <c>/init</c> has to write the file that GOVERNS, which means it has to
    /// know what this list says. A private copy over there would be a second list that agrees today:
    /// add a name here and <c>/init</c> would keep writing a file the agent never reads, silently
    /// and with every test still green.</para>
    /// </summary>
    public static readonly string[] ProjectFileNames = ["CXAGENT.md", "AGENTS.md", "CLAUDE.md"];

    /// <summary>
    /// The name read from cxagent's own config directory — <c>AppPaths.ConfigDir</c>, which resolves
    /// per-OS (<c>%APPDATA%\cxagent</c>, <c>~/Library/Application Support/cxagent</c>,
    /// <c>$XDG_CONFIG_HOME/cxagent</c>).
    ///
    /// <para>CXAGENT.md, and only that. This file is READ ONLY BY THIS APP — no other agent looks in
    /// cxagent's config directory — so the shared names buy nothing here: an AGENTS.md at this path
    /// would be a "vendor-neutral" name in a vendor-specific location, which is just a misleading
    /// name. The project directory is the opposite case: several agents read the same repo, so the
    /// shared names matter there and are honoured.</para>
    ///
    /// <para>No CLAUDE.md either, at this level. A project's CLAUDE.md describes the PROJECT and is
    /// honoured wherever the project is; a USER-level one is another product's configuration, written
    /// for a different agent with different tools, and reading it would mean silently obeying
    /// instructions never addressed to this app.</para>
    /// </summary>
    private const string GlobalFileName = "CXAGENT.md";

    /// <summary>
    /// How much of the file is used.
    ///
    /// <para>This rides in the system message, which is the prompt-cache prefix re-sent on EVERY
    /// turn — so an enormous AGENTS.md is not a one-off cost but a permanent tax on the window. The
    /// cut is marked in the text rather than silent: instructions that stop mid-sentence with no
    /// explanation read as a bug in the agent.</para>
    /// </summary>
    private const int MaxChars = 8_000;

    /// <summary>
    /// The instruction files that apply here: the global one if there is one, then the nearest
    /// project one, in that order.
    /// </summary>
    /// <param name="globalDirectory">
    /// cxagent's own config directory, or null to skip it. It carries what is true of the USER
    /// wherever they work, which a per-repo file cannot express and which they should not have to
    /// copy into every checkout.
    ///
    /// <para>OUR FOLDER ONLY. Another product's user-level file, such as <c>~/.claude/CLAUDE.md</c>,
    /// is not read: it would mean silently obeying instructions written for a different agent with
    /// different tools. A repo's CLAUDE.md is a different thing: it describes the PROJECT, so it is
    /// read where the project is.</para>
    /// </param>
    /// <remarks>
    /// <para>GLOBAL FIRST, PROJECT LAST, because later text wins on a conflict: a repo saying "tabs
    /// here" must override a global "spaces everywhere", the repo being the more specific claim.</para>
    ///
    /// <para>Never throws. An unreadable directory, a permission error or a file that vanishes
    /// mid-walk means the agent runs without instructions, which is a degraded run rather than a
    /// failed one — instructions are an enhancement, and no caller has a better answer than
    /// continuing.</para>
    /// </remarks>
    /// <param name="startDirectory">Where the search for instruction files begins, walking upward.</param>
    /// <param name="home">The user's home folder, or null for the real one — where the walk stops short of a
    /// home kept in git (see <see cref="ProjectLevels"/>).</param>
    public static IReadOnlyList<ProjectInstructionFile> Find(
        string startDirectory, string? globalDirectory = null, string? home = null) =>
        Scan(startDirectory, globalDirectory, home).Files;

    /// <summary>
    /// The instruction files <see cref="Find"/> reads, and the ones it skipped — a project file whose real path
    /// leaves the repository is not read (a cloned repository could link it to a key), and is listed so the user
    /// can be told.
    /// </summary>
    /// <param name="startDirectory">Where the search begins, walking upward — from its real folder, the one
    /// trusted, so the repository root it finds always holds it.</param>
    /// <param name="globalDirectory">cxagent's config folder, whose <c>CXAGENT.md</c> is read wherever it links.</param>
    /// <param name="home">The user's home folder, or null for the real one.</param>
    public static InstructionScan Scan(string startDirectory, string? globalDirectory = null, string? home = null)
    {
        var found = new List<ProjectInstructionFile>();
        var skipped = new List<SkippedInstructionFile>();

        if (globalDirectory is not null && Read(new InstructionRead(globalDirectory, GlobalFileName, null, skipped)) is { } global)
            found.Add(global);

        try
        {
            // ONE NAME, EVERY LEVEL THAT HAS IT: every directory from here to the worktree root
            // contributes its hit for the first name that matched anywhere. "Not stacking from every
            // ancestor" is about not mixing AGENTS.md with CLAUDE.md, not about taking only one
            // directory.
            //
            // The monorepo case is why it matters: a root file carries the house style, a package
            // file carries what is specific to that package, and both are true at the same time.
            // BOUNDED AT THE WORKTREE ROOT. This ran to the FILESYSTEM root — `d = d.Parent` until
            // null — so a file in the user's home directory, in /home, or in / was collected and
            // applied as though it described this project. A CLAUDE.md sitting directly in ~ was
            // stacked in, which is the same "another product's configuration written for a different
            // agent" this class deliberately refuses at ~/.claude/CLAUDE.md, reached by the other
            // route; and on a shared machine the directories above ~ are writable by other people.
            //
            // THE PROJECT'S FOLDERS, nearest first — see ProjectLevels for where the walk stops and why.
            var dirs = ProjectLevels.From(Permissions.PathResolver.RealFolder(startDirectory), home);
            var root = dirs[^1];

            foreach (var name in ProjectFileNames)
            {
                // ROOT FIRST, so the nearest file is rendered LAST and wins on a conflict — it is
                // the more specific claim about the code being worked on.
                var matches = new List<ProjectInstructionFile>();
                for (var i = dirs.Count - 1; i >= 0; i--)
                    if (Read(new InstructionRead(dirs[i], name, root, skipped)) is { } file) matches.Add(file);

                if (matches.Count == 0) continue;

                foreach (var file in matches)
                    // A global file at the same path is the same file; do not send it twice.
                    if (found.All(f => !string.Equals(f.Path, file.Path, StringComparison.Ordinal)))
                        found.Add(file);

                break;
            }
        }
        catch (Exception)
        {
            // Best effort, like every other read in this app that is not the work itself.
        }

        return new InstructionScan(found, skipped);
    }

    /// <summary>One file to read: the folder and name, the folder its real path must stay in (null for the user's
    /// own), and where a skip is recorded.</summary>
    private sealed record InstructionRead(string Directory, string Name, string? Root, List<SkippedInstructionFile> Skipped);

    /// <summary>One named file, or null when it is absent, empty, unreadable or refused.</summary>
    private static ProjectInstructionFile? Read(InstructionRead read)
    {
        var path = Path.Combine(read.Directory, read.Name);

        // CONFINED, CAPPED, AND NEVER A DEVICE — see PathResolver.ReadConfined. Bytes enough for the character cap
        // in any encoding UTF-8 allows.
        var text = Permissions.PathResolver.ReadConfined(new Permissions.ConfinedRead(path, read.Root, MaxChars * 4));
        if (text.Problem is { } problem && text.Text is null && File.Exists(path))
        {
            read.Skipped.Add(new SkippedInstructionFile(Path.GetFullPath(path), problem));
            return null;
        }

        // AN EMPTY FILE IS NOT INSTRUCTIONS. A placeholder would otherwise produce a header
        // announcing project instructions followed by nothing.
        var body = text.Text?.Trim();
        if (string.IsNullOrEmpty(body)) return null;

        if (body.Length > MaxChars || text.Truncated)
            body = body[..Math.Min(body.Length, MaxChars)]
                 + $"\n\n[truncated — {read.Name} is longer than {MaxChars:N0} characters]";

        return new ProjectInstructionFile(Path.GetFullPath(path), body);
    }

    public static string Render(IReadOnlyList<ProjectInstructionFile> files)
    {
        if (files.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            sb.Append("\n# Project instructions\n\n");
            sb.Append($"These come from {file.Path}. Where they disagree with anything above, "
                    + "follow these.\n\n");
            sb.Append(file.Text);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
