namespace CxAgent.Core.Sandbox;

/// <summary>
/// The parts of a git repository the sandbox needs to know about: where a linked worktree's shared <c>.git</c>
/// is, and which files inside a git folder run code.
/// </summary>
internal static class GitFolders
{
    /// <summary>
    /// The main repository's <c>.git</c> folder when <paramref name="root"/> is a linked worktree, or null.
    ///
    /// <para>A worktree's <c>.git</c> is a FILE holding <c>gitdir: &lt;main&gt;/.git/worktrees/&lt;name&gt;</c>, and
    /// that folder's <c>commondir</c> names the shared <c>.git</c> relative to it. A submodule's <c>.git</c> file
    /// points into the parent's <c>.git/modules</c> with no <c>commondir</c>, and is not a worktree.</para>
    /// </summary>
    /// <param name="root">The folder that may be a worktree.</param>
    public static string? CommonDir(string root)
    {
        try
        {
            var pointer = Path.Combine(root, ".git");
            if (!File.Exists(pointer)) return null;
            var line = File.ReadLines(pointer).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null) return null;
            var gitdir = Path.GetFullPath(line["gitdir:".Length..].Trim(), root);
            var commondir = Path.Combine(gitdir, "commondir");
            if (!File.Exists(commondir)) return null;
            var common = Path.GetFullPath(File.ReadAllText(commondir).Trim(), gitdir);
            return Directory.Exists(common) ? Permissions.PathResolver.RealFolder(common) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every submodule's own git folder under <paramref name="gitDir"/>'s <c>modules</c>, nested ones too — the
    /// folders that must be mount points so none can be renamed and rebuilt.</summary>
    public static IEnumerable<string> SubmoduleGitDirs(string gitDir)
    {
        var modules = Path.Combine(gitDir, "modules");
        if (!Directory.Exists(modules)) return [];
        try
        {
            return [.. Directory.EnumerateDirectories(modules, "*", SearchOption.AllDirectories)
                .Where(d => File.Exists(Path.Combine(d, "HEAD")))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The folder <c>core.hooksPath</c> in <paramref name="gitDir"/>'s config names, as a full path, or null. Hooks
    /// there run on the next git command like those in <c>.git/hooks</c>, so it is held read-only the same way. A
    /// relative path is relative to the working tree, as git reads it.
    /// </summary>
    public static string? HooksPath(string gitDir, string workTree)
    {
        try
        {
            var config = Path.Combine(gitDir, "config");
            if (!File.Exists(config)) return null;
            var inCore = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inCore = line.StartsWith("[core]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inCore) continue;
                var eq = line.IndexOf('=');
                if (eq < 0 || !line[..eq].Trim().Equals("hooksPath", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(eq + 1)..].Trim().Trim('"');
                if (value.Length == 0) return null;
                if (value.StartsWith("~/", StringComparison.Ordinal))
                    value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
                return Path.GetFullPath(value, workTree);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    /// <summary>
    /// The paths inside a git folder that make git run something: its hooks, its config (where
    /// <c>core.hooksPath</c>, <c>core.fsmonitor</c> and filter drivers are set), each worktree's
    /// <c>config.worktree</c>, and the same for every submodule's git folder under <c>modules</c>.
    ///
    /// <para>ONLY THESE, NOT THE FOLDER. Objects, refs, the index and logs must stay writable or add and commit
    /// fail; none of them runs anything.</para>
    /// </summary>
    /// <param name="gitDir">A <c>.git</c> folder, or a submodule's folder under <c>modules</c>.</param>
    public static IEnumerable<string> ExecutingParts(string gitDir)
    {
        var found = new List<string>();
        Collect(gitDir, found, submodules: true);
        return found;
    }

    private static void Collect(string gitDir, List<string> found, bool submodules)
    {
        foreach (var part in new[] { "hooks", "config" })
        {
            var path = Path.Combine(gitDir, part);
            if (Directory.Exists(path) || File.Exists(path)) found.Add(path);
        }

        try
        {
            var worktrees = Path.Combine(gitDir, "worktrees");
            if (Directory.Exists(worktrees))
                found.AddRange(Directory.EnumerateDirectories(worktrees)
                    .Select(w => Path.Combine(w, "config.worktree")).Where(File.Exists));

            // EVERY SUBMODULE'S GIT FOLDER, nested ones included (modules/a/modules/b), found in one walk: a folder is
            // one when it has a HEAD. The walk already reaches the nested ones, so they are not walked again.
            var modules = Path.Combine(gitDir, "modules");
            if (submodules && Directory.Exists(modules))
                foreach (var dir in Directory.EnumerateDirectories(modules, "*", SearchOption.AllDirectories))
                    if (File.Exists(Path.Combine(dir, "HEAD"))) Collect(dir, found, submodules: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
