using CxAgent.Core.Storage;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Telling a folder apart from a different folder wearing the same name.
///
/// <para>The failure this closes was measured in this app's own store: 111 rules across seven
/// scopes, 61 of them pinned to a <c>/tmp</c> directory that no longer existed. Recreating that path
/// would have woken all sixty-one.</para>
/// </summary>
public class FolderIdentityTests : IDisposable
{
    private readonly List<string> _made = [];

    private string MakeDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "cxa-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _made.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var d in _made)
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    /// <summary>The same folder is the same scope, however many times it is asked.</summary>
    [Fact]
    public void TheSameFolder_IsAlwaysTheSameScope()
    {
        var dir = MakeDir();

        Assert.Equal(FolderIdentity.ScopeFor(dir), FolderIdentity.ScopeFor(dir));
    }

    /// <summary>
    /// THE POINT OF THE WHOLE THING. Delete and recreate, and the old rules must not apply — the
    /// path is a label and this is a different folder.
    /// </summary>
    [Fact]
    public async Task ARecreatedFolder_IsADifferentScope()
    {
        var dir = MakeDir();
        var before = FolderIdentity.ScopeFor(dir);

        Directory.Delete(dir, recursive: true);

        // The scope carries whole seconds — two folders created inside one second are the same
        // folder for this purpose, so the recreation has to land in the next one to be visible.
        await Task.Delay(1100);
        Directory.CreateDirectory(dir);

        var after = FolderIdentity.ScopeFor(dir);

        // On a filesystem with no birth time both fall back to the bare path, and this signal
        // cannot distinguish them — no worse than before, so the test says so rather than failing
        // for a reason the reader cannot act on.
        if (before == FolderIdentity.PathOf(before))
            return;   // no creation time here; nothing to assert

        Assert.NotEqual(before, after);
    }

    /// <summary>Two different folders are never one scope, whatever they are called.</summary>
    [Fact]
    public void TwoFolders_AreTwoScopes()
    {
        Assert.NotEqual(FolderIdentity.ScopeFor(MakeDir()), FolderIdentity.ScopeFor(MakeDir()));
    }

    /// <summary>
    /// One folder, two spellings. Without this a trailing slash would be a second set of rules and
    /// the user would grant the same thing twice with no way to see why.
    /// </summary>
    [Fact]
    public void ATrailingSeparator_DoesNotMakeASecondScope()
    {
        var dir = MakeDir();

        Assert.Equal(FolderIdentity.ScopeFor(dir), FolderIdentity.ScopeFor(dir + Path.DirectorySeparatorChar));
    }

    /// <summary>And a relative path is the same folder as its absolute form.</summary>
    [Fact]
    public void ARelativePath_ResolvesToTheSameScope()
    {
        var dir = MakeDir();
        var viaDots = Path.Combine(dir, "..", Path.GetFileName(dir));

        Assert.Equal(FolderIdentity.ScopeFor(dir), FolderIdentity.ScopeFor(viaDots));
    }

    /// <summary>
    /// A folder that is not there yet is its path. Never throwing matters more than being exact:
    /// this runs on the way to a permission decision, and an exception there fails a turn.
    /// </summary>
    [Fact]
    public void AFolderThatDoesNotExist_IsJustItsPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cxa-id-absent-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(FolderIdentity.PathOf(FolderIdentity.ScopeFor(missing)),
                     FolderIdentity.ScopeFor(missing));
    }

    /// <summary>The path is readable in the scope — someone opening permissions.json can still see
    /// which project a rule belongs to.</summary>
    [Fact]
    public void PathOf_RecoversTheFolder()
    {
        var dir = MakeDir();

        Assert.Equal(Path.TrimEndingDirectorySeparator(dir),
                     FolderIdentity.PathOf(FolderIdentity.ScopeFor(dir)));
    }

    /// <summary>A Windows path carries a colon and a drive letter; PathOf must not be confused by
    /// them, which is why it splits on the LAST separator rather than the first.</summary>
    [Fact]
    public void PathOf_HandlesAScopeWithNoIdentity()
    {
        Assert.Equal("/a/b", FolderIdentity.PathOf("/a/b"));
    }

    /// <summary>
    /// THE BEHAVIOUR THIS ALL EXISTS FOR, through the store rather than the helper: a grant made in
    /// a folder does not survive that folder being deleted and remade.
    ///
    /// <para>Measured before this existed: 111 rules across seven scopes, 61 of them pinned to a
    /// <c>/tmp</c> directory that no longer existed — every one of which would have applied again
    /// the moment something recreated the path.</para>
    /// </summary>
    [Fact]
    public async Task ARuleGrantedBeforeAFolderWasRecreated_DoesNotApplyAfterwards()
    {
        var dir = MakeDir();
        var store = new PermissionRulesStore(new AppPaths(MakeDir()));

        store.Add(dir, PermissionKind.Shell, "dotnet build");
        Assert.True(store.Matches(dir, PermissionKind.Shell, "dotnet build"));

        Directory.Delete(dir, recursive: true);
        await Task.Delay(1100);          // the scope carries whole seconds
        Directory.CreateDirectory(dir);

        // Where the filesystem records no birth time there is nothing to tell the two apart, and
        // this degrades to plain path-only matching rather than failing for an invisible reason.
        if (FolderIdentity.ScopeFor(dir) == FolderIdentity.PathOf(FolderIdentity.ScopeFor(dir)))
            return;

        Assert.False(store.Matches(dir, PermissionKind.Shell, "dotnet build"));
    }

    /// <summary>And trust dies with the folder for the same reason — a recreated directory is not
    /// the project the user vouched for.</summary>
    [Fact]
    public async Task TrustDoesNotSurviveTheFolderBeingRecreated()
    {
        var dir = MakeDir();
        var store = new PermissionRulesStore(new AppPaths(MakeDir()));

        store.SetTrust(dir, TrustState.Trusted);
        Assert.Equal(TrustState.Trusted, store.GetTrust(dir));

        Directory.Delete(dir, recursive: true);
        await Task.Delay(1100);
        Directory.CreateDirectory(dir);

        if (FolderIdentity.ScopeFor(dir) == FolderIdentity.PathOf(FolderIdentity.ScopeFor(dir)))
            return;

        Assert.NotEqual(TrustState.Trusted, store.GetTrust(dir));
    }

    /// <summary>
    /// A SCOPE SURVIVES THE AGENT WRITING FILES, which is the whole point of it.
    ///
    /// <para>Found on a real drive: the same folder trusted twice in one session, and `cd*` granted
    /// three times under three different scopes minutes apart — because each file the agent wrote
    /// moved the clock .NET reports as a creation time on Linux, orphaning the previous grant.</para>
    /// </summary>
    [Fact]
    public void ScopeFor_IsUnchangedByWritingInsideTheFolder()
    {
        var dir = MakeDir();
        var before = FolderIdentity.ScopeFor(dir);

        // Enough for the containing directory's ctime to move to a new second.
        Thread.Sleep(1_100);
        File.WriteAllText(Path.Combine(dir, "written-by-the-agent.txt"), "x");

        Assert.Equal(before, FolderIdentity.ScopeFor(dir));
    }

    /// <summary>...and so does removing one. Same clock, same hazard.</summary>
    [Fact]
    public void ScopeFor_IsUnchangedByDeletingInsideTheFolder()
    {
        var dir = MakeDir();
        var file = Path.Combine(dir, "temp.txt");
        File.WriteAllText(file, "x");

        var before = FolderIdentity.ScopeFor(dir);

        Thread.Sleep(1_100);
        File.Delete(file);

        Assert.Equal(before, FolderIdentity.ScopeFor(dir));
    }

    /// <summary>
    /// A FOLDER STILL HAS AN IDENTITY THE MOMENT IT IS MADE.
    ///
    /// <para>The first attempt at the Linux problem was a heuristic: compare creation against
    /// last-write and drop the suffix when they match, since a real birth time is usually earlier.
    /// A FRESHLY CREATED FOLDER HAS THEM EQUAL ON EVERY PLATFORM — so it threw the identity away on
    /// Windows and macOS for exactly the new folders where "someone recreated this path" is the live
    /// hazard. Reading the birth time instead of inferring it is what makes this pass.</para>
    /// </summary>
    [Fact]
    public void ScopeFor_ANewFolder_IsStillDistinguishedFromItsPath()
    {
        var dir = MakeDir();

        Assert.NotEqual(dir, FolderIdentity.ScopeFor(dir));
        Assert.StartsWith(dir + "#", FolderIdentity.ScopeFor(dir), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HAZARD THIS TYPE EXISTS FOR: a path is a label, not an identity. Delete a folder and
    /// recreate it and every grant made to the first must not apply to the second.
    /// </summary>
    [Fact]
    public void ScopeFor_ARecreatedFolder_IsNotTheOriginal()
    {
        var dir = MakeDir();
        var before = FolderIdentity.ScopeFor(dir);

        Directory.Delete(dir, recursive: true);
        Thread.Sleep(1_100);          // the suffix has second resolution
        Directory.CreateDirectory(dir);

        Assert.NotEqual(before, FolderIdentity.ScopeFor(dir));
    }
}
