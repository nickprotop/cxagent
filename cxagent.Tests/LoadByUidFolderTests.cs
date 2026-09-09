using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That the store itself refuses a uid from another folder when asked to.
///
/// <para>THE GUARD USED TO LIVE ONLY IN THE CALLER. `/sessions resume` resolved a typed uid against
/// rows it had already filtered by working directory — a real guard, but one that holds for exactly
/// as long as every caller remembers to filter first, and that says nothing about the uid it finally
/// passes. Stating the folder makes the refusal the store's own.</para>
/// </summary>
public class LoadByUidFolderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-uidscope-" + Guid.NewGuid().ToString("N"));

    private readonly FolderSessionStore _store;

    public LoadByUidFolderTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new FolderSessionStore(new AppPaths(_dir));

        _store.SaveTurn("01AAAAAAAAAAAAAAAAAAAAAAAA", [], 1, 1, workingDir: "/work/alpha");
        _store.SaveTurn("01BBBBBBBBBBBBBBBBBBBBBBBB", [], 1, 1, workingDir: "/work/beta");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A UID IN THE NAMED FOLDER IS FOUND.</summary>
    [Fact]
    public void AUidInsideTheFolderResolves()
    {
        var found = _store.LoadByUid("01AAAAAAAAAAAAAAAAAAAAAAAA", withinFolder: "/work/alpha");

        Assert.NotNull(found.Session);
    }

    /// <summary>AND ONE FROM ANOTHER FOLDER IS NOT — even though the uid is exact and the row
    /// exists.</summary>
    [Fact]
    public void AUidFromAnotherFolderIsRefused()
    {
        var found = _store.LoadByUid("01BBBBBBBBBBBBBBBBBBBBBBBB", withinFolder: "/work/alpha");

        Assert.Null(found.Session);
    }

    /// <summary>
    /// AND NO FOLDER MEANS ANY FOLDER, which is what `--resume` needs: naming a session on the
    /// command line is a deliberate act by somebody who typed the id, and the directory they happen
    /// to be standing in is not a reason to refuse it.
    /// </summary>
    [Fact]
    public void NoFolderSearchesEverywhere()
    {
        var found = _store.LoadByUid("01BBBBBBBBBBBBBBBBBBBBBBBB");

        Assert.NotNull(found.Session);
    }
}
