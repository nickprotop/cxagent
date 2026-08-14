using Microsoft.Data.Sqlite;

namespace CxAgent.Core.Storage;

/// <summary>One finished session, as history remembers it.</summary>
public sealed record SessionRecord(
    string AgentId, string? WorkingDir, string? ModelId, string Mode,
    int InputTokens, int OutputTokens, int SubAgentTokens, int Turns,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    /// <summary>Input tokens the provider served from its prefix cache.</summary>
    int CachedInputTokens = 0,
    /// <summary>Input tokens written INTO the provider's cache — zero where warming is free, which
    /// is every local endpoint. Non-zero only where it is BILLED, and a hit rate reported without
    /// it reads as pure saving on exactly those providers.</summary>
    int CacheWrittenTokens = 0,
    /// <summary>Whether the provider reported cache figures at all. False makes
    /// <see cref="CachedInputTokens"/> mean "unknown" rather than "none", which is the difference
    /// between staying silent and claiming a 0% hit rate.</summary>
    bool CacheReported = false);

/// <summary>One sub-agent run, written by the PARENT when the child finishes.</summary>
public sealed record RunRecord(
    string RunId, string ParentAgentId, string TypeName, string? ModelId,
    int InputTokens, int OutputTokens, int Turns, int ToolCalls,
    string Outcome, DateTimeOffset StartedAt, long DurationMs, string? WorkingDir);

/// <summary>One tool invocation, by whichever agent made it.</summary>
/// <param name="WorkingDir">
/// WHICH PROJECT. Carried on every event row, not only on the session, so each table can be grouped
/// by project WITHOUT a join — and, more importantly, so a row survives its session being pruned. A
/// child's rows would otherwise have no project at all: children are never sessions, so there is
/// nothing to join them to.
/// </param>
public sealed record ToolCallRecord(
    string CallId, string AgentId, string ToolName, string? PluginType,
    string Outcome, long DurationMs, int ResultChars, DateTimeOffset StartedAt,
    string? WorkingDir = null);

/// <summary>One compaction — the most expensive automatic thing the app does.</summary>
public sealed record CompactionRecord(
    string AgentId, DateTimeOffset At, int BeforeTokens, int AfterTokens, string Trigger,
    string? WorkingDir = null);

/// <summary>One permission decision.</summary>
public sealed record PermissionRecord(
    string AgentId, DateTimeOffset At, string Kind, string Decision, string? Requester,
    string? WorkingDir = null);

/// <summary>
/// Usage history: what this installation has actually done, kept across sessions.
///
/// <para>A SEPARATE DATABASE FROM RESUME, and the distinction is the point.
/// <see cref="SqliteSessionStore"/> is a resume buffer — one row per agent, replaced every turn,
/// worthless once the session ends cleanly. This is an archive: append-only, never rewritten, and the
/// only place a question like "is <c>planner</c> worth spawning" can be answered, because that needs
/// many runs and one session holds one.</para>
///
/// <para>NOTHING HERE CAN FAIL A SESSION. Every method swallows its own exceptions, exactly as the
/// resume store and <see cref="LogFileManager"/> do. Statistics are the least important thing the app
/// does; a crash caused by recording them would be the worst trade in the codebase.</para>
///
/// <para>WRITES ARE APPEND-ONLY AND FIRE-AND-FORGET. No read-modify-write, so concurrent writers —
/// which step 3's parallel spawns will produce — cannot lose each other's rows.</para>
/// </summary>
public sealed class UsageHistoryStore
{
    private readonly string _connectionString;

    public UsageHistoryStore(AppPaths paths)
    {
        paths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.HistoryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        TryCreateSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        // WAL so a reader (/stats) never blocks the writers, and busy_timeout so two agents writing
        // at once wait rather than throw — step 3 makes concurrent spawns real.
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void TryCreateSchema()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            // ONE ROW PER SESSION, UPSERTED. A session is written repeatedly as it runs — a crash
            // must not lose the whole session — but it is ONE session, so the key is the agent id
            // and later writes replace earlier ones. Everything else here is append-only.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    agent_id         TEXT PRIMARY KEY,
                    working_dir      TEXT,
                    model_id         TEXT,
                    mode             TEXT NOT NULL DEFAULT 'single',
                    input_tokens     INTEGER NOT NULL DEFAULT 0,
                    output_tokens    INTEGER NOT NULL DEFAULT 0,
                    sub_agent_tokens INTEGER NOT NULL DEFAULT 0,
                    turns            INTEGER NOT NULL DEFAULT 0,
                    started_at       TEXT NOT NULL,
                    updated_at       TEXT NOT NULL);

                CREATE INDEX IF NOT EXISTS idx_sessions_when ON sessions(updated_at);
                CREATE INDEX IF NOT EXISTS idx_sessions_dir  ON sessions(working_dir, updated_at);

                CREATE TABLE IF NOT EXISTS runs (
                    run_id          TEXT PRIMARY KEY,
                    parent_agent_id TEXT NOT NULL,
                    type_name       TEXT NOT NULL,
                    model_id        TEXT,
                    input_tokens    INTEGER NOT NULL DEFAULT 0,
                    output_tokens   INTEGER NOT NULL DEFAULT 0,
                    turns           INTEGER NOT NULL DEFAULT 0,
                    tool_calls      INTEGER NOT NULL DEFAULT 0,
                    outcome         TEXT NOT NULL,
                    started_at      TEXT NOT NULL,
                    duration_ms     INTEGER NOT NULL DEFAULT 0,
                    working_dir     TEXT);

                CREATE INDEX IF NOT EXISTS idx_runs_parent ON runs(parent_agent_id);
                CREATE INDEX IF NOT EXISTS idx_runs_type   ON runs(type_name, started_at);

                CREATE TABLE IF NOT EXISTS tool_calls (
                    call_id      TEXT PRIMARY KEY,
                    agent_id     TEXT NOT NULL,
                    tool_name    TEXT NOT NULL,
                    plugin_type  TEXT,
                    outcome      TEXT NOT NULL,
                    duration_ms  INTEGER NOT NULL DEFAULT 0,
                    result_chars INTEGER NOT NULL DEFAULT 0,
                    started_at   TEXT NOT NULL,
                    working_dir  TEXT);

                CREATE INDEX IF NOT EXISTS idx_calls_agent ON tool_calls(agent_id);
                CREATE INDEX IF NOT EXISTS idx_calls_tool  ON tool_calls(tool_name, started_at);

                CREATE TABLE IF NOT EXISTS compactions (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id      TEXT NOT NULL,
                    at            TEXT NOT NULL,
                    before_tokens INTEGER NOT NULL,
                    after_tokens  INTEGER NOT NULL,
                    trigger       TEXT NOT NULL,
                    working_dir   TEXT);

                CREATE TABLE IF NOT EXISTS permissions (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id  TEXT NOT NULL,
                    at        TEXT NOT NULL,
                    kind      TEXT NOT NULL,
                    decision  TEXT NOT NULL,
                    requester TEXT,
                    working_dir TEXT);
                """;
            cmd.ExecuteNonQuery();

            // COLUMNS ADDED AFTER A DATABASE ALREADY EXISTED. `CREATE TABLE IF NOT EXISTS` is a
            // no-op against an older file, so a new column reaches a fresh install and NOTHING else
            // — the failure being an app that works on a new machine and throws "no such column" on
            // the developer's own.
            AddColumnIfMissing(conn, "sessions", "cached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(conn, "sessions", "cache_reported", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(conn, "sessions", "cache_written_tokens", "INTEGER NOT NULL DEFAULT 0");
        }
        catch (Exception)
        {
            // No history means no /stats. It must not mean no app.
        }
    }

    /// <summary>
    /// Adds a column when the table lacks it, and does nothing when it already has one.
    ///
    /// <para>SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so the check is a <c>PRAGMA</c> read
    /// rather than a caught exception — catching would also swallow a genuinely broken schema and
    /// leave the store silently missing a column it believes it added.</para>
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string decl)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = $"PRAGMA table_info({table});";
        using var reader = probe.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return;
        reader.Close();

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl};";
        alter.ExecuteNonQuery();
    }

    private static string Stamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Records or updates a session. UPSERT, so a session written every turn ends with one row
    /// holding its final totals — and <c>started_at</c> survives, because the first write set it and
    /// the update clause deliberately does not touch it.
    /// </summary>
    public void SaveSession(SessionRecord r)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions
                    (agent_id, working_dir, model_id, mode, input_tokens, output_tokens,
                     sub_agent_tokens, turns, started_at, updated_at,
                     cached_input_tokens, cache_reported, cache_written_tokens)
                VALUES ($id, $dir, $model, $mode, $in, $out, $sub, $turns, $started, $updated,
                        $cached, $creported, $cwritten)
                ON CONFLICT(agent_id) DO UPDATE SET
                    working_dir      = excluded.working_dir,
                    model_id         = excluded.model_id,
                    mode             = excluded.mode,
                    input_tokens     = excluded.input_tokens,
                    output_tokens    = excluded.output_tokens,
                    sub_agent_tokens = excluded.sub_agent_tokens,
                    turns            = excluded.turns,
                    updated_at       = excluded.updated_at,
                    cached_input_tokens = excluded.cached_input_tokens,
                    cache_reported      = excluded.cache_reported,
                    cache_written_tokens = excluded.cache_written_tokens;
                """;
            cmd.Parameters.AddWithValue("$id", r.AgentId);
            cmd.Parameters.AddWithValue("$dir", (object?)r.WorkingDir ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", (object?)r.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mode", r.Mode);
            cmd.Parameters.AddWithValue("$in", r.InputTokens);
            cmd.Parameters.AddWithValue("$out", r.OutputTokens);
            cmd.Parameters.AddWithValue("$sub", r.SubAgentTokens);
            cmd.Parameters.AddWithValue("$turns", r.Turns);
            cmd.Parameters.AddWithValue("$started", Stamp(r.StartedAt));
            cmd.Parameters.AddWithValue("$updated", Stamp(r.UpdatedAt));
            cmd.Parameters.AddWithValue("$cached", r.CachedInputTokens);
            cmd.Parameters.AddWithValue("$creported", r.CacheReported ? 1 : 0);
            cmd.Parameters.AddWithValue("$cwritten", r.CacheWrittenTokens);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }

    /// <summary>Records a finished sub-agent run. Written ONCE, by the parent, at completion — a
    /// child that never finishes never writes, and the parent's own sub_agent_tokens still accounts
    /// for what it burned.</summary>
    public void SaveRun(RunRecord r)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO runs
                    (run_id, parent_agent_id, type_name, model_id, input_tokens, output_tokens,
                     turns, tool_calls, outcome, started_at, duration_ms, working_dir)
                VALUES ($id, $parent, $type, $model, $in, $out, $turns, $calls, $outcome,
                        $started, $dur, $dir);
                """;
            cmd.Parameters.AddWithValue("$id", r.RunId);
            cmd.Parameters.AddWithValue("$parent", r.ParentAgentId);
            cmd.Parameters.AddWithValue("$type", r.TypeName);
            cmd.Parameters.AddWithValue("$model", (object?)r.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$in", r.InputTokens);
            cmd.Parameters.AddWithValue("$out", r.OutputTokens);
            cmd.Parameters.AddWithValue("$turns", r.Turns);
            cmd.Parameters.AddWithValue("$calls", r.ToolCalls);
            cmd.Parameters.AddWithValue("$outcome", r.Outcome);
            cmd.Parameters.AddWithValue("$started", Stamp(r.StartedAt));
            cmd.Parameters.AddWithValue("$dur", r.DurationMs);
            cmd.Parameters.AddWithValue("$dir", (object?)r.WorkingDir ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }

    /// <summary>Records one tool invocation, from either a session or a child.</summary>
    public void SaveToolCall(ToolCallRecord r)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO tool_calls
                    (call_id, agent_id, tool_name, plugin_type, outcome, duration_ms,
                     result_chars, started_at, working_dir)
                VALUES ($id, $agent, $tool, $plugin, $outcome, $dur, $chars, $started, $dir);
                """;
            cmd.Parameters.AddWithValue("$id", r.CallId);
            cmd.Parameters.AddWithValue("$agent", r.AgentId);
            cmd.Parameters.AddWithValue("$tool", r.ToolName);
            cmd.Parameters.AddWithValue("$plugin", (object?)r.PluginType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", r.Outcome);
            cmd.Parameters.AddWithValue("$dur", r.DurationMs);
            cmd.Parameters.AddWithValue("$chars", r.ResultChars);
            cmd.Parameters.AddWithValue("$started", Stamp(r.StartedAt));
            cmd.Parameters.AddWithValue("$dir", (object?)r.WorkingDir ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }

    public void SaveCompaction(CompactionRecord r)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO compactions (agent_id, at, before_tokens, after_tokens, trigger, working_dir)
                VALUES ($agent, $at, $before, $after, $trigger, $dir);
                """;
            cmd.Parameters.AddWithValue("$agent", r.AgentId);
            cmd.Parameters.AddWithValue("$at", Stamp(r.At));
            cmd.Parameters.AddWithValue("$before", r.BeforeTokens);
            cmd.Parameters.AddWithValue("$after", r.AfterTokens);
            cmd.Parameters.AddWithValue("$trigger", r.Trigger);
            cmd.Parameters.AddWithValue("$dir", (object?)r.WorkingDir ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }

    public void SavePermission(PermissionRecord r)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO permissions (agent_id, at, kind, decision, requester, working_dir)
                VALUES ($agent, $at, $kind, $decision, $requester, $dir);
                """;
            cmd.Parameters.AddWithValue("$agent", r.AgentId);
            cmd.Parameters.AddWithValue("$at", Stamp(r.At));
            cmd.Parameters.AddWithValue("$kind", r.Kind);
            cmd.Parameters.AddWithValue("$decision", r.Decision);
            cmd.Parameters.AddWithValue("$requester", (object?)r.Requester ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dir", (object?)r.WorkingDir ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Deletes everything. Irreversible, and the only destructive operation here.
    ///
    /// <para>THIS ONE THROWS. Every other method swallows, on the reasoning that a failed write costs
    /// statistics and nothing else — but a delete that silently fails and reports success would leave
    /// a user believing their history is gone when it is not. Deleting is the one thing here a person
    /// must be told the truth about.</para>
    ///
    /// <para>DELETE rather than dropping the file: the connection pool may hold the database open, and
    /// a half-deleted file is a worse state than a full one. The tables survive empty, so the next
    /// write needs no schema check.</para>
    /// </summary>
    public void Clear()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM sessions;
            DELETE FROM runs;
            DELETE FROM tool_calls;
            DELETE FROM compactions;
            DELETE FROM permissions;
            """;
        cmd.ExecuteNonQuery();

        // The file does not shrink on DELETE alone — pages are freed for reuse, not returned. Someone
        // clearing their history usually means it, so the space goes back to the filesystem.
        using var vacuum = conn.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
    }

    /// <summary>How many rows exist, for a confirmation that can say what is at stake.</summary>
    public int TotalRows()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM sessions) + (SELECT COUNT(*) FROM runs)
                 + (SELECT COUNT(*) FROM tool_calls) + (SELECT COUNT(*) FROM compactions)
                 + (SELECT COUNT(*) FROM permissions);
            """;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ---- reads ---------------------------------------------------------------------------------
    //
    // Reads THROW rather than swallowing, unlike the writes above. A write that fails must not break
    // a session; a read that fails and returns an empty list would tell the user "you have done
    // nothing", which is worse than an error. /stats catches and says so.

    /// <summary>Sessions updated at or after <paramref name="since"/>, newest first.</summary>
    public IReadOnlyList<SessionRecord> SessionsSince(DateTimeOffset since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, working_dir, model_id, mode, input_tokens, output_tokens,
                   sub_agent_tokens, turns, started_at, updated_at,
                   cached_input_tokens, cache_reported, cache_written_tokens
            FROM sessions WHERE updated_at >= $since ORDER BY updated_at DESC;
            """;
        cmd.Parameters.AddWithValue("$since", Stamp(since));

        var list = new List<SessionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new SessionRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt32(10), reader.GetInt32(12), reader.GetInt32(11) != 0));
        return list;
    }

    /// <summary>Runs started at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<RunRecord> RunsSince(DateTimeOffset since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, parent_agent_id, type_name, model_id, input_tokens, output_tokens,
                   turns, tool_calls, outcome, started_at, duration_ms, working_dir
            FROM runs WHERE started_at >= $since ORDER BY started_at DESC;
            """;
        cmd.Parameters.AddWithValue("$since", Stamp(since));

        var list = new List<RunRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new RunRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        return list;
    }

    /// <summary>Tool calls started at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<ToolCallRecord> ToolCallsSince(DateTimeOffset since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT call_id, agent_id, tool_name, plugin_type, outcome, duration_ms,
                   result_chars, started_at, working_dir
            FROM tool_calls WHERE started_at >= $since ORDER BY started_at DESC;
            """;
        cmd.Parameters.AddWithValue("$since", Stamp(since));

        var list = new List<ToolCallRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ToolCallRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        return list;
    }

    /// <summary>Compactions at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<CompactionRecord> CompactionsSince(DateTimeOffset since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, at, before_tokens, after_tokens, trigger, working_dir
            FROM compactions WHERE at >= $since ORDER BY at DESC;
            """;
        cmd.Parameters.AddWithValue("$since", Stamp(since));

        var list = new List<CompactionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new CompactionRecord(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return list;
    }

    /// <summary>Permission decisions at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<PermissionRecord> PermissionsSince(DateTimeOffset since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, at, kind, decision, requester, working_dir
            FROM permissions WHERE at >= $since ORDER BY at DESC;
            """;
        cmd.Parameters.AddWithValue("$since", Stamp(since));

        var list = new List<PermissionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new PermissionRecord(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return list;
    }
}
