namespace CxAgent.Core.Permissions;

/// <summary>
/// Which folder a permission rule belongs to — the PATH plus enough to tell a folder apart from a
/// different folder wearing the same name.
///
/// <para>WHY A PATH IS NOT ENOUGH. A path is a label, not an identity: <c>rm -rf /tmp/work</c>
/// followed by <c>mkdir /tmp/work</c> produces a different directory with the same name, and every
/// rule granted to the first silently applies to the second. Measured in this app's own store: 111
/// accumulated rules across seven scopes, 61 of them pinned to a <c>/tmp</c> directory that no
/// longer exists. Recreate that path and sixty-one grants wake up.</para>
///
/// <para>CREATION TIME WHERE THERE IS ONE, because it is the one signal .NET gives portably. An inode is exact and
/// costs a P/Invoke per platform — <c>stat</c> on Unix, <c>GetFileInformationByHandle</c> on
/// Windows, where the index is documented as unstable on ReFS. That is a lot of native surface for
/// a check that must never be wrong. <c>DirectoryInfo.CreationTimeUtc</c> needs none of it.</para>
///
/// <para>THE EPOCH CASE IS NOT A PROBLEM, which is worth stating because it looks like one. Some
/// filesystems report no birth time and return the epoch. That would matter if this measured AGE —
/// it does not. The only question asked is "are these the same folder", so two folders that both
/// report the epoch are indistinguishable by this signal and fall back to path alone, which is
/// exactly today's behaviour and no worse.</para>
/// </summary>
public static class FolderIdentity
{
    /// <summary>
    /// The scope string a rule is stored and matched under.
    ///
    /// <para>Shaped so the path stays readable in the file — someone opening permissions.json should
    /// still be able to see which project a rule belongs to without decoding anything.</para>
    /// </summary>
    /// <remarks>
    /// Never throws. A folder that cannot be stat'd gets its bare path, which means an old rule may
    /// still match — the same behaviour as before this existed, and a miss here costs one prompt
    /// while a spurious match costs a grant nobody gave.
    /// </remarks>
    public static string ScopeFor(string folder)
    {
        var path = Normalise(folder);

        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return path;

            // THE BIRTH TIME, READ RATHER THAN GUESSED AT.
            //
            // DirectoryInfo.CreationTimeUtc is the right value on Windows and macOS. On Linux it is
            // the CHANGE time — .NET cannot reach statx through libc's stat — and that clock moves
            // whenever anything inside the directory is created, renamed or removed. Scoping rules
            // by it meant every grant died the moment the agent wrote a file: measured on one drive
            // as the same folder trusted twice and `cd*` granted three times under three scopes.
            //
            // FolderBirthTime asks the kernel directly on Linux and returns null everywhere else,
            // so the BCL keeps the platforms where it is already correct.
            var created = FolderBirthTime.Of(path) ?? info.CreationTimeUtc;

            // NO BIRTH TIME, NO SUFFIX. Epoch means the filesystem does not record one; adding
            // "#0" would look like an identity while distinguishing nothing, and would then differ
            // from a rule written on a filesystem that DOES report one — turning a missing feature
            // into a mismatch.
            if (created == DateTime.UnixEpoch || created == default) return path;

            // SECONDS, not ticks. Two folders created in the same second are the same folder for
            // this purpose, and sub-second precision differs between filesystems — a rule written
            // on one and matched on another would miss for a reason nobody could see.
            return $"{path}#{created:yyyyMMddTHHmmss}";
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// Whether this scope names a folder the filesystem can actually distinguish.
    ///
    /// <para>FALSE MEANS THE SCOPE IS A BARE PATH — no birth time was available, so
    /// <see cref="ScopeFor"/> could not disambiguate this folder from any other folder that has ever
    /// occupied the same path. A grant made before an `rm -rf dir &amp;&amp; mkdir dir` then applies
    /// to whatever is there now, which is the precise failure this type exists to prevent.</para>
    ///
    /// <para>THE CONSEQUENCE IS NOT SYMMETRIC, which is why this is a question a caller asks rather
    /// than a refusal made here. A stale RULE permits one command shape inside a folder; stale TRUST
    /// unlocks every silent read and write in it, and is the precondition for the free pass that
    /// reads files by name. Trust is the one that must not be inherited by a stranger.</para>
    /// </summary>
    public static bool IsDistinguishable(string scope)
    {
        // A SUFFIX MEANS A BIRTH TIME WAS READ, which is the whole question.
        if (scope.Contains('#', StringComparison.Ordinal)) return true;

        // NO SUFFIX HAS TWO CAUSES AND THEY ARE NOT THE SAME RISK. A folder that does not exist has
        // no identity to confuse with anything — nothing is running there, and refusing trust for it
        // would break every caller reasoning about a path in the abstract. A folder that EXISTS and
        // still has no birth time is the dangerous case: it is indistinguishable from every folder
        // that has ever occupied that path, including the one a grant was made for.
        try
        {
            return !Directory.Exists(scope);
        }
        catch (Exception)
        {
            return false;   // cannot tell: treat as indistinguishable, which is the cautious answer
        }
    }

    /// <summary>
    /// The path part of a scope, for display. A user reading a prompt or a listing wants the folder,
    /// not the timestamp that disambiguates it.
    /// </summary>
    public static string PathOf(string scope)
    {
        var hash = scope.LastIndexOf('#');
        return hash > 0 ? scope[..hash] : scope;
    }

    /// <summary>
    /// Trailing separators removed, so <c>/a/b</c> and <c>/a/b/</c> are one scope. Done here rather
    /// than trusted to callers: two spellings of one folder would be two sets of rules, and the user
    /// would grant the same thing twice without ever seeing why.
    /// </summary>
    private static string Normalise(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception)
        {
            return folder;
        }
    }
}
