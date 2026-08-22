using CxAgent.Core.Sessions;
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

    /// <summary>
    /// A SUPERSEDED SESSION SURVIVES PRUNING, however old.
    ///
    /// <para>Resuming retires the row it restored, so the same context is not offered again — the
    /// same suppression a clean exit gets, and NOT the same event. A superseded session is a live
    /// conversation somebody continued, and its successor was built on it: pruning it deletes the
    /// history behind work that is still going, and a long chain of resumes would age out from its
    /// tail one link at a time.</para>
    /// </summary>
    [Fact]
    public void Prune_KeepsSupersededSessions_HoweverOld()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("continued", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        store.MarkSuperseded("continued");

        store.Prune(TimeSpan.Zero);

        Assert.Equal(1, store.CountSessions());
    }

    /// <summary>
    /// ...but it is still retired: bare --resume must not offer a conversation that already has a
    /// successor, or accepting it twice forks one history into two sessions claiming it.
    /// </summary>
    [Fact]
    public void ASupersededSessionIsNotOfferedForResume()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("continued", [Msg("user", "hello")], inputTokens: 10, outputTokens: 1, workingDir: Here);
        store.MarkSuperseded("continued");

        Assert.Null(store.LoadLatestUnfinished(Here));

        // Still listed, and still reachable by id — retired is not deleted.
        Assert.Single(store.List(Here));
        Assert.NotNull(store.LoadByUid("continued").Session);
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

    /// <summary>The newest session FROM THIS FOLDER wins — not the newest overall. Ordering by
    /// recency alone lets a session in an unrelated folder shadow the one you actually wanted.</summary>
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
    /// <para>`CREATE TABLE IF NOT EXISTS` does nothing when the table is already there, so without
    /// a migration a user upgrading in place keeps the older shape and every INSERT naming
    /// working_dir fails — silently, since this store swallows everything. Their resume would simply
    /// stop working with no message. This opens a second store over the same directory, which is
    /// what a second launch does.</para>
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

    // ---- the listing, and finding one by uid ---------------------------------------------------

    /// <summary>
    /// THE TITLE IS HOW A PERSON RECOGNISES A CONVERSATION. A ULID identifies without describing; a
    /// size and an age describe without identifying.
    /// </summary>
    [Fact]
    public void ASessionIsNamedByItsFirstUserMessage()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a",
            [Msg("system", "you are cxagent"), Msg("user", "add the arity table"), Msg("assistant", "ok")],
            1, 1, workingDir: Here);

        Assert.Equal("add the arity table", Assert.Single(store.List(Here)).Title);
    }

    /// <summary>
    /// AND KEEPS THAT NAME. Later turns must not retitle a session the user already knows by its
    /// opening — the list would reshuffle its own labels as work continued.
    /// </summary>
    [Fact]
    public void TheFirstTitleSticks()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [Msg("user", "the original question")], 1, 1, workingDir: Here);
        store.SaveTurn("agent-a",
            [Msg("user", "the original question"), Msg("user", "a later one")], 2, 2, workingDir: Here);

        Assert.Equal("the original question", Assert.Single(store.List(Here)).Title);
    }

    /// <summary>A session with no user message yet has no subject, and inventing one from the system
    /// prompt would name every session the same thing.</summary>
    [Fact]
    public void ASessionWithNoUserMessageHasNoTitle()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [Msg("system", "you are cxagent")], 1, 1, workingDir: Here);

        Assert.Null(Assert.Single(store.List(Here)).Title);
    }

    /// <summary>
    /// FINISHED ROWS ARE LISTED. Why a session ended is worth seeing and is not a reason to hide it —
    /// the flag gates what `--resume` picks by DEFAULT, not what can be reached.
    /// </summary>
    [Fact]
    public void TheListShowsFinishedSessionsToo()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [Msg("user", "one")], 1, 1, workingDir: Here);
        store.SaveTurn("agent-b", [Msg("user", "two")], 1, 1, workingDir: Here);
        store.MarkFinished("agent-a");

        var listed = store.List(Here);

        Assert.Equal(2, listed.Count);
        Assert.True(listed.Single(x => x.Uid == "agent-a").Finished);
        Assert.False(listed.Single(x => x.Uid == "agent-b").Finished);

        // ...while the default resume still skips the finished one.
        Assert.Equal("agent-b", store.LoadLatestUnfinished(Here)!.AgentId);
    }

    /// <summary>Listing across folders is safe; RESTORING across them is the caller's decision.</summary>
    [Fact]
    public void TheListIsFolderScopedUnlessAskedOtherwise()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [Msg("user", "here")], 1, 1, workingDir: Here);
        store.SaveTurn("agent-b", [Msg("user", "elsewhere")], 1, 1, workingDir: "/projects/other");

        Assert.Single(store.List(Here));
        Assert.Equal(2, store.List(Here, all: true).Count);
    }

    [Fact]
    public void TheListIsNewestFirst()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-old", [Msg("user", "first")], 1, 1, workingDir: Here);
        Thread.Sleep(1100);   // updated_at carries whole seconds
        store.SaveTurn("agent-new", [Msg("user", "second")], 1, 1, workingDir: Here);

        Assert.Equal("agent-new", store.List(Here)[0].Uid);
    }

    /// <summary>
    /// SESSIONS THAT PREDATE THE TITLE COLUMN ARE TITLED ANYWAY, once, when the column is added.
    ///
    /// <para>Titles are written on save, and a session that already ended is never saved again — so
    /// without a backfill every conversation a user already had would list as "(no messages yet)"
    /// forever, on exactly the rows most worth resuming.</para>
    /// </summary>
    [Fact]
    public void SessionsFromBeforeTheTitleColumnAreTitledOnTheNextOpen()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-old", [Msg("user", "why does /mode not show the file axis")], 1, 1,
            workingDir: Here);

        // Back to what the row looked like before the column existed.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agent_sessions SET title = NULL;";
            cmd.ExecuteNonQuery();
        }

        // Opening the store runs the migration, and the migration fills it in.
        var reopened = new SqliteSessionStore(_paths);

        Assert.Equal("why does /mode not show the file axis", reopened.List(Here)[0].Title);
    }

    /// <summary>A ULID is 26 characters and unusable at a prompt, so it must be abbreviable.</summary>
    [Fact]
    public void ASessionIsFoundByAnUnambiguousPrefix()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("01KZWZ0XHWBDDGD64Z7RRN873P", [Msg("user", "hello")], 1, 1, workingDir: Here);

        Assert.Equal("01KZWZ0XHWBDDGD64Z7RRN873P", store.LoadByUid("01KZWZ").Session!.AgentId);
        Assert.Equal("01KZWZ0XHWBDDGD64Z7RRN873P", store.LoadByUid("01kzwz").Session!.AgentId);   // as typed
        Assert.Equal("01KZWZ0XHWBDDGD64Z7RRN873P",
            store.LoadByUid("01KZWZ0XHWBDDGD64Z7RRN873P").Session!.AgentId);                     // in full
    }

    /// <summary>
    /// AND BY ITS TAIL, which is the form the listing actually prints. A ULID begins with a
    /// timestamp: sessions started minutes apart share their leading characters, so the git habit of
    /// abbreviating from the front is exactly wrong here — three sessions from one afternoon all
    /// abbreviate to the same six. The random half is at the end.
    /// </summary>
    [Fact]
    public void ASessionIsAlsoFoundByTheTailTheListingShows()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("01KZXC5H9QXNND4VH6W0GR07R2", [Msg("user", "hello")], 1, 1, workingDir: Here);
        store.SaveTurn("01KZXC96Z5CSTJC6C9QF7WVD8H", [Msg("user", "hello")], 1, 1, workingDir: Here);

        // The shared timestamp prefix names both and resolves neither...
        Assert.True(store.LoadByUid("01KZXC").IsAmbiguous);

        // ...while the tail names exactly one.
        Assert.Equal("01KZXC5H9QXNND4VH6W0GR07R2", store.LoadByUid("GR07R2").Session!.AgentId);
        Assert.Equal("01KZXC96Z5CSTJC6C9QF7WVD8H", store.LoadByUid("wvd8h").Session!.AgentId);
    }

    /// <summary>
    /// AMBIGUITY IS REPORTED, NEVER RESOLVED. Picking the newest match silently is how someone
    /// restores the wrong conversation and does not find out for ten minutes.
    /// </summary>
    [Fact]
    public void AnAmbiguousPrefixIsReportedRatherThanGuessedAt()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("01KZWZAAAA", [Msg("user", "one")], 1, 1, workingDir: Here);
        store.SaveTurn("01KZWZBBBB", [Msg("user", "two")], 1, 1, workingDir: Here);

        var found = store.LoadByUid("01KZWZ");

        Assert.Null(found.Session);
        Assert.True(found.IsAmbiguous);
        Assert.Equal(2, found.Ambiguous.Count);
    }

    [Fact]
    public void APrefixThatMatchesNothing_FindsNothing()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [Msg("user", "one")], 1, 1, workingDir: Here);

        var found = store.LoadByUid("nope");

        Assert.Null(found.Session);
        Assert.False(found.IsAmbiguous);
    }

    /// <summary>A uid names a session wherever it was recorded — the folder scoping is the LIST's
    /// concern, not the lookup's.</summary>
    [Fact]
    public void AUidFindsASessionFromAnotherFolder()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-elsewhere", [Msg("user", "other project")], 1, 1,
            workingDir: "/projects/other");

        Assert.NotNull(store.LoadByUid("agent-else").Session);
    }

    /// <summary>And reaches a finished one, which `--resume` with no uid deliberately will not.</summary>
    [Fact]
    public void AUidReachesAFinishedSession()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-a", [Msg("user", "done with this")], 1, 1, workingDir: Here);
        store.MarkFinished("agent-a");

        Assert.NotNull(store.LoadByUid("agent-a").Session);
        Assert.Null(store.LoadLatestUnfinished(Here));
    }

    /// <summary>
    /// A DATABASE WRITTEN BEFORE THE TITLE COLUMN EXISTED still opens, still lists, and keeps its
    /// rows. `CREATE TABLE IF NOT EXISTS` does nothing to an existing table, so the column is added
    /// by migration — the same path working_dir already took.
    /// </summary>
    [Fact]
    public void AnOlderDatabaseGainsTheColumnWithoutLosingRows()
    {
        // A store shaped like the old schema, written by hand.
        var dbPath = Path.Combine(_dir, "cxagent.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE agent_sessions (
                    agent_id TEXT PRIMARY KEY, context_json TEXT NOT NULL,
                    input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                    finished INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, working_dir TEXT);
                INSERT INTO agent_sessions VALUES ('old-agent', '[]', 5, 6, 0, '2026-01-01T00:00:00+00:00', '/projects/alpha');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new SqliteSessionStore(_paths);
        var listed = Assert.Single(store.List("/projects/alpha"));

        Assert.Equal("old-agent", listed.Uid);
        Assert.Null(listed.Title);          // no column then, so nothing to show now
        Assert.Equal(5, listed.InputTokens);
    }

    // ---- the edit mode, so resume cannot silently widen it ---------------------------------------

    /// <summary>
    /// RESUME NEVER WIDENS. A session saved in always-ask must come back in always-ask — resuming it
    /// into the accept-edits default would silently undo a decision the user made, at the moment they
    /// are least likely to be watching.
    /// </summary>
    [Theory]
    [InlineData(EditMode.AlwaysAsk)]
    [InlineData(EditMode.AcceptEdits)]
    [InlineData(EditMode.Auto)]
    public void SaveTurn_RoundTripsTheEditMode(EditMode mode)
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn(new SqliteSessionStore.ResumeTurn("agent-1", [Msg("user", "hello")],
            InputTokens: 10, OutputTokens: 2, WorkingDir: Here, Edits: mode));

        Assert.Equal(mode, store.LoadLatestUnfinished(Here)!.Edits);
    }

    /// <summary>
    /// A ROW FROM BEFORE THE COLUMN HAS NO MODE, and that absence must stay distinguishable from a
    /// recorded choice — the caller resolves null to always-ask, which it could not do if the store
    /// invented a default here.
    /// </summary>
    [Fact]
    public void ASessionSavedWithoutAMode_ComesBackWithNull()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn("agent-1", [Msg("user", "hello")],
            inputTokens: 10, outputTokens: 2, workingDir: Here);

        Assert.Null(store.LoadLatestUnfinished(Here)!.Edits);
    }

    /// <summary>A corrupted or hand-edited value must not be able to widen a session: it reads as
    /// absent, which the caller resolves to always-ask.</summary>
    [Fact]
    public void AnUnrecognisedStoredMode_ReadsAsAbsent()
    {
        var store = new SqliteSessionStore(_paths);
        store.SaveTurn(new SqliteSessionStore.ResumeTurn("agent-1", [Msg("user", "hello")],
            InputTokens: 10, OutputTokens: 2, WorkingDir: Here, Edits: EditMode.AcceptEdits));

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_paths.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agent_sessions SET edit_mode = 'nonsense';";
            cmd.ExecuteNonQuery();
        }

        Assert.Null(store.LoadLatestUnfinished(Here)!.Edits);
    }
}
