using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The resume buffer. A crash is exactly when the exit path does not run, so what makes a session
/// recoverable is written as turns complete — and what is written has to come back byte-identical,
/// because the thing being restored is the model's memory.
/// </summary>
public class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;

    public SqliteSessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-store-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
    }

    /// <summary>
    /// The folder these round-trip tests pretend to run in.
    ///
    /// <para>Resume is SCOPED to a working directory, so a session saved without one is deliberately
    /// unreachable — a row that cannot say where it came from could have come from anywhere. These
    /// tests are about round-tripping rather than scoping, so they all use one folder; the scoping
    /// rules have their own tests below.</para>
    /// </summary>
    private const string Here = "/projects/here";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ChatMessage Msg(string role, string content) =>
        new() { Role = role, Content = content, Timestamp = DateTimeOffset.UtcNow };

    /// <summary>A saved session comes back with its messages in order, byte-identical.</summary>
    [Fact]
    public void SaveTurn_ThenLoad_RoundTripsTheContext()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-1",
            [Msg("system", "Your working directory is /tmp."), Msg("user", "hello"), Msg("assistant", "hi")],
            inputTokens: 100, outputTokens: 20, workingDir: Here);

        var snap = store.LoadLatestUnfinished(Here);

        Assert.NotNull(snap);
        Assert.Equal("agent-1", snap!.AgentId);
        Assert.Equal(3, snap.Context.Count);
        Assert.Equal("system", snap.Context[0].Role);
        Assert.Equal("Your working directory is /tmp.", snap.Context[0].Content);
        Assert.Equal("hi", snap.Context[2].Content);
    }

    /// <summary>
    /// Tool calls and tool-call ids survive.
    ///
    /// <para>ToolCallId is the ONLY marker of a tool result — a round trip that drops it turns the
    /// result into an ordinary user turn, with no error and no warning, and the model simply never
    /// sees what it asked for. ToolCall.Arguments is a JsonElement, which is the part most likely to
    /// come back detached or empty, and the symptom would appear far from the cause.</para>
    /// </summary>
    [Fact]
    public void SaveTurn_RoundTripsToolCallsAndToolCallIds()
    {
        var store = new SqliteSessionStore(_paths);
        var call = new ToolCall
        {
            Id = "call-7",
            Name = "read_file",
            Arguments = JsonSerializer.SerializeToElement(new { path = "cxagent/UI/Agent.cs" }),
        };

        store.SaveTurn("agent-1",
        [
            new ChatMessage { Role = "assistant", Content = "", ToolCalls = [call] },
            new ChatMessage { Role = "tool", Content = "file contents", ToolCallId = "call-7" },
        ], inputTokens: 10, outputTokens: 5, workingDir: Here);

        var snap = store.LoadLatestUnfinished(Here);

        Assert.NotNull(snap);
        var restored = Assert.Single(snap!.Context[0].ToolCalls!);
        Assert.Equal("read_file", restored.Name);
        Assert.Equal("call-7", restored.Id);
        Assert.Equal("cxagent/UI/Agent.cs", restored.Arguments.GetProperty("path").GetString());

        Assert.Equal("call-7", snap.Context[1].ToolCallId);
    }

    /// <summary>The LATEST turn wins. Each save replaces that agent's snapshot rather than
    /// accumulating — the context is the whole conversation every time, not a delta.</summary>
    [Fact]
    public void SaveTurn_Twice_KeepsOnlyTheNewerContext()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-1", [Msg("user", "first")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        store.SaveTurn("agent-1", [Msg("user", "first"), Msg("user", "second")],
            inputTokens: 30, outputTokens: 4, workingDir: Here);

        var snap = store.LoadLatestUnfinished(Here);

        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Context.Count);
        Assert.Equal(30, snap.InputTokens);
    }

    /// <summary>A finished session is not offered for resume. Only a crash leaves one unfinished.</summary>
    [Fact]
    public void MarkFinished_ThenLoad_ReturnsNull()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-1", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        store.MarkFinished("agent-1");

        Assert.Null(store.LoadLatestUnfinished(Here));
    }

    /// <summary>Nothing saved, nothing to resume.</summary>
    [Fact]
    public void LoadLatestUnfinished_OnAnEmptyDatabase_ReturnsNull()
    {
        var store = new SqliteSessionStore(_paths);

        Assert.Null(store.LoadLatestUnfinished(Here));
    }

    /// <summary>The ledger totals come back, so a resumed session reports what it has already spent
    /// rather than restarting the count at zero.</summary>
    [Fact]
    public void SaveTurn_RoundTripsTheLedgerTotals()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-1", [Msg("user", "hello")], inputTokens: 1_234, outputTokens: 567, workingDir: Here);

        var snap = store.LoadLatestUnfinished(Here);

        Assert.NotNull(snap);
        Assert.Equal(1_234, snap!.InputTokens);
        Assert.Equal(567, snap.OutputTokens);
    }

    /// <summary>
    /// The NEWEST unfinished session, when two crashed. Resuming the older one would silently discard
    /// the more recent work, which is the opposite of what this store is for.
    /// </summary>
    [Fact]
    public void LoadLatestUnfinished_WithSeveral_ReturnsTheMostRecentlyUpdated()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("older", [Msg("user", "old")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        Thread.Sleep(1_100);   // the timestamp has second resolution in ISO-8601 round-trip form
        store.SaveTurn("newer", [Msg("user", "new")], inputTokens: 20, outputTokens: 2, workingDir: Here);

        var snap = store.LoadLatestUnfinished(Here);

        Assert.NotNull(snap);
        Assert.Equal("newer", snap!.AgentId);
    }

    /// <summary>
    /// Finished sessions are kept briefly, not forever. This is a resume buffer — persistence as
    /// history is a different feature, and a store with no retention rule grows for the life of the
    /// install.
    /// </summary>
    [Fact]
    public void Prune_RemovesFinishedSessionsOlderThanTheWindow()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("old", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        store.MarkFinished("old");

        // Everything finished, however recently, is older than a zero-length window.
        store.Prune(TimeSpan.Zero);

        Assert.Equal(0, store.CountSessions());
    }

    /// <summary>
    /// An UNFINISHED session is never pruned by age. It is the only thing here that cannot be
    /// reconstructed, and a machine left off over a weekend must not lose it.
    /// </summary>
    [Fact]
    public void Prune_KeepsUnfinishedSessions_HoweverOld()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("crashed", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1, workingDir: Here);

        store.Prune(TimeSpan.Zero);

        Assert.Equal(1, store.CountSessions());
        Assert.NotNull(store.LoadLatestUnfinished(Here));
    }

    /// <summary>A finished session inside the window stays — pruning is age-based, not a purge.</summary>
    [Fact]
    public void Prune_KeepsFinishedSessionsInsideTheWindow()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("recent", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        store.MarkFinished("recent");

        store.Prune(TimeSpan.FromDays(7));

        Assert.Equal(1, store.CountSessions());
    }

    /// <summary>
    /// A store over an unwritable path must not take the app down with it. Persistence is a
    /// convenience; a session that cannot be saved is degraded, not broken.
    /// </summary>
    [Fact]
    public void SaveTurn_WhenTheStoreCannotWrite_DoesNotThrow()
    {
        var store = new SqliteSessionStore(_paths);
        Directory.Delete(_dir, recursive: true);   // pull the database out from under it

        var ex = Record.Exception(() =>
            store.SaveTurn("agent-1", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1));

        Assert.Null(ex);
    }

    // ---- resume is scoped to the folder it was started in ---------------------------------------

    /// <summary>
    /// A SESSION IS OFFERED IN ITS OWN FOLDER AND NOWHERE ELSE.
    ///
    /// <para>Without the filter the newest unfinished session ANYWHERE on the machine was offered
    /// wherever cxagent next started, and accepting it restored another project's conversation into
    /// this one — file paths, code and decisions describing a tree the user is not in. Permission
    /// rules were scoped this way from the start; only this store missed it.</para>
    /// </summary>
    [Fact]
    public void ASession_IsOfferedInItsOwnFolder_AndNotInAnother()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [new ChatMessage { Role = "user", Content = "work in A" }],
            10, 5, workingDir: "/projects/alpha");

        Assert.NotNull(store.LoadLatestUnfinished("/projects/alpha"));
        Assert.Null(store.LoadLatestUnfinished("/projects/beta"));
    }

    /// <summary>The newest session FROM THIS FOLDER wins — not the newest overall, which is the whole
    /// bug: a more recent session elsewhere used to shadow the one you actually wanted.</summary>
    [Fact]
    public void TheNewestSessionInThisFolder_Wins_EvenIfAnotherFolderIsMoreRecent()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("older-here", [new ChatMessage { Role = "user", Content = "mine" }],
            1, 1, workingDir: "/projects/alpha");
        Thread.Sleep(1100);   // the timestamp has second resolution
        store.SaveTurn("newer-elsewhere", [new ChatMessage { Role = "user", Content = "theirs" }],
            1, 1, workingDir: "/projects/beta");

        var snapshot = store.LoadLatestUnfinished("/projects/alpha");

        Assert.NotNull(snapshot);
        Assert.Equal("older-here", snapshot!.AgentId);
    }

    /// <summary>
    /// A ROW WITH NO FOLDER IS NEVER OFFERED. Those are sessions written before the column existed —
    /// they could be from anywhere, which is exactly the condition being fixed. The cost is one lost
    /// resume for a session that predates the fix; the alternative is restoring a stranger's context.
    /// </summary>
    [Fact]
    public void ALegacyRowWithNoFolder_IsNeverOffered()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("legacy", [new ChatMessage { Role = "user", Content = "from before" }], 1, 1);

        Assert.Null(store.LoadLatestUnfinished("/projects/alpha"));
        Assert.Null(store.LoadLatestUnfinished(null));
    }

    /// <summary>
    /// THE MIGRATION RUNS AGAINST AN EXISTING DATABASE WITHOUT LOSING ROWS.
    ///
    /// <para>`CREATE TABLE IF NOT EXISTS` does nothing when the table is already there, so a user
    /// upgrading in place would keep the old shape and every INSERT naming working_dir would fail —
    /// silently, since this store swallows everything. Their resume would simply stop working with no
    /// message. This opens a second store over the same directory, which is what a second launch
    /// does.</para>
    /// </summary>
    [Fact]
    public void ASecondStoreOverTheSameDatabase_KeepsWorking()
    {
        var first = new SqliteSessionStore(_paths);
        first.SaveTurn("agent-a", [new ChatMessage { Role = "user", Content = "first run" }],
            10, 5, workingDir: "/projects/alpha");

        // A fresh store re-runs schema creation and the migration over the existing file.
        var second = new SqliteSessionStore(_paths);

        var snapshot = second.LoadLatestUnfinished("/projects/alpha");
        Assert.NotNull(snapshot);
        Assert.Equal("agent-a", snapshot!.AgentId);

        // And it can still write.
        second.SaveTurn("agent-b", [new ChatMessage { Role = "user", Content = "second run" }],
            1, 1, workingDir: "/projects/alpha");
        Assert.Equal("agent-b", second.LoadLatestUnfinished("/projects/alpha")!.AgentId);
    }
}
