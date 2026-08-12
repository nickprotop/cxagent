namespace CxAgent.Core.Llm;

/// <summary>One project's instruction file: where it was found, and what it said.</summary>
public sealed record ProjectInstructionFile(string Path, string Text);

/// <summary>
/// Project and user instructions — <c>CXAGENT.md</c>, <c>AGENTS.md</c> or <c>CLAUDE.md</c> found by
/// walking up from the working directory, plus <c>CXAGENT.md</c> in cxagent's own config directory.
///
/// <para>WHY A SEPARATE FILE AT ALL. Some instructions are true of a REPO, not of agents in general,
/// and they cannot live in the universal system prompt. The case that forced this: opencode's prompt
/// says "DO NOT ADD ***ANY*** COMMENTS unless asked", while this codebase wants the opposite — heavy
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
    private static readonly string[] ProjectFileNames = ["CXAGENT.md", "AGENTS.md", "CLAUDE.md"];

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
    /// instructions never addressed to this app. opencode does read <c>~/.claude/CLAUDE.md</c>; this
    /// deliberately does not.</para>
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
    /// <para>OUR FOLDER ONLY. opencode also reads <c>~/.claude/CLAUDE.md</c> — another product's
    /// user-level file — which would mean silently obeying instructions written for a different agent
    /// with different tools. A repo's CLAUDE.md is a different thing: it describes the PROJECT, so it
    /// is read where the project is.</para>
    /// </param>
    /// <remarks>
    /// <para>GLOBAL FIRST, PROJECT LAST, because later text wins on a conflict: a repo saying "tabs
    /// here" must override a global "spaces everywhere", the repo being the more specific claim.</para>
    ///
    /// <para>Never throws. An unreadable directory, a permission error or a file that vanishes
    /// mid-walk means the agent runs without instructions — exactly as it did before this existed.</para>
    /// </remarks>
    public static IReadOnlyList<ProjectInstructionFile> Find(
        string startDirectory, string? globalDirectory = null)
    {
        var found = new List<ProjectInstructionFile>();

        if (globalDirectory is not null && Read(globalDirectory, GlobalFileName) is { } global)
            found.Add(global);

        try
        {
            // ONE NAME, EVERY LEVEL THAT HAS IT. opencode does the same: their findUp collects each
            // directory from here to the worktree root and adds every hit for the first name that
            // matched anywhere. Their comment about "not stacking from every ancestor" is about not
            // mixing AGENTS.md with CLAUDE.md, not about taking only one directory.
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
            // A .git ENTRY, FILE OR DIRECTORY. A submodule and a linked worktree mark their root
            // with a .git FILE containing a gitdir: pointer, so testing for the directory alone
            // walks straight past them and back out of the repo.
            //
            // NO REPO, NO WALK. Outside a worktree "the project" has no boundary that means
            // anything, so the only directory whose file certainly describes THIS work is the one
            // the agent was started in. Collecting ancestors there is how a scratch directory under
            // ~ would pick up the home folder's own files.
            var dirs = new List<DirectoryInfo>();
            var foundRepoRoot = false;
            for (var d = new DirectoryInfo(startDirectory); d is not null; d = d.Parent)
            {
                dirs.Add(d);
                var dotGit = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit)) { foundRepoRoot = true; break; }
            }

            // Nothing above the start directory is part of any project, so nothing above it is read.
            if (!foundRepoRoot && dirs.Count > 1) dirs.RemoveRange(1, dirs.Count - 1);

            foreach (var name in ProjectFileNames)
            {
                // ROOT FIRST, so the nearest file is rendered LAST and wins on a conflict — it is
                // the more specific claim about the code being worked on.
                var matches = new List<ProjectInstructionFile>();
                for (var i = dirs.Count - 1; i >= 0; i--)
                    if (Read(dirs[i].FullName, name) is { } file) matches.Add(file);

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

        return found;
    }

    /// <summary>One named file, or null when it is absent, empty or unreadable.</summary>
    private static ProjectInstructionFile? Read(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();

            // AN EMPTY FILE IS NOT INSTRUCTIONS. A placeholder would otherwise produce a header
            // announcing project instructions followed by nothing.
            if (text.Length == 0) return null;

            if (text.Length > MaxChars)
                text = text[..MaxChars]
                     + $"\n\n[truncated — {name} is longer than {MaxChars:N0} characters]";

            return new ProjectInstructionFile(Path.GetFullPath(path), text);
        }
        catch (Exception)
        {
            // Unreadable is the same as absent.
            return null;
        }
    }

    /// <summary>
    /// Renders the file as the block that follows the system prompt, or an empty string when there
    /// is nothing to add.
    ///
    /// <para>NAMED AND FENCED. The model is told where the text came from and that it takes
    /// precedence — an unattributed paragraph appended to a system prompt reads as though the app
    /// itself said it, and there would be no way for the model to weigh a project rule against a
    /// general one.</para>
    /// </summary>
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
