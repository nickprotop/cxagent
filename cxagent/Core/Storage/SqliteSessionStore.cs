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

            // THE TITLE, for the listing. Derivable from context_json — the first user message is in
            // there — but deriving it means deserialising every session's WHOLE conversation to
            // render one line each, which is the wrong cost for a list. Written once, read directly.
            AddColumnIfMissing(conn, "agent_sessions", "title", "TEXT");
            BackfillTitles(conn);

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
    /// <summary>
    /// Titles the sessions that existed before the column did.
    ///
    /// <para>WITHOUT THIS, EVERY SESSION A USER ALREADY HAS reads "(no messages yet)" in the listing
    /// — the one row-level fact that makes a row recognisable, absent on exactly the rows most worth
    /// resuming, and never filled in because a title is written on save and a past session is not
    /// going to be saved again.</para>
    ///
    /// <para>ONCE, AT MIGRATION, rather than derived per read: the title lives inside
    /// <c>context_json</c>, so deriving it on the fly means deserialising every conversation in full
    /// to render one line each. Here the cost is paid a single time, for a handful of rows.</para>
    /// </summary>
    private void BackfillTitles(SqliteConnection conn)
    {
        try
        {
            var pending = new List<(string Id, string Json)>();

            using (var read = conn.CreateCommand())
            {
                read.CommandText =
                    "SELECT agent_id, context_json FROM agent_sessions WHERE title IS NULL;";
                using var r = read.ExecuteReader();
                while (r.Read())
                    if (!r.IsDBNull(1)) pending.Add((r.GetString(0), r.GetString(1)));
            }

            foreach (var (id, json) in pending)
            {
                var context = JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOptions);
                if (context is null || TitleOf(context) is not { } title) continue;

                using var write = conn.CreateCommand();
                write.CommandText =
                    "UPDATE agent_sessions SET title = $t WHERE agent_id = $id;";
                write.Parameters.AddWithValue("$t", title);
                write.Parameters.AddWithValue("$id", id);
                write.ExecuteNonQuery();
            }
        }
        catch (Exception)
        {
            // Best-effort, like the rest of this file: an untitled row is a worse listing, never a
            // failed startup. A session that cannot be deserialised here also cannot be resumed.
        }
    }

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

            // THE FIRST USER MESSAGE NAMES THE SESSION. It is what a person recognises a conversation
            // by — a ULID identifies without describing, and a size and an age describe without
            // identifying. Recomputed on every save and written with COALESCE below so the FIRST one
            // sticks: later turns must not retitle a session the user already knows by its opening.
            var title = TitleOf(context);

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_sessions
                    (agent_id, context_json, input_tokens, output_tokens, finished, updated_at,
                     working_dir, title)
                VALUES ($id, $json, $in, $out, 0, $at, $dir, $title)
                ON CONFLICT(agent_id) DO UPDATE SET
                    context_json=$json, input_tokens=$in, output_tokens=$out, updated_at=$at,
                    working_dir=$dir,
                    -- COALESCE, so the first title wins. A session is named by how it opened.
                    title=COALESCE(agent_sessions.title, $title);
                """;
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$in", inputTokens);
            cmd.Parameters.AddWithValue("$out", outputTokens);
            cmd.Parameters.AddWithValue("$at", Ts(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$dir", (object?)workingDir ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
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
    /// <summary>
    /// Every session, newest first — folder-scoped unless <paramref name="all"/>.
    /// </summary>
    /// <param name="all">
    /// Across every folder, with the folder shown. LISTING across folders is safe; RESTORING across
    /// them fills a context with another project's files, which is the caller's decision to make.
    /// </param>
    /// <remarks>
    /// FINISHED ROWS ARE INCLUDED. Why a session ended is worth seeing and is not a reason to hide
    /// it — the flag gates what <c>--resume</c> picks by DEFAULT, not what can be reached.
    /// </remarks>
    public IReadOnlyList<SessionInfo> List(string? workingDir = null, bool all = false)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = all
                ? """
                  SELECT agent_id, title, working_dir, input_tokens, output_tokens, finished, updated_at
                  FROM agent_sessions ORDER BY updated_at DESC;
                  """
                : """
                  SELECT agent_id, title, working_dir, input_tokens, output_tokens, finished, updated_at
                  FROM agent_sessions
                  WHERE working_dir IS NOT NULL AND working_dir = $dir
                  ORDER BY updated_at DESC;
                  """;
            if (!all) cmd.Parameters.AddWithValue("$dir", (object?)workingDir ?? DBNull.Value);

            var rows = new List<SessionInfo>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new SessionInfo(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetInt32(3),
                    r.GetInt32(4),
                    r.GetInt32(5) != 0,
                    DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture)));

            return rows;
        }
        catch (Exception)
        {
            // Best-effort, like every read here: no list is a list you cannot use, not a crash.
            return [];
        }
    }

    /// <summary>
    /// One session by uid, or by any unambiguous ABBREVIATION of one — from either end.
    ///
    /// <para>ABBREVIATIONS BECAUSE A ULID IS 26 CHARACTERS and unusable at a prompt. But the git
    /// habit of taking the FIRST few does not carry over: a commit hash is random from character
    /// one, while a ULID opens with a timestamp, so every session started in the same few minutes
    /// shares a leading prefix. The listing therefore shows the tail, and this matches both — the
    /// tail a user reads off the screen, and the head of a full uid pasted from somewhere else.</para>
    ///
    /// <para>AMBIGUITY IS REPORTED, NEVER RESOLVED. Picking the newest match silently is how someone
    /// restores the wrong conversation and does not find out for ten minutes.</para>
    /// </summary>
    public UidLookup LoadByUid(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return new UidLookup(null, []);

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // EITHER END, because a ULID starts with a timestamp. Sessions begun in the same few
            // minutes share their opening characters, so the listing shows the TAIL — the random
            // half — and a leading-prefix match alone could never resolve what a user reads off the
            // screen. A full uid pasted from --sessions or an exit hint still matches from the front.
            cmd.CommandText = """
                SELECT agent_id FROM agent_sessions
                WHERE agent_id LIKE $p ESCAPE '\' OR agent_id LIKE $s ESCAPE '\'
                ORDER BY updated_at DESC;
                """;
            // Case-insensitive by hand: a user reads a lowercase id off the screen and a ULID is
            // stored uppercase, so an exact LIKE would never match what they just typed.
            var needle = Escape(prefix.Trim().ToUpperInvariant());
            cmd.Parameters.AddWithValue("$p", needle + "%");
            cmd.Parameters.AddWithValue("$s", "%" + needle);

            var matches = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) matches.Add(r.GetString(0));

            if (matches.Count == 0) return new UidLookup(null, []);
            if (matches.Count > 1) return new UidLookup(null, matches);

            return new UidLookup(LoadById(matches[0]), []);
        }
        catch (Exception)
        {
            return new UidLookup(null, []);
        }
    }

    /// <summary>One session by its exact id, finished or not.</summary>
    public SessionSnapshot? LoadById(string agentId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT agent_id, context_json, input_tokens, output_tokens, updated_at
                FROM agent_sessions WHERE agent_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", agentId);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            var context = JsonSerializer.Deserialize<List<ChatMessage>>(r.GetString(1), JsonOptions);
            if (context is null) return null;

            return new SessionSnapshot(r.GetString(0), context, r.GetInt32(2), r.GetInt32(3),
                DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>LIKE wildcards in a user-supplied prefix would match more than they typed.</summary>
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// The first user message, clipped — what a person recognises a conversation by.
    /// </summary>
    /// <remarks>
    /// Null when there is no user message yet: a session that has only a system prompt has not been
    /// given a subject, and inventing one from the prompt would name every session the same thing.
    /// </remarks>
    public static string? TitleOf(IReadOnlyList<ChatMessage> context)
    {
        var first = context.FirstOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.Ordinal)
            && m.ToolCallId is null
            && !string.IsNullOrWhiteSpace(m.Content));

        if (first is null) return null;

        var text = first.Content.ReplaceLineEndings(" ").Trim();
        return text.Length <= 80 ? text : text[..80].TrimEnd() + "…";
    }

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
/// <summary>
/// One row of the session list — enough to recognise a conversation by, and nothing more.
///
/// <para>DELIBERATELY NOT <see cref="SessionSnapshot"/>, which carries the whole message list.
/// Rendering ten rows must not cost ten conversation deserialisations.</para>
/// </summary>
/// <param name="Uid">The agent id. Shown as its last six characters, matched from either end — see
/// <c>LoadByUid</c>, which explains why a ULID cannot be abbreviated from the front.</param>
/// <param name="Title">The first user message, clipped, or null for a session that never got one.</param>
/// <param name="Finished">
/// Ended cleanly, OR superseded by a resume. Two meanings in one flag: see the resume path, which
/// retires the row it restored so one conversation cannot be accepted twice.
/// </param>
public sealed record SessionInfo(
    string Uid,
    string? Title,
    string? WorkingDir,
    int InputTokens,
    int OutputTokens,
    bool Finished,
    DateTimeOffset UpdatedAt);

/// <summary>What a uid lookup found. Ambiguity is REPORTED, never resolved to the newest match —
/// silently picking is how someone restores the wrong conversation and does not notice.</summary>
public sealed record UidLookup(SessionSnapshot? Session, IReadOnlyList<string> Ambiguous)
{
    public bool IsAmbiguous => Ambiguous.Count > 1;
}

public sealed record SessionSnapshot(
    string AgentId,
    IReadOnlyList<ChatMessage> Context,
    int InputTokens,
    int OutputTokens,
    DateTimeOffset UpdatedAt);
