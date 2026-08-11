using System.Globalization;
using System.Text.Json;
using CxAgent.Core.Models;
using Microsoft.Data.Sqlite;

namespace CxAgent.Core.Storage;

/// <summary>
/// What one agent had in its context, on disk, so a crash is recoverable.
///
/// <para>WRITTEN AS TURNS COMPLETE, not at exit — a crash is precisely when exit does not happen.
/// Everything worth saving already lives in one place under one key, because the agent owns its
/// context, its identity and its ledger for the life of the session; this stores that and nothing
/// else. It is a RESUME BUFFER, not an archive: persistence-as-history is a different feature with
/// different requirements, and pretending one is the other produces a database that grows forever
/// and a schema that serves neither.</para>
///
/// <para>ONE ROW PER AGENT, REPLACED. Not a row per message: compression rewrites the conversation
/// wholesale — messages removed, a summary inserted at the head — so an append-only message log would
/// have to be reconciled against a list that no longer matches it. Rewriting the whole context is a
/// few KB per turn against a local file, and it makes the stored state exactly the in-memory state
/// with no merge step to get wrong.</para>
///
/// <para>BEST-EFFORT THROUGHOUT. Every operation swallows its failures: an agent that cannot save is
/// degraded, an agent that crashes because it could not save is broken. This is the same contract
/// <see cref="LogFileManager"/> has for a failed append.</para>
/// </summary>
public sealed class SqliteSessionStore
{
    private readonly string _connectionString;

    public SqliteSessionStore(AppPaths paths)
    {
        paths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        TryCreateSchema();
    }

    /// <summary>Opens a connection with WAL applied, as every operation here does.</summary>
    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void TryCreateSchema()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_sessions (
                    agent_id TEXT PRIMARY KEY,
                    context_json TEXT NOT NULL,
                    input_tokens INTEGER NOT NULL DEFAULT 0,
                    output_tokens INTEGER NOT NULL DEFAULT 0,
                    finished INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL,
                    working_dir TEXT);
                """;
            cmd.ExecuteNonQuery();

            // THE COLUMN ON AN EXISTING DATABASE. `CREATE TABLE IF NOT EXISTS` does exactly nothing
            // when the table is already there, so a user upgrading in place would keep the old shape
            // and every INSERT naming working_dir would fail — silently, since every operation here
            // is best-effort. Their resume would simply stop working with no message.
            AddColumnIfMissing(conn, "agent_sessions", "working_dir", "TEXT");

            using var index = conn.CreateCommand();
            // KEYED BY FOLDER FIRST, because that is now the leading predicate of every read. The old
            // index on (finished, updated_at) is left alone rather than dropped: it costs one page of
            // a table that holds a handful of rows, and dropping it is a migration that can fail.
            index.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_sessions_by_dir
                    ON agent_sessions(working_dir, finished, updated_at);
                """;
            index.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // No database means no resume. It must not mean no app.
        }
    }

    /// <summary>
    /// Adds a column when it is not already there, for a database created before it existed.
    ///
    /// <para>SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so the check is a <c>PRAGMA</c> read. The
    /// alternative — running the ALTER and swallowing the "duplicate column" error — cannot tell that
    /// failure apart from a real one, and this store swallows everything.</para>
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;

        reader.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Records the agent's whole context and ledger totals, replacing whatever it had before.
    /// </summary>
    /// <param name="workingDir">
    /// The folder this session is running in — what scopes it for resume.
    ///
    /// <para>Optional so the ~10 existing call sites and tests keep compiling, but a session saved
    /// without one can never be OFFERED (see <see cref="LoadLatestUnfinished"/>): a row that does not
    /// say where it came from could have come from anywhere, and offering it is the bug this
    /// exists to fix.</para>
    /// </param>
    public void SaveTurn(string agentId, IReadOnlyList<ChatMessage> context,
        int inputTokens, int outputTokens, string? workingDir = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(context, JsonOptions);

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_sessions
                    (agent_id, context_json, input_tokens, output_tokens, finished, updated_at, working_dir)
                VALUES ($id, $json, $in, $out, 0, $at, $dir)
                ON CONFLICT(agent_id) DO UPDATE SET
                    context_json=$json, input_tokens=$in, output_tokens=$out, updated_at=$at,
                    working_dir=$dir;
                """;
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$in", inputTokens);
            cmd.Parameters.AddWithValue("$out", outputTokens);
            cmd.Parameters.AddWithValue("$at", Ts(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$dir", (object?)workingDir ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Best-effort: see the class doc. A turn that ran is not undone by failing to record it.
        }
    }

    /// <summary>
    /// The most recently updated session that was never marked finished, or null if there is none.
    ///
    /// <para>NEWEST FIRST, because two crashed sessions can both be sitting here and resuming the
    /// older one would silently discard the more recent work.</para>
    /// </summary>
    /// <param name="workingDir">
    /// Only a session from THIS folder is offered.
    ///
    /// <para>WITHOUT THIS, the most recent unfinished session ANYWHERE on the machine was offered
    /// wherever cxagent next started — and accepting it restored another project's conversation into
    /// this one: its file paths, its code, its decisions, all describing a tree that is not the one
    /// you are in. The agent then reasons from context about files that do not exist here, and the
    /// dialog said only "an earlier session ended without closing (N messages, last active 5m ago)",
    /// never WHERE.</para>
    ///
    /// <para>Permission rules were scoped this way from the start — "a grant made in one project must
    /// never silently cover another" (PermissionRulesStore) — and the same argument always applied
    /// here. Only one of the two stores got it.</para>
    ///
    /// <para>A NULL working_dir IS NEVER OFFERED. Those are rows written before this column existed;
    /// they could be from anywhere, which is precisely the condition being fixed. Prune drops them in
    /// time, and the cost of ignoring them is one lost resume for a session that predates the fix.</para>
    /// </param>
    public SessionSnapshot? LoadLatestUnfinished(string? workingDir = null)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT agent_id, context_json, input_tokens, output_tokens, updated_at
                FROM agent_sessions
                WHERE finished = 0 AND working_dir IS NOT NULL AND working_dir = $dir
                ORDER BY updated_at DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$dir", (object?)workingDir ?? DBNull.Value);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            var context = JsonSerializer.Deserialize<List<ChatMessage>>(r.GetString(1), JsonOptions);
            if (context is null) return null;

            return new SessionSnapshot(r.GetString(0), context, r.GetInt32(2), r.GetInt32(3),
                ParseTs(r.GetString(4)));
        }
        catch (Exception)
        {
            // An unreadable or corrupt store offers no resume, which is the safe direction.
            return null;
        }
    }

    /// <summary>
    /// Marks a session as ended normally, so it is never offered for resume. The distinction this
    /// whole store turns on: an unfinished row means the process did not get to say goodbye.
    /// </summary>
    public void MarkFinished(string agentId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE agent_sessions SET finished = 1, updated_at = $at WHERE agent_id = $id;";
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.Parameters.AddWithValue("$at", Ts(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Worst case the session is offered for resume once and declined.
        }
    }

    /// <summary>
    /// Drops finished sessions older than <paramref name="keepFinishedFor"/>.
    ///
    /// <para>UNFINISHED ROWS ARE NEVER PRUNED, however old. They are the only thing here that cannot
    /// be reconstructed, and a machine left off over a weekend must not lose one.</para>
    /// </summary>
    public void Prune(TimeSpan keepFinishedFor)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM agent_sessions WHERE finished = 1 AND updated_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", Ts(DateTimeOffset.UtcNow - keepFinishedFor));
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Housekeeping. Failing it costs disk, not correctness.
        }
    }

    /// <summary>How long a finished session stays resumable-by-mistake before being dropped. A week
    /// is long enough to cover a holiday and short enough that the buffer stays a buffer.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>How many sessions are stored, finished or not. Diagnostic — it is what a retention
    /// test can assert on without reaching past this type into the schema.</summary>
    public int CountSessions()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM agent_sessions;";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Round-tripping <see cref="ChatMessage"/> verbatim. <c>ToolCall.Arguments</c> is a
    /// <c>JsonElement</c>, which serialises as its own document and reads back as one — the
    /// round-trip that must not quietly drop it is covered by a test, because the symptom (a model
    /// never seeing a tool result it asked for) appears nowhere near the cause.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static string Ts(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTs(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>One recoverable session, as it was at the last completed turn.</summary>
public sealed record SessionSnapshot(
    string AgentId,
    IReadOnlyList<ChatMessage> Context,
    int InputTokens,
    int OutputTokens,
    DateTimeOffset UpdatedAt);
