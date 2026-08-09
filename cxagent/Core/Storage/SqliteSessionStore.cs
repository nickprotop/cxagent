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
                    updated_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS idx_sessions_unfinished
                    ON agent_sessions(finished, updated_at);
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // No database means no resume. It must not mean no app.
        }
    }

    /// <summary>
    /// Records the agent's whole context and ledger totals, replacing whatever it had before.
    /// </summary>
    public void SaveTurn(string agentId, IReadOnlyList<ChatMessage> context,
        int inputTokens, int outputTokens)
    {
        try
        {
            var json = JsonSerializer.Serialize(context, JsonOptions);

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_sessions
                    (agent_id, context_json, input_tokens, output_tokens, finished, updated_at)
                VALUES ($id, $json, $in, $out, 0, $at)
                ON CONFLICT(agent_id) DO UPDATE SET
                    context_json=$json, input_tokens=$in, output_tokens=$out, updated_at=$at;
                """;
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$in", inputTokens);
            cmd.Parameters.AddWithValue("$out", outputTokens);
            cmd.Parameters.AddWithValue("$at", Ts(DateTimeOffset.UtcNow));
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
    public SessionSnapshot? LoadLatestUnfinished()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT agent_id, context_json, input_tokens, output_tokens, updated_at
                FROM agent_sessions
                WHERE finished = 0
                ORDER BY updated_at DESC
                LIMIT 1;
                """;

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
