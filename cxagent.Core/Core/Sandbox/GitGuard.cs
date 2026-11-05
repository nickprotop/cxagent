namespace CxAgent.Core.Sandbox;

/// <summary>
/// What a contained command can change that mounts cannot hold, read as a list of facts so a before and an after can be
/// compared.
///
/// <para>WHY AFTER THE FACT. A <c>.git/commondir</c> sends git to a config elsewhere, and a rewritten <c>.git</c> pointer
/// to another git folder; either can set <c>core.fsmonitor</c>, which runs on the next <c>git status</c> — yours, or
/// cxagent's own — outside the sandbox. A protected folder the project lacks (<c>.cxagent</c>, <c>.github</c>) cannot be
/// held read-only without creating it in the repository first. None of these can be refused by a bind, so they are
/// noticed instead, before the next command and after each one.</para>
/// </summary>
public static class GitGuard
{
    /// <summary>The facts about <paramref name="root"/> that matter: what <c>.git</c> is and where it points, any
    /// <c>commondir</c> or <c>gitdir</c> redirect, and which executable-config names exist at the root.</summary>
    /// <param name="root">The writable root of a contained command.</param>
    public static IReadOnlyList<string> Snapshot(string root)
    {
        var facts = new List<string>();
        try
        {
            var git = Path.Combine(root, ".git");
            if (File.Exists(git)) facts.Add($".git: file {Read(git)}");
            else if (Directory.Exists(git))
            {
                facts.Add(".git: folder");
                foreach (var redirect in new[] { "commondir", "gitdir" })
                    if (File.Exists(Path.Combine(git, redirect))) facts.Add($".git/{redirect}: {Read(Path.Combine(git, redirect))}");
            }

            if (GitFolders.CommonDir(root) is { } common)
            {
                if (File.Exists(Path.Combine(common, "commondir"))) facts.Add($"{common}/commondir: {Read(Path.Combine(common, "commondir"))}");
                var worktrees = Path.Combine(common, "worktrees");
                if (Directory.Exists(worktrees))
                    foreach (var w in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
                        foreach (var redirect in new[] { "commondir", "gitdir" })
                            if (File.Exists(Path.Combine(w, redirect))) facts.Add($"{w}/{redirect}: {Read(Path.Combine(w, redirect))}");
            }

            foreach (var name in Permissions.PermissionPolicy.ExecutableConfigNames.Where(n => n != ".git"))
                if (Directory.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, name))) facts.Add($"{name}: present");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { facts.Add($"unreadable: {ex.Message}"); }
        return facts;
    }

    /// <summary>What differs, one line per path, in words: empty when nothing does.</summary>
    /// <param name="before">The earlier snapshot.</param>
    /// <param name="after">The later one.</param>
    public static IReadOnlyList<string> Changes(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        static string Key(string fact) => fact[..Math.Max(0, fact.IndexOf(':'))];
        var keys = before.Concat(after).Select(Key).Distinct(StringComparer.Ordinal);
        var changes = new List<string>();
        foreach (var key in keys)
        {
            var was = before.FirstOrDefault(f => Key(f) == key);
            var now = after.FirstOrDefault(f => Key(f) == key);
            if (was == now) continue;
            changes.Add(was is null ? $"{key} appeared" : now is null ? $"{key} removed" : $"{key} changed");
        }
        return changes;
    }

    private static string Read(string file) => File.ReadAllText(file).Trim();
}
