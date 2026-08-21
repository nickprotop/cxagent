using CxAgent.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The usage archive. Distinct from <see cref="SqliteSessionStore"/>, whose own doc calls itself "a
/// RESUME BUFFER, not an archive" — these are the tests for the thing it declined to be.
/// </summary>
public class UsageHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly UsageHistoryStore _store;

    public UsageHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-hist-" + Guid.NewGuid().ToString("N"));
        _store = new UsageHistoryStore(new AppPaths(_dir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static DateTimeOffset Ago(int hours) => DateTimeOffset.UtcNow.AddHours(-hours);

    private static SessionRecord Session(string id, int input = 100, int output = 10,
        int sub = 0, string? dir = "/tmp/proj", int turns = 1) =>
        new(id, dir, "test-model", "fan-out", input, output, sub, turns, Ago(1), DateTimeOffset.UtcNow);

    /// <summary>
    /// A SESSION IS UPSERTED, not appended. It is written every turn — a crash must not lose the
    /// whole session — but it remains ONE session, so the final row holds the final totals rather
    /// than the history accumulating a row per turn.
    /// </summary>
    [Fact]
    public void SaveSession_Upserts_SoOneSessionIsOneRow()
    {
        _store.SaveSession(Session("s1", input: 100, turns: 1));
        _store.SaveSession(Session("s1", input: 900, turns: 4));

        var all = _store.SessionsSince(Ago(24));

        var row = Assert.Single(all);
        Assert.Equal(900, row.InputTokens);
        Assert.Equal(4, row.Turns);
    }

    /// <summary>
    /// STARTED_AT SURVIVES THE UPDATE. Duration is the axis every "where did my week go" view wants,
    /// and an upsert that overwrote the start with each turn would make every session appear
    /// instantaneous — the update clause deliberately omits that column.
    /// </summary>
    [Fact]
    public void SaveSession_KeepsTheOriginalStartedAt()
    {
        var first = Ago(3);
        _store.SaveSession(new SessionRecord("s1", "/tmp/p", "m", "single", 1, 1, 0, 1,
            first, DateTimeOffset.UtcNow));
        _store.SaveSession(new SessionRecord("s1", "/tmp/p", "m", "single", 2, 2, 0, 2,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var row = Assert.Single(_store.SessionsSince(Ago(24)));

        Assert.True(row.StartedAt < DateTimeOffset.UtcNow.AddHours(-2),
            $"started_at was overwritten by the update — got {row.StartedAt:O}");
    }

    /// <summary>Sub-agent tokens are stored, because they are the one figure a fan-out session cannot
    /// reconstruct later: children share the parent's ledger and usually its model, so nothing else
    /// distinguishes their spend.</summary>
    [Fact]
    public void SaveSession_KeepsTheWorkerSplit()
    {
        _store.SaveSession(Session("s1", input: 1000, output: 100, sub: 880));

        Assert.Equal(880, Assert.Single(_store.SessionsSince(Ago(24))).SubAgentTokens);
    }

    [Fact]
    public void SessionsSince_ExcludesOlderRows()
    {
        _store.SaveSession(new SessionRecord("old", "/tmp/p", "m", "single", 1, 1, 0, 1,
            Ago(100), Ago(100)));
        _store.SaveSession(Session("new"));

        Assert.Equal("new", Assert.Single(_store.SessionsSince(Ago(24))).AgentId);
    }

    /// <summary>
    /// A RUN IS ONE ROW PER CHILD, and it carries the type — which is the whole reason the table
    /// exists. "Is planner worth spawning" needs many runs; one session holds one.
    /// </summary>
    [Fact]
    public void SaveRun_RecordsTheTypeAndCost()
    {
        _store.SaveRun(new RunRecord("r1", "s1", "planner", "test-model",
            38_900, 2_303, 7, 12, "completed", Ago(1), 130_000, "/tmp/proj"));

        var run = Assert.Single(_store.RunsSince(Ago(24)));
        Assert.Equal("planner", run.TypeName);
        Assert.Equal(41_203, run.InputTokens + run.OutputTokens);
        Assert.Equal(12, run.ToolCalls);
        Assert.Equal("completed", run.Outcome);
    }

    /// <summary>
    /// A TOOL CALL RECORDS WHAT IT COST THE CONTEXT. `result_chars` is the field that is not
    /// recoverable from anywhere else, and the premise of delegation is moving large results out of
    /// the parent — unmeasurable without it.
    /// </summary>
    [Fact]
    public void SaveToolCall_RecordsResultChars()
    {
        _store.SaveToolCall(new ToolCallRecord("c1", "a1", "read_file", "file",
            "succeeded", 42, 16_030, Ago(1), "/tmp/proj"));

        Assert.Equal(16_030, Assert.Single(_store.ToolCallsSince(Ago(24))).ResultChars);
    }

    /// <summary>
    /// A CHILD'S CALLS KEEP THE CHILD'S ID. Forwarding a child's report through the parent must
    /// attribute, not absorb: the child is where the expensive reading happens, and rewriting the id
    /// to the parent's would hide exactly the thing worth seeing.
    /// </summary>
    [Fact]
    public void SaveToolCall_AttributesAChildsCallToTheChild()
    {
        _store.SaveToolCall(new ToolCallRecord("c1", "parent-1", "task", "llm_agent",
            "succeeded", 1000, 19_231, Ago(1), "/tmp/proj"));
        _store.SaveToolCall(new ToolCallRecord("c2", "child-1", "read_file", "file",
            "succeeded", 10, 5_000, Ago(1), "/tmp/proj"));

        var calls = _store.ToolCallsSince(Ago(24));

        Assert.Contains(calls, c => c.AgentId == "child-1" && c.ToolName == "read_file");
        Assert.Contains(calls, c => c.AgentId == "parent-1" && c.ToolName == "task");
    }

    /// <summary>Compaction's trigger separates "the app decided" from "the user asked" — a threshold
    /// that fires too eagerly and one that never fires are indistinguishable from a bare count.
    /// </summary>
    [Fact]
    public void SaveCompaction_KeepsTheTrigger()
    {
        _store.SaveCompaction(new CompactionRecord("a1", Ago(1), 190_000, 40_000, "pressure", "/tmp/p"));
        _store.SaveCompaction(new CompactionRecord("a1", Ago(1), 90_000, 30_000, "manual", "/tmp/p"));

        var rows = _store.CompactionsSince(Ago(24));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Trigger == "pressure" && r.BeforeTokens == 190_000);
        Assert.Contains(rows, r => r.Trigger == "manual");
    }

    /// <summary>Compactions are APPEND-ONLY — two in one session are two facts, not one overwritten.
    /// The table has a synthetic key precisely so a second compaction cannot replace the first.
    /// </summary>
    [Fact]
    public void SaveCompaction_AppendsRatherThanReplacing()
    {
        for (var i = 0; i < 3; i++)
            _store.SaveCompaction(new CompactionRecord("a1", Ago(1), 100_000, 20_000, "pressure", null));

        Assert.Equal(3, _store.CompactionsSince(Ago(24)).Count);
    }

    /// <summary>
    /// SILENT IS ITS OWN DECISION. "Allowed without asking" and "the user said yes" are different
    /// facts; collapsing them would make a session of stored rules look like a session of choices.
    /// </summary>
    [Fact]
    public void SavePermission_DistinguishesSilentFromAllowed()
    {
        _store.SavePermission(new PermissionRecord("a1", Ago(1), "Shell", "silent", null, "/tmp/p"));
        _store.SavePermission(new PermissionRecord("a1", Ago(1), "FileWrite", "allowed", "explore", "/tmp/p"));
        _store.SavePermission(new PermissionRecord("a1", Ago(1), "Http", "denied", null, "/tmp/p"));

        var rows = _store.PermissionsSince(Ago(24));

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Decision == "silent");
        Assert.Contains(rows, r => r.Decision == "allowed" && r.Requester == "explore");
        Assert.Contains(rows, r => r.Decision == "denied");
    }

    /// <summary>
    /// EVERY EVENT ROW CARRIES ITS PROJECT. Not only so each table can be grouped without a join, but
    /// because a CHILD's rows have nothing to join to — children are never sessions.
    /// </summary>
    [Fact]
    public void EveryEventTable_CarriesTheWorkingDirectory()
    {
        _store.SaveToolCall(new ToolCallRecord("c1", "child-1", "read_file", "file",
            "succeeded", 1, 10, Ago(1), "/tmp/proj"));
        _store.SaveCompaction(new CompactionRecord("child-1", Ago(1), 10, 5, "pressure", "/tmp/proj"));
        _store.SavePermission(new PermissionRecord("child-1", Ago(1), "Shell", "allowed", "explore", "/tmp/proj"));

        Assert.Equal("/tmp/proj", Assert.Single(_store.ToolCallsSince(Ago(24))).WorkingDir);
        Assert.Equal("/tmp/proj", Assert.Single(_store.CompactionsSince(Ago(24))).WorkingDir);
        Assert.Equal("/tmp/proj", Assert.Single(_store.PermissionsSince(Ago(24))).WorkingDir);
    }

    /// <summary>
    /// WITHOUT THE SUBJECT, "auto-refused: 4" cannot be acted on. The question a user has is which
    /// COMMANDS are being refused, and a count answers a different one.
    /// </summary>
    [Fact]
    public void APermissionRow_KeepsWhatItWasAbout()
    {
        _store.SavePermission(new PermissionRecord(
            "a", Ago(1), "Shell", "auto-refused", null, "/tmp/p",
            Subject: "dotnet build 2>&1 | tail -5"));

        var back = _store.PermissionsSince(Ago(24));

        Assert.Equal("dotnet build 2>&1 | tail -5", Assert.Single(back).Subject);
    }

    /// <summary>
    /// MIGRATION, NOT A RESET. Rows written before this column existed must keep loading — a user's
    /// history is the only evidence of what auto mode did before the change.
    /// </summary>
    [Fact]
    public void AnOlderRowWithNoSubjectStillReads()
    {
        _store.SavePermission(new PermissionRecord("a", Ago(1), "Shell", "allowed", null));

        Assert.Null(Assert.Single(_store.PermissionsSince(Ago(24))).Subject);
    }

    /// <summary>
    /// THE MIGRATION ITSELF, not the record. The two tests above write and read `subject` entirely
    /// through the NEW code path against a FRESH database — neither would notice if
    /// <c>AddColumnIfMissing(conn, "permissions", "subject", "TEXT")</c> were deleted from
    /// <c>TryCreateSchema</c> tomorrow, because a fresh `CREATE TABLE` still has no `subject` column
    /// either way and `SavePermission`/`PermissionsSince` would just throw "no such column" — same
    /// crash whether the row is old or new. The only way to prove the ALTER runs is to build a
    /// database that predates it, by hand, with the exact pre-change column list, and open it with
    /// today's store.
    /// </summary>
    [Fact]
    public void AnOldSchemaDatabase_IsUpgradedInPlace()
    {
        // A DIRECTORY OF ITS OWN, not `_dir` — the fixture's own `_store` already created a
        // history.db (new schema) at `_dir` in the constructor, so writing the old schema there
        // would collide with a table that already has `subject`. This has to be a database the
        // current store has never opened.
        var oldDir = Path.Combine(Path.GetTempPath(), "cxagent-hist-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(oldDir);
        var dbPath = Path.Combine(oldDir, "history.db");

        // Pre-change schema, written with a raw connection so this row is genuinely old — never
        // touched by any version of UsageHistoryStore that knows about `subject`.
        using (var raw = new SqliteConnection($"Data Source={dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE permissions (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id  TEXT NOT NULL,
                    at        TEXT NOT NULL,
                    kind      TEXT NOT NULL,
                    decision  TEXT NOT NULL,
                    requester TEXT,
                    working_dir TEXT);
                INSERT INTO permissions (agent_id, at, kind, decision, requester, working_dir)
                VALUES ('old-agent', $at, 'Shell', 'allowed', NULL, '/tmp/old');
                """;
            cmd.Parameters.AddWithValue("$at", Ago(1).UtcDateTime.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        try
        {
            // Opening the store must run the migration, not throw — this is the line that fails
            // first if AddColumnIfMissing regresses, because the constructor calls TryCreateSchema
            // and that swallows its own exceptions (a broken migration would otherwise look like a
            // working one).
            var upgraded = new UsageHistoryStore(new AppPaths(oldDir));

            var old = Assert.Single(upgraded.PermissionsSince(Ago(24)), r => r.AgentId == "old-agent");
            Assert.Null(old.Subject);

            // AND A ROW WRITTEN AFTER THE UPGRADE ROUND-TRIPS ITS SUBJECT, in the SAME database
            // file — proving the added column is not just present but usable both ways.
            upgraded.SavePermission(new PermissionRecord("new-agent", Ago(1), "Shell", "auto-refused",
                null, "/tmp/new", Subject: "echo hi"));

            var fresh = Assert.Single(upgraded.PermissionsSince(Ago(24)), r => r.AgentId == "new-agent");
            Assert.Equal("echo hi", fresh.Subject);
        }
        finally
        {
            Directory.Delete(oldDir, recursive: true);
        }
    }

    /// <summary>
    /// A BROKEN STORE COSTS STATISTICS, NEVER A SESSION. Every write swallows its failures — the same
    /// contract the resume store and LogFileManager have. Here the database path is a directory,
    /// which no SQLite connection can open.
    /// </summary>
    [Fact]
    public void AWriteThatCannotSucceed_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-hist-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "history.db"));   // a directory where the file goes
        try
        {
            var broken = new UsageHistoryStore(new AppPaths(dir));

            broken.SaveSession(Session("s1"));
            broken.SaveRun(new RunRecord("r1", "s1", "explore", null, 1, 1, 1, 1, "completed",
                Ago(1), 10, null));
            broken.SaveToolCall(new ToolCallRecord("c1", "a1", "read_file", "file", "succeeded",
                1, 1, Ago(1), null));
            broken.SaveCompaction(new CompactionRecord("a1", Ago(1), 2, 1, "pressure", null));
            broken.SavePermission(new PermissionRecord("a1", Ago(1), "Shell", "allowed", null, null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
