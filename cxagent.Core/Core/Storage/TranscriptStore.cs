using Microsoft.Data.Sqlite;

namespace CxAgent.Core.Storage;

/// <summary>
/// What a session's front end was shown, kept so another one can be shown the same.
///
/// <para>WHY NOT THE ARCHIVE. <see cref="UsageHistoryStore"/> records measurements and says so: a
/// tool call's duration and result SIZE, not its arguments or its output. Adding those would put
/// whatever the agent touched — a token in a command, a key in a file it read — permanently into an
/// append-only store that <c>/stats</c> also reads. This is a different thing with different
/// retention: disposable, deleted with its session, worth nothing but scrollback.</para>
///
/// <para><b>A ROW IS NOT AN EVENT.</b> Assistant text arrives token by token and a running tool
/// streams progress continuously, so storing every emitted event would be thousands of rows a turn.
/// A message is ONE row that grows and a job is ONE row that is updated, which makes a replay a
/// reconstruction of the transcript rather than a recording of the event stream — and makes "the
/// last twenty" mean twenty messages rather than twenty tokens.</para>
///
/// <para>THAT IS A DECISION ABOUT THE WIRE, not only about the disk. A client asking for a window
/// asks for N of something, and this is what N counts.</para>
/// </summary>
public sealed class TranscriptStore
{
    private readonly string _connectionString;

    public TranscriptStore(AppPaths paths)
    {
        paths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.TranscriptPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        TryCreateSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // WAL AND A BUSY TIMEOUT, matching the other stores: a reader replaying a session must not
        // block the writer recording one, and two sessions writing at once wait rather than throw.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void TryCreateSchema()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            // ORDERED BY seq WITHIN A SESSION, not by timestamp. Two events in the same millisecond
            // are ordinary — a token stream produces many — and a replay that reordered them would
            // render nonsense. The sequence is assigned by the writer.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS entries (
                    session_id  TEXT NOT NULL,
                    seq         INTEGER NOT NULL,
                    at          TEXT NOT NULL,
                    kind        TEXT NOT NULL,
                    role        TEXT,
                    body        TEXT,
                    PRIMARY KEY (session_id, seq));

                CREATE INDEX IF NOT EXISTS idx_entries_session ON entries(session_id, seq);
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // NO TRANSCRIPT MEANS NO REPLAY. It must not mean no app — the same stance the usage
            // store takes, and more clearly right here, since this store is disposable by design.
        }
    }

    /// <summary>
    /// Appends one entry and answers the sequence it took.
    ///
    /// <para>THE CALLER OWNS THE SEQUENCE because it owns the ordering. A store that numbered rows
    /// itself would order them by arrival, and arrival order is exactly what a concurrent producer
    /// does not guarantee.</para>
    /// </summary>
    public void Append(string sessionId, long seq, string kind, string? role, string? body)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entries (session_id, seq, at, kind, role, body)
                VALUES ($s, $q, $at, $k, $r, $b)
                ON CONFLICT(session_id, seq) DO UPDATE SET body = excluded.body;
                """;
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$q", seq);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$r", (object?)role ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// A window of a session's transcript, oldest first.
    ///
    /// <para>PAGED RATHER THAN CAPPED. Nothing is discarded when it is written; a client asks for
    /// what it can show and scrolls back for more. Capping at write time would make a replay
    /// silently lossy, and the loss invisible unless every client printed a caveat.</para>
    /// </summary>
    /// <param name="sessionId">The session, by its own id rather than an agent's.</param>
    /// <param name="before">Return entries with a lower sequence than this, or null for the latest.</param>
    /// <param name="limit">How many at most.</param>
    public IReadOnlyList<TranscriptEntry> Window(string sessionId, long? before = null, int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        // NEWEST FIRST IN SQL, REVERSED IN MEMORY: "the last N before X" is a descending query, and
        // a caller wants them in reading order.
        cmd.CommandText = before is null
            ? "SELECT seq, at, kind, role, body FROM entries WHERE session_id = $s ORDER BY seq DESC LIMIT $n;"
            : "SELECT seq, at, kind, role, body FROM entries WHERE session_id = $s AND seq < $b ORDER BY seq DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$n", limit);
        if (before is { } b) cmd.Parameters.AddWithValue("$b", b);

        var rows = new List<TranscriptEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TranscriptEntry(
                Seq: reader.GetInt64(0),
                At: DateTimeOffset.Parse(reader.GetString(1)),
                Kind: reader.GetString(2),
                Role: reader.IsDBNull(3) ? null : reader.GetString(3),
                Body: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        rows.Reverse();
        return rows;
    }

    /// <summary>
    /// Forgets a session.
    ///
    /// <para>DISPOSABLE BY DESIGN — this store exists to replay a conversation to a front end, so a
    /// session nobody can attach to any more has nothing left to say.</para>
    /// </summary>
    public void Forget(string sessionId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM entries WHERE session_id = $s;";
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception) { }
    }
}

/// <summary>One entry of a session's transcript.</summary>
/// <param name="Seq">Its place in the session's order — assigned by the writer, not by arrival.</param>
/// <param name="At">When it was recorded.</param>
/// <param name="Kind">What it is: a message, a tool row, a system note.</param>
/// <param name="Role">Whose message, when it is one.</param>
/// <param name="Body">The text as it was shown, already coalesced.</param>
public sealed record TranscriptEntry(
    long Seq, DateTimeOffset At, string Kind, string? Role, string? Body);
