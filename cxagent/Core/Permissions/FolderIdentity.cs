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

            var created = info.CreationTimeUtc;

            // NO BIRTH TIME, NO SUFFIX. Epoch means the filesystem does not record one; adding
            // "#0" would look like an identity while distinguishing nothing, and would then differ
            // from a rule written on a filesystem that DOES report one — turning a missing feature
            // into a mismatch.
            if (created == DateTime.UnixEpoch || created == default) return path;

            // ...AND LINUX DOES NOT REPORT ONE, whatever the property is called.
            //
            // .NET has no access to statx's btime on Linux, so DirectoryInfo.CreationTimeUtc returns
            // the CHANGE time — which moves every time anything inside the directory is created,
            // renamed or removed. That is not an identity, it is a modification clock, and scoping
            // rules by it means every grant and every trust decision dies the moment the agent
            // writes a file. Measured on a real drive: the same folder trusted twice in one session,
            // two entries in the store minutes apart, and a second trust prompt for a directory the
            // user had already trusted — because cloning a repo into it moved the clock.
            //
            // DETECTED RATHER THAN ASSUMED. Where a true birth time exists it is almost always
            // EARLIER than the last write; where the platform substitutes ctime the two are the same
            // value to the tick, because both come from the same field. Equal means the suffix would
            // be a modification time wearing an identity's clothes, so it is dropped and the scope
            // falls back to the path — exactly the behaviour before this type existed, and no worse.
            if (created == info.LastWriteTimeUtc) return path;

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
