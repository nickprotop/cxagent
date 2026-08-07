using System.Globalization;
using System.Text.Json;
using CxAgent.Core.Models;
using Microsoft.Data.Sqlite;

namespace CxAgent.Core.Storage;

/// <summary>
/// SQLite-backed IGoalStore using raw parameterized SQL. Every connection enables
/// foreign keys and WAL. Serialization reuses P1's Dictionary&lt;string,object?&gt;
/// round-trip (values reload as JsonElement, which JobParameters.Get&lt;T&gt; converts).
/// </summary>
public class SqliteGoalStore : IGoalStore
{
    private readonly string _connectionString;
    private readonly LogFileManager? _logs;

    public SqliteGoalStore(AppPaths paths, LogFileManager? logs = null)
    {
        paths.EnsureCreated();
        _logs = logs;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        CreateSchema();
    }

    // Opens a connection with FK + WAL applied. Every operation goes through this.
    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void CreateSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS goals (
                id TEXT PRIMARY KEY, description TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'Active', provider_id TEXT NOT NULL,
                created_at TEXT NOT NULL, completed_at TEXT);
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
                plugin_type TEXT NOT NULL, display_name TEXT NOT NULL,
                plan_local_id TEXT,
                parameters_json TEXT NOT NULL, depends_on_json TEXT NOT NULL DEFAULT '[]',
                state TEXT NOT NULL DEFAULT 'Pending', created_at TEXT NOT NULL,
                started_at TEXT, completed_at TEXT,
                retry_count INTEGER NOT NULL DEFAULT 0, max_retries INTEGER NOT NULL DEFAULT 3,
                result_json TEXT, log_file TEXT);
            CREATE TABLE IF NOT EXISTS chat_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
                role TEXT NOT NULL, content TEXT NOT NULL,
                tool_calls_json TEXT, tool_call_id TEXT, timestamp TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_jobs_goal ON jobs(goal_id);
            CREATE INDEX IF NOT EXISTS idx_chat_goal ON chat_messages(goal_id);
            """;
        cmd.ExecuteNonQuery();
        AddColumnIfMissing(conn, "jobs", "plan_local_id", "TEXT");
        AddColumnIfMissing(conn, "jobs", "orchestrator_edit_count", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS is a no-op on a database that already has the table, so a column
    /// added later reaches only fresh installs unless it is also ALTERed in. SQLite has no
    /// ADD COLUMN IF NOT EXISTS, hence the table_info probe.
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col;";
        probe.Parameters.AddWithValue("$col", column);
        if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    private static string Ts(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTs(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? TsN(DateTimeOffset? d) => d.HasValue ? Ts(d.Value) : null;

    // ---- Goals -------------------------------------------------------------

    public async Task SaveGoalAsync(Goal goal)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO goals (id, description, state, provider_id, created_at, completed_at)
            VALUES ($id, $desc, $state, $pid, $created, $completed)
            ON CONFLICT(id) DO UPDATE SET
                description=$desc, state=$state, provider_id=$pid,
                created_at=$created, completed_at=$completed;
            """;
        cmd.Parameters.AddWithValue("$id", goal.Id);
        cmd.Parameters.AddWithValue("$desc", goal.Description);
        cmd.Parameters.AddWithValue("$state", goal.State.ToString());
        cmd.Parameters.AddWithValue("$pid", goal.ProviderId);
        cmd.Parameters.AddWithValue("$created", Ts(goal.CreatedAt));
        cmd.Parameters.AddWithValue("$completed", (object?)TsN(goal.CompletedAt) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Goal?> GetGoalAsync(string goalId)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, description, state, provider_id, created_at, completed_at FROM goals WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", goalId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return ReadGoal(r);
    }

    public async Task<List<Goal>> ListGoalsAsync(int limit = 50, int offset = 0)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, description, state, provider_id, created_at, completed_at FROM goals ORDER BY created_at DESC LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return await ReadGoals(cmd);
    }

    public async Task<List<Goal>> ListGoalsByStateAsync(params GoalState[] states)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        var names = states.Select((_, i) => "$s" + i).ToList();
        cmd.CommandText = $"SELECT id, description, state, provider_id, created_at, completed_at FROM goals WHERE state IN ({string.Join(",", names)});";
        for (int i = 0; i < states.Length; i++) cmd.Parameters.AddWithValue("$s" + i, states[i].ToString());
        return await ReadGoals(cmd);
    }

    private static async Task<List<Goal>> ReadGoals(SqliteCommand cmd)
    {
        var list = new List<Goal>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadGoal(r));
        return list;
    }

    private static Goal ReadGoal(System.Data.Common.DbDataReader r) => new()
    {
        Id = r.GetString(0),
        Description = r.GetString(1),
        State = Enum.Parse<GoalState>(r.GetString(2)),
        ProviderId = r.GetString(3),
        CreatedAt = ParseTs(r.GetString(4)),
        CompletedAt = r.IsDBNull(5) ? null : ParseTs(r.GetString(5)),
    };

    // ---- Jobs --------------------------------------------------------------

    public async Task SaveJobAsync(Job job)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (id, goal_id, plugin_type, display_name, plan_local_id, parameters_json, depends_on_json,
                              state, created_at, started_at, completed_at, retry_count, max_retries, result_json, log_file,
                              orchestrator_edit_count)
            VALUES ($id, $gid, $type, $name, $local, $params, $deps, $state, $created, $started, $completed, $rc, $mr, $result, $log, $oec)
            ON CONFLICT(id) DO UPDATE SET
                plugin_type=$type, display_name=$name, plan_local_id=$local,
                parameters_json=$params, depends_on_json=$deps,
                state=$state, created_at=$created, started_at=$started, completed_at=$completed,
                retry_count=$rc, max_retries=$mr, result_json=$result, log_file=$log,
                orchestrator_edit_count=$oec;
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$gid", job.GoalId);
        cmd.Parameters.AddWithValue("$type", job.PluginType);
        cmd.Parameters.AddWithValue("$name", job.DisplayName);
        cmd.Parameters.AddWithValue("$local", (object?)job.PlanLocalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$params", JsonSerializer.Serialize(job.Parameters.Values));
        cmd.Parameters.AddWithValue("$deps", JsonSerializer.Serialize(job.DependsOn));
        cmd.Parameters.AddWithValue("$state", job.State.ToString());
        cmd.Parameters.AddWithValue("$created", Ts(job.CreatedAt));
        cmd.Parameters.AddWithValue("$started", (object?)TsN(job.StartedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", (object?)TsN(job.CompletedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", job.RetryCount);
        cmd.Parameters.AddWithValue("$mr", job.MaxRetries);
        cmd.Parameters.AddWithValue("$result", (object?)(job.Result is null ? null : JsonSerializer.Serialize(job.Result)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$log", (object?)job.Result?.LogFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oec", job.OrchestratorEditCount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Job>> GetJobsForGoalAsync(string goalId)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, goal_id, plugin_type, display_name, parameters_json, depends_on_json,
                   state, created_at, started_at, completed_at, retry_count, max_retries, result_json,
                   plan_local_id, orchestrator_edit_count
            FROM jobs WHERE goal_id=$gid ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$gid", goalId);
        var list = new List<Job>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var jobId = r.GetString(0);
            try
            {
                var paramsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(r.GetString(4)) ?? new();
                var deps = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? new();
                JobResult? result = r.IsDBNull(12) ? null : JsonSerializer.Deserialize<JobResult>(r.GetString(12));
                list.Add(new Job
                {
                    Id = jobId,
                    GoalId = r.GetString(1),
                    PluginType = r.GetString(2),
                    DisplayName = r.GetString(3),
                    // The id the orchestrator's own plan used ("r1"). ResumeService rebuilds the DAG
                    // from these rows, so dropping it here leaves every {{r1.content}} in a
                    // not-yet-run job unresolvable after a restart.
                    PlanLocalId = r.IsDBNull(13) ? null : r.GetString(13),
                    Parameters = new JobParameters(paramsDict),
                    DependsOn = deps,
                    State = Enum.Parse<JobState>(r.GetString(6)),
                    CreatedAt = ParseTs(r.GetString(7)),
                    StartedAt = r.IsDBNull(8) ? null : ParseTs(r.GetString(8)),
                    CompletedAt = r.IsDBNull(9) ? null : ParseTs(r.GetString(9)),
                    RetryCount = r.GetInt32(10),
                    MaxRetries = r.GetInt32(11),
                    Result = result,
                    // Must survive a restart, or the fail-edit-fail loop's per-job budget resets on
                    // every resume — exactly the runaway OrchestratorSettings.MaxEditsPerJob exists
                    // to stop. See Job.OrchestratorEditCount.
                    OrchestratorEditCount = r.GetInt32(14),
                });
            }
            catch (JsonException ex)
            {
                throw new PersistenceException($"Failed to deserialize job '{jobId}'.", goalId, jobId, ex);
            }
        }
        return list;
    }

    // ---- Chat (implemented in Task 3) --------------------------------------

    public async Task SaveChatMessageAsync(string goalId, ChatMessage message)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_messages (goal_id, role, content, tool_calls_json, tool_call_id, timestamp)
            VALUES ($gid, $role, $content, $tc, $tcid, $ts);
            """;
        cmd.Parameters.AddWithValue("$gid", goalId);
        cmd.Parameters.AddWithValue("$role", message.Role);
        cmd.Parameters.AddWithValue("$content", message.Content);
        cmd.Parameters.AddWithValue("$tc", (object?)(message.ToolCalls is null ? null : JsonSerializer.Serialize(message.ToolCalls)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tcid", (object?)message.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", Ts(message.Timestamp));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ChatMessage>> GetConversationAsync(string goalId)
    {
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT role, content, tool_calls_json, tool_call_id, timestamp FROM chat_messages WHERE goal_id=$gid ORDER BY id;";
        cmd.Parameters.AddWithValue("$gid", goalId);

        var all = new List<ChatMessage>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                List<ToolCall>? toolCalls = r.IsDBNull(2) ? null : JsonSerializer.Deserialize<List<ToolCall>>(r.GetString(2));
                all.Add(new ChatMessage
                {
                    Role = r.GetString(0),
                    Content = r.GetString(1),
                    ToolCalls = toolCalls,
                    ToolCallId = r.IsDBNull(3) ? null : r.GetString(3),
                    Timestamp = ParseTs(r.GetString(4)),
                });
            }
        }

        // Drop any tool-result whose matching tool-use id is absent, so the replayed
        // transcript is provider-valid (a tool_result with no preceding tool_use is rejected).
        var presentToolUseIds = all
            .Where(m => m.ToolCalls is not null)
            .SelectMany(m => m.ToolCalls!)
            .Select(tc => tc.Id)
            .Where(id => id is not null)
            .ToHashSet();

        return all.Where(m => m.ToolCallId is null || presentToolUseIds.Contains(m.ToolCallId)).ToList();
    }

    // ---- Delete ------------------------------------------------------------

    public async Task DeleteGoalAsync(string goalId)
    {
        // Row cascade (jobs + chat_messages) requires foreign_keys=ON, applied in Open().
        await using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM goals WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", goalId);
        await cmd.ExecuteNonQueryAsync();

        // The FK cascade covers rows, not files — remove the goal's log directory too.
        _logs?.DeleteGoalLogs(goalId);
    }
}
