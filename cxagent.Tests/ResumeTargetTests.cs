using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What <c>--resume</c> resolves to, and what it says when it resolves to nothing.
///
/// <para>The two failure modes are kept apart deliberately: nothing to resume means "just start",
/// an ambiguous id means "type more characters". One message for both would hide which happened.</para>
/// </summary>
public class ResumeTargetTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    private const string Here = "/projects/here";

    public ResumeTargetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-resume-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static ChatMessage Msg(string role, string content) =>
        new() { Role = role, Content = content, Timestamp = DateTimeOffset.UtcNow };

    private SqliteSessionStore StoreWith(params (string Id, string Dir)[] sessions)
    {
        var store = new SqliteSessionStore(_paths);
        foreach (var (id, dir) in sessions)
            store.SaveTurn(id, [Msg("user", $"hello from {id}")], 1, 1, workingDir: dir);

        return store;
    }

    /// <summary>Bare --resume continues what the startup offer would have proposed.</summary>
    [Fact]
    public void BareResume_TakesTheNewestUnfinishedSessionHere()
    {
        var store = StoreWith(("AAAA01KZXC", Here), ("BBBB01KZXC", Here));

        var (snapshot, _) = AppBootstrap.FindResumeTarget(store, Here, uid: null);

        Assert.Equal("BBBB01KZXC", snapshot!.AgentId);
    }

    /// <summary>
    /// SCOPED TO THE FOLDER, like the offer it stands in for: restoring another project's
    /// conversation fills this one with its files and decisions.
    /// </summary>
    [Fact]
    public void BareResume_IgnoresSessionsFromOtherFolders()
    {
        var store = StoreWith(("AAAA01KZXC", "/projects/elsewhere"));

        var (snapshot, problem) = AppBootstrap.FindResumeTarget(store, Here, uid: null);

        Assert.Null(snapshot);
        Assert.Contains("No unfinished session", problem);
    }

    [Fact]
    public void BareResume_SkipsAFinishedSession()
    {
        var store = StoreWith(("AAAA01KZXC", Here));
        store.MarkFinished("AAAA01KZXC");

        Assert.Null(AppBootstrap.FindResumeTarget(store, Here, uid: null).Snapshot);
    }

    [Fact]
    public void AnIdResolvesByAbbreviation()
    {
        var store = StoreWith(("AAAA01KZXC", Here), ("BBBB01KZXC", Here));

        Assert.Equal("AAAA01KZXC",
            AppBootstrap.FindResumeTarget(store, Here, "AAAA").Snapshot!.AgentId);
    }

    /// <summary>
    /// NAMING A SESSION IS AN EXPLICIT ACT, so it is not folder-scoped the way bare --resume is.
    /// Someone who copied an id out of `--sessions all` meant that session; the scope exists to stop
    /// an unasked-for session appearing, not to stop a named one being opened.
    /// </summary>
    [Fact]
    public void AnIdReachesASessionFromAnotherFolder()
    {
        var store = StoreWith(("AAAA01KZXC", "/projects/elsewhere"));

        Assert.NotNull(AppBootstrap.FindResumeTarget(store, Here, "AAAA").Snapshot);
    }

    /// <summary>A named session is opened even after it ended cleanly — asking for it says so.</summary>
    [Fact]
    public void AnIdReachesAFinishedSession()
    {
        var store = StoreWith(("AAAA01KZXC", Here));
        store.MarkFinished("AAAA01KZXC");

        Assert.NotNull(AppBootstrap.FindResumeTarget(store, Here, "AAAA").Snapshot);
    }

    [Fact]
    public void AnAmbiguousIdSaysHowManyItMatchedAndNamesThem()
    {
        var store = StoreWith(("ZZZAAA01KZXC", Here), ("ZZZBBB01KZXC", Here));

        var (snapshot, problem) = AppBootstrap.FindResumeTarget(store, Here, "ZZZ");

        Assert.Null(snapshot);
        Assert.Contains("2 sessions", problem);
        Assert.Contains("ZZZAAA", problem);
        Assert.Contains("ZZZBBB", problem);
    }

    [Fact]
    public void AnUnknownIdIsReportedAsSuch()
    {
        var store = StoreWith(("AAAA01KZXC", Here));

        var (snapshot, problem) = AppBootstrap.FindResumeTarget(store, Here, "QQQQ");

        Assert.Null(snapshot);
        Assert.Contains("QQQQ", problem);
    }
}
