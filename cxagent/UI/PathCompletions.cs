namespace CxAgent.UI;

/// <summary>One path an <c>@</c> reference can name.</summary>
/// <param name="Path">What the completion inserts — relative to the root, or absolute when the
/// prefix was.</param>
/// <param name="Display">What the menu shows: the path, with a trailing separator on a directory.</param>
/// <param name="IsDirectory">Whether it is a directory, which decides the trailing separator.</param>
public sealed record PathMatch(string Path, string Display, bool IsDirectory);

/// <summary>
/// The paths an <c>@</c> can complete to.
///
/// <para>NOTHING IS EXCLUDED — not <c>bin/</c>, not <c>obj/</c>, not <c>node_modules/</c>, not
/// <c>.git/</c>, not what <c>.gitignore</c> names. The file tree excludes those because nobody
/// BROWSES generated output; this is not browsing. Someone typing <c>@obj/Debug/</c> has been
/// specific enough that second-guessing them is the wrong answer, and a completion that hides a file
/// you know exists is one you stop trusting.</para>
///
/// <para>OUTSIDE THE ROOT IS ALLOWED, and it costs nothing: completion is not the permission
/// boundary. A read whose path resolves outside the session's folder prompts — PermissionPolicy is
/// explicit about it — so refusing to COMPLETE such a path would restrict the composer somewhere the
/// permission model does not, and buy no safety at all.</para>
/// </summary>
public static class PathCompletions
{
    /// <summary>
    /// Paths matching <paramref name="prefix"/>, directories first.
    ///
    /// <para>A SEPARATOR IN THE PREFIX ANCHORS THE SEARCH. <c>src/UI/Sh</c> reads one directory;
    /// a bare <c>Sh</c> matches a filename anywhere below the root. That split is what keeps a deep
    /// path reachable without walking the world for every keystroke — the recursive case is the one
    /// with the least typed, and it is bounded by <paramref name="limit"/>.</para>
    ///
    /// <para>CAPPED, because a menu is a list someone reads: forty rows is already past what anyone
    /// scans, and the cap also bounds the walk on a repository nobody expected.</para>
    /// </summary>
    /// <param name="root">The session's working directory — what a relative prefix is relative to.</param>
    /// <param name="prefix">What was typed after the <c>@</c>. Empty offers everything.</param>
    /// <param name="limit">Most rows to return.</param>
    public static IReadOnlyList<PathMatch> Find(string root, string? prefix, int limit = 40)
    {
        prefix = (prefix ?? string.Empty).Replace('\\', '/');

        // AN ABSOLUTE OR ~ PREFIX LEAVES THE ROOT ENTIRELY, and then the completion is absolute too:
        // splicing a relative path back into a sentence that began with "/" would name a different
        // file from the one the user picked.
        var absolute = prefix.StartsWith('/') || prefix.StartsWith('~');
        var expanded = prefix.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           prefix.TrimStart('~').TrimStart('/'))
            : prefix;

        var lastSlash = expanded.LastIndexOf('/');
        var hasDirectory = lastSlash >= 0;

        var searchDir = hasDirectory
            ? (absolute ? expanded[..(lastSlash + 1)] : Path.Combine(root, expanded[..(lastSlash + 1)]))
            : root;
        var needle = hasDirectory ? expanded[(lastSlash + 1)..] : expanded;

        // ONE DIRECTORY WHEN THE PREFIX NAMES ONE, RECURSIVE OTHERWISE. See the summary: the
        // recursive case is the one with the least typed, so it is also the one that must be capped.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = !hasDirectory,
            IgnoreInaccessible = true,
            // A SYMLINKED DIRECTORY CAN POINT AT ITS OWN PARENT, and a walk that followed one would
            // not return. Skipping them costs a rare real path and removes a hang.
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        List<PathMatch> hits = [];
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(searchDir, "*", options))
            {
                var name = Path.GetFileName(entry);
                if (needle.Length > 0
                    && !name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isDir = Directory.Exists(entry);

                // RELATIVE TO THE ROOT unless the user started absolute — what goes into the
                // sentence should read the way they were already typing it.
                var shown = absolute
                    ? entry
                    : Path.GetRelativePath(root, entry).Replace('\\', '/');

                hits.Add(new PathMatch(
                    isDir ? shown + '/' : shown,
                    isDir ? shown + '/' : shown,
                    isDir));

                // COUNTED AGAINST THE CAP AS THEY ARRIVE, not filtered afterwards: the point of the
                // cap is to stop walking, and a walk that completes before being trimmed has already
                // paid for the whole tree.
                if (hits.Count >= limit * 4) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            // A ROOT THAT IS NOT THERE IS AN EMPTY MENU, not a crash in the composer. Typing a path
            // that does not exist yet is ordinary — the directory half of it may be being typed.
            return [];
        }

        // DIRECTORIES FIRST, THEN ORDINAL. A list that reshuffled as it narrowed would move the row
        // under the selection while someone was reaching for it.
        return [.. hits
            .OrderByDescending(h => h.IsDirectory)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .Take(limit)];
    }
}
