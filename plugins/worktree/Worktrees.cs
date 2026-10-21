using System.Text.RegularExpressions;

namespace CxAgent.Plugins.Worktree;

/// <summary>Where worktrees go and what their branches are called — the plugin's settings.</summary>
/// <param name="Folder">The folder, relative to the agent's own, that worktrees are made in.</param>
/// <param name="BranchPrefix">Put in front of a worktree's name to make its branch's.</param>
public sealed record WorktreeSettings(string Folder = ".worktrees", string BranchPrefix = "worktree/");

/// <summary>What a tool call came to: text for the model, and whether it did what was asked.</summary>
public sealed record Outcome(bool Ok, string Text)
{
    public static Outcome Done(string text) => new(true, text);
    public static Outcome Refused(string text) => new(false, text);
}

/// <summary>
/// The worktrees one repository has, made and merged and thrown away through git.
///
/// <para>INSIDE THE AGENT'S FOLDER, AND THAT IS THE POINT. A worktree under
/// <c>&lt;folder&gt;/.worktrees/&lt;name&gt;</c> can be handed to a sub-agent as its <c>root</c>, which
/// must be inside the parent's folder; a worktree beside the repository, where <c>git worktree</c>
/// would put one by default, could not. The folder is added to the repository's
/// <c>.git/info/exclude</c> — local to this clone, never committed — so the user's own status and
/// searches do not fill with a second copy of their tree, and their <c>.gitignore</c> is not touched.</para>
///
/// <para>ONLY WORKTREES IN THAT FOLDER ARE THIS PLUGIN'S. Ones the user made elsewhere are theirs: not
/// listed, not merged, never removed.</para>
///
/// <para>FROM THE MAIN CHECKOUT ONLY. Called from inside a worktree, every operation refuses — a
/// worktree made from a worktree would nest checkouts, and a merge from one would merge into the
/// sandbox instead of into the user's branch.</para>
/// </summary>
public sealed class Worktrees(Git git, WorktreeSettings settings)
{
    /// <summary>What a worktree may be called: a folder name and a branch name both, with no way to
    /// climb out of the folder or look like an option to git.</summary>
    private static readonly Regex Name = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,47}$", RegexOptions.Compiled);

    /// <summary>Where the call is, as git sees it.</summary>
    /// <param name="Folder">The agent's folder.</param>
    /// <param name="Top">The main checkout's top folder.</param>
    /// <param name="Prefix">The agent's folder relative to <paramref name="Top"/>, with a trailing slash, or empty.</param>
    private sealed record Place(string Folder, string Top, string Prefix)
    {
        public string Home(WorktreeSettings settings) => Path.GetFullPath(Path.Combine(Folder, settings.Folder));
    }

    /// <summary>One of this plugin's worktrees.</summary>
    /// <param name="Path">Its top folder.</param>
    /// <param name="Branch">Its branch, without <c>refs/heads/</c>, or null when detached.</param>
    private sealed record Tree(string Name, string Path, string? Branch);

    public Outcome Create(string folder, string name, string? baseRef)
    {
        if (Locate(folder, out var place) is { } refusal) return refusal;
        if (CheckName(name) is { } badName) return badName;
        if (CheckFolderSetting() is { } badFolder) return badFolder;

        var branch = settings.BranchPrefix + name;
        if (!git.Run(place.Top, "check-ref-format", "--branch", branch).Ok)
            return Outcome.Refused($"'{branch}' is not a branch name git accepts; choose another name.");

        var path = Path.Combine(place.Home(settings), name);
        if (Directory.Exists(path) || File.Exists(path))
            return Outcome.Refused($"a worktree called '{name}' already exists at {Shown(place, path)}. Use it, or worktree_discard it first.");
        if (git.Run(place.Top, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).Ok)
            return Outcome.Refused($"branch '{branch}' already exists. Choose another name, or delete the branch.");

        var start = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef.Trim();
        // NOTHING THAT LOOKS LIKE AN OPTION reaches git as a ref: `--orphan` here would be read as one.
        if (start.StartsWith('-')) return Outcome.Refused($"'{start}' is not a commit or branch.");
        var commit = git.Run(place.Top, "rev-parse", "--verify", "--quiet", start + "^{commit}");
        if (!commit.Ok) return Outcome.Refused($"'{start}' is not a commit or branch in this repository.");

        if (Exclude(place) is { } excludeFailed) return excludeFailed;

        // A worktree folder someone deleted by hand is still registered; prune it, or git refuses
        // to make a new one at the same path.
        git.Run(place.Top, "worktree", "prune");
        var made = git.Run(place.Top, "worktree", "add", "-b", branch, path, commit.Output.Trim());
        if (!made.Ok) return Outcome.Refused($"git could not create the worktree: {made.Why}");

        var root = Shown(place, Path.Combine(path, place.Prefix));
        var dirty = Count(git.Run(place.Top, "status", "--porcelain").Output);
        var shortSha = commit.Output.Trim()[..Math.Min(7, commit.Output.Trim().Length)];

        var text = $"Created worktree '{name}' at {root}, on branch {branch}, from {shortSha} ({start}).\n"
                 + $"To have a sub-agent work there, spawn it with root \"{root}\". Its edits stay on {branch} "
                 + "until worktree_merge brings them into the current branch; worktree_discard throws them away.";
        // SAID, BECAUSE IT IS THE SURPRISE: a worktree is made from a commit, so work in progress
        // here is not in it, and an agent sent to finish that work would find it missing.
        if (dirty > 0)
            text += $"\nNote: your folder has {dirty} uncommitted change{(dirty == 1 ? "" : "s")}. The worktree starts from the last commit and does not have them.";
        return Outcome.Done(text);
    }

    public Outcome List(string folder)
    {
        if (Locate(folder, out var place) is { } refusal) return refusal;
        var trees = Ours(place);
        if (trees.Count == 0) return Outcome.Done($"No worktrees in {Shown(place, place.Home(settings))}.");

        var lines = new List<string> { $"{trees.Count} worktree{(trees.Count == 1 ? "" : "s")}:" };
        foreach (var tree in trees)
        {
            var (ahead, changed) = Work(place, tree);
            var state = ahead == 0 && changed == 0 ? "nothing to merge"
                : $"{ahead} commit{(ahead == 1 ? "" : "s")} and {changed} uncommitted change{(changed == 1 ? "" : "s")} not merged";
            lines.Add($"  {tree.Name}: {Shown(place, Path.Combine(tree.Path, place.Prefix))}, branch {tree.Branch ?? "(detached)"} — {state}");
        }
        return Outcome.Done(string.Join("\n", lines));
    }

    /// <summary>Most of a diff returned in one call. Past this the model is told to ask for the stat
    /// and read the files it cares about — a diff that size does not fit beside anything else.</summary>
    private const int MaxDiff = 60_000;

    public Outcome Diff(string folder, string name, bool statOnly)
    {
        if (Locate(folder, out var place) is { } refusal) return refusal;
        if (Find(place, name) is not { } tree) return Missing(folder, place, name);

        var mergeBase = Base(place, tree);
        if (mergeBase is null) return Outcome.Refused($"worktree '{name}' shares no history with the current branch.");

        // NEW FILES ARE PART OF THE WORK. `git diff` shows only what git tracks, and an agent's
        // new file is untracked until something adds it; intent-to-add puts it in the diff without
        // staging its content. The worktree's index is the sandbox's own, so marking it costs the
        // user nothing.
        git.Run(tree.Path, "add", "--intent-to-add", "--all");

        var (ahead, changed) = Work(place, tree);
        var header = $"worktree '{name}' (branch {tree.Branch ?? "(detached)"}): {ahead} commit{(ahead == 1 ? "" : "s")} "
                   + $"and {changed} uncommitted change{(changed == 1 ? "" : "s")} since {mergeBase[..7]}.";

        var stat = git.Run(tree.Path, "diff", "--stat=120", mergeBase);
        if (!stat.Ok) return Outcome.Refused($"git could not diff the worktree: {stat.Why}");
        if (stat.Output.Trim().Length == 0) return Outcome.Done(header + "\nNo changes.");
        // FENCED. Tool output is rendered as markdown, where a removed line reads as a bullet point
        // and an underscore pair as emphasis; the diff fence shows it as a diff, coloured.
        var statBlock = "```\n" + stat.Output.TrimEnd() + "\n```";
        if (statOnly) return Outcome.Done(header + "\n\n" + statBlock);

        var diff = git.Run(tree.Path, "diff", mergeBase);
        var body = diff.Output.TrimEnd();
        var cut = body.Length > MaxDiff
            ? $"\n\nThe diff is cut at {MaxDiff} characters of {body.Length}. Call again with stat_only for the full list, and read the files you need."
            : "";
        if (cut.Length > 0) body = body[..MaxDiff];
        // LONGER THAN ANY RUN OF BACKTICKS IN THE DIFF: a changed markdown file carries fences of its
        // own, and the first one would close a fence of the same length early.
        var fence = "```";
        while (body.Contains(fence, StringComparison.Ordinal)) fence += "`";
        return Outcome.Done(header + "\n\n" + statBlock + $"\n\n{fence}diff\n" + body + $"\n{fence}" + cut);
    }

    public Outcome Merge(string folder, string name, string? message, bool keep)
    {
        if (Locate(folder, out var place) is { } refusal) return refusal;
        if (Find(place, name) is not { } tree) return Missing(folder, place, name);
        if (tree.Branch is null) return Outcome.Refused($"worktree '{name}' is not on a branch, so there is nothing to merge.");

        var target = git.Run(place.Top, "symbolic-ref", "--short", "-q", "HEAD");
        if (!target.Ok) return Outcome.Refused("your checkout is not on a branch (detached HEAD); check out the branch to merge into first.");
        var into = target.Output.Trim();
        if (git.Run(place.Top, "rev-parse", "-q", "--verify", "MERGE_HEAD").Ok)
            return Outcome.Refused("your checkout is in the middle of another merge; finish or abort it first.");

        // THE WORKTREE'S UNCOMMITTED WORK IS COMMITTED THERE FIRST. A merge takes commits, and a
        // sub-agent that edited files and stopped has left nothing but uncommitted changes.
        git.Run(tree.Path, "add", "--all");
        if (!git.Run(tree.Path, "diff", "--cached", "--quiet").Ok)
        {
            var commitMessage = string.IsNullOrWhiteSpace(message) ? $"Work from worktree '{name}'" : message.Trim();
            var committed = git.Run(tree.Path, "commit", "-q", "-m", commitMessage);
            if (!committed.Ok) return Outcome.Refused($"git could not commit the worktree's changes: {committed.Why}");
        }

        var ahead = CountCommits(place.Top, $"HEAD..{tree.Branch}");
        if (ahead == 0)
            return Outcome.Done($"Nothing to merge: worktree '{name}' has no work that {into} does not already have."
                + (keep ? "" : " " + Remove(place, tree, merged: true).Text));

        var merged = git.Run(place.Top, "merge", "--no-edit", tree.Branch);
        if (!merged.Ok)
        {
            var conflicts = git.Run(place.Top, "diff", "--name-only", "--diff-filter=U").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // NEVER LEFT HALF-MERGED. A checkout stopped mid-merge with conflict markers in the
            // user's files is the one outcome worse than the merge not happening; undo it, and the
            // work is still on its branch to try again.
            if (git.Run(place.Top, "rev-parse", "-q", "--verify", "MERGE_HEAD").Ok)
                git.Run(place.Top, "merge", "--abort");

            return conflicts.Length > 0
                ? Outcome.Refused($"Merging '{name}' into {into} conflicts in: {string.Join(", ", conflicts)}. "
                    + "The merge was undone — your checkout is as it was, and the worktree still has its work. "
                    + $"Bring {into} into the worktree and resolve the conflicts there, then merge again.")
                : Outcome.Refused($"Nothing was merged; git refused: {merged.Why}");
        }

        var fastForward = merged.Output.Contains("Fast-forward", StringComparison.Ordinal);
        var summary = git.Run(place.Top, "diff", "--shortstat", "ORIG_HEAD", "HEAD").Output.Trim();
        var head = git.Run(place.Top, "rev-parse", "--short", "HEAD").Output.Trim();

        var text = $"Merged worktree '{name}' into {into}"
                 + (fastForward ? $" (fast-forward to {head})" : $" (merge commit {head})")
                 + (summary.Length > 0 ? $": {summary}." : ".");
        return Outcome.Done(text + " " + (keep ? $"The worktree is still at {Shown(place, tree.Path)} on {tree.Branch}." : Remove(place, tree, merged: true).Text));
    }

    public Outcome Discard(string folder, string name)
    {
        if (Locate(folder, out var place) is { } refusal) return refusal;
        if (Find(place, name) is not { } tree) return Missing(folder, place, name);

        var (ahead, changed) = Work(place, tree);
        var removed = Remove(place, tree, merged: false);
        if (!removed.Complete) return Outcome.Refused($"Worktree '{name}' was not fully discarded. {removed.Text}");

        var lost = ahead == 0 && changed == 0 ? "It held no work that was not merged."
            : $"{ahead} commit{(ahead == 1 ? "" : "s")} and {changed} uncommitted change{(changed == 1 ? "" : "s")} are gone.";
        return Outcome.Done($"Discarded worktree '{name}'. {lost}");
    }

    /// <summary>
    /// Removes a worktree's folder and branch, and says so — or says what was left behind.
    ///
    /// <para>AFTER A MERGE THE BRANCH IS DELETED WITH <c>-d</c>, which git refuses for a branch not
    /// fully merged: the merge just said it was, and if git disagrees the branch is the work, so it
    /// stays. Only a discard, which the user approved as one, forces it.</para>
    /// </summary>
    private (bool Complete, string Text) Remove(Place place, Tree tree, bool merged)
    {
        var removed = git.Run(place.Top, "worktree", "remove", "--force", tree.Path);
        if (!removed.Ok) return (false, $"Its folder could not be removed: {removed.Why}");

        if (tree.Branch is not null)
        {
            var deleted = git.Run(place.Top, "branch", merged ? "-d" : "-D", tree.Branch);
            if (!deleted.Ok) return (false, $"Its folder is removed, but branch {tree.Branch} stays: {deleted.Why}");
        }

        // An empty .worktrees folder is clutter the user did not make.
        try
        {
            var home = place.Home(settings);
            if (Directory.Exists(home) && !Directory.EnumerateFileSystemEntries(home).Any()) Directory.Delete(home);
        }
        catch (IOException) { }

        return (true, "The worktree and its branch are removed.");
    }

    /// <summary>Finds the repository, or refuses: not in one, or in one of its worktrees.</summary>
    private Outcome? Locate(string folder, out Place place)
    {
        place = new Place(folder, "", "");
        var top = git.Run(folder, "rev-parse", "--show-toplevel");
        if (!top.Ok) return Outcome.Refused($"{folder} is not inside a git repository.");

        var gitDir = git.Run(folder, "rev-parse", "--path-format=absolute", "--git-dir").Output.Trim();
        var common = git.Run(folder, "rev-parse", "--path-format=absolute", "--git-common-dir").Output.Trim();
        if (gitDir.Length == 0 || common.Length == 0) return Outcome.Refused($"git could not describe the repository at {folder}.");
        if (!SamePath(gitDir, common))
            return Outcome.Refused("this folder is itself a worktree. Worktrees are made, merged and discarded from the main checkout.");

        var prefix = git.Run(folder, "rev-parse", "--show-prefix").Output.Trim();
        place = new Place(RealPath(folder), top.Output.Trim(), prefix);
        return null;
    }

    private static Outcome? CheckName(string name) =>
        Name.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) && !name.EndsWith(".lock", StringComparison.Ordinal)
            ? null
            : Outcome.Refused($"'{name}' cannot name a worktree: use letters, digits, '.', '_' and '-', at most 48, starting with a letter or digit.");

    private Outcome? CheckFolderSetting()
    {
        var f = settings.Folder.Replace('\\', '/');
        return Path.IsPathRooted(f) || f.Split('/').Contains("..") || f.Trim('/').Length == 0
            ? Outcome.Refused($"the folder setting '{settings.Folder}' must be a folder inside yours, like .worktrees.")
            : null;
    }

    /// <summary>
    /// Adds the worktrees folder to the repository's exclude file, once.
    ///
    /// <para>ANCHORED AT THE REPOSITORY'S TOP, with the agent's folder in front: <c>/sub/.worktrees/</c>,
    /// not <c>.worktrees/</c>, which would also hide any folder of that name the user has elsewhere.</para>
    /// </summary>
    private Outcome? Exclude(Place place)
    {
        var path = git.Run(place.Top, "rev-parse", "--path-format=absolute", "--git-path", "info/exclude").Output.Trim();
        if (path.Length == 0) return Outcome.Refused("git could not say where this repository's exclude file is.");

        var entry = "/" + place.Prefix + settings.Folder.Replace('\\', '/').Trim('/') + "/";
        try
        {
            var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
            if (existing.Any(line => line.Trim() == entry)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var separator = existing.Length > 0 && File.ReadAllText(path) is var all && all.Length > 0 && !all.EndsWith('\n') ? "\n" : "";
            File.AppendAllText(path, $"{separator}# worktrees made by cxagent's worktree plugin\n{entry}\n");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome.Refused($"could not add {entry} to {path}: {ex.Message}");
        }
    }

    /// <summary>The worktrees git knows of whose folder is directly inside the worktrees folder.</summary>
    private List<Tree> Ours(Place place)
    {
        var home = RealPath(place.Home(settings));
        var trees = new List<Tree>();
        string? path = null, branch = null;

        foreach (var line in (git.Run(place.Top, "worktree", "list", "--porcelain").Output + "\n\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { path = line[9..]; branch = null; }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = line[18..];
            else if (line.Length == 0 && path is not null)
            {
                if (SamePath(RealPath(Path.GetDirectoryName(path)!), home))
                    trees.Add(new Tree(Path.GetFileName(path), path, branch));
                path = null;
            }
        }
        return trees.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private Tree? Find(Place place, string name) => Ours(place).FirstOrDefault(t => t.Name == name);

    private Outcome Missing(string folder, Place place, string name)
    {
        var names = Ours(place).Select(t => t.Name).ToList();
        return Outcome.Refused($"no worktree called '{name}' in {Shown(place, place.Home(settings))}."
            + (names.Count > 0 ? $" There is: {string.Join(", ", names)}." : " There are none."));
    }

    /// <summary>Where the worktree's branch left the current one — what its work is measured from.</summary>
    private string? Base(Place place, Tree tree)
    {
        var result = git.Run(place.Top, "merge-base", "HEAD", tree.Branch ?? git.Run(tree.Path, "rev-parse", "HEAD").Output.Trim());
        return result.Ok ? result.Output.Trim() : null;
    }

    /// <summary>Commits on the worktree's branch the current branch lacks, and its uncommitted changes.</summary>
    private (int Ahead, int Changed) Work(Place place, Tree tree)
    {
        var ahead = tree.Branch is null ? 0 : CountCommits(place.Top, $"HEAD..{tree.Branch}");
        return (ahead, Count(git.Run(tree.Path, "status", "--porcelain").Output));
    }

    private int CountCommits(string top, string range) =>
        int.TryParse(git.Run(top, "rev-list", "--count", range).Output.Trim(), out var n) ? n : 0;

    private static int Count(string porcelain) => porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>A path as the agent would write it: relative to its folder, with forward slashes.</summary>
    private static string Shown(Place place, string path)
    {
        var relative = Path.GetRelativePath(place.Folder, path).Replace('\\', '/').TrimEnd('/');
        return relative.Length == 0 ? "." : relative;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// A path with every symlink along it followed — git reports a worktree's real path, and a
    /// folder reached through a link (macOS's /tmp, for one) would otherwise never match it.
    /// </summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (IOException) { }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
