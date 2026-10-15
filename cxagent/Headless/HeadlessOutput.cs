using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.UI;

namespace CxAgent.Headless;

public sealed record RunStart(string? SessionId, string Cwd, string Model, string PermissionMode,
    IReadOnlyList<string> Tools, IReadOnlyList<(string Name, string Status)> McpServers,
    IReadOnlyList<string> Plugins, RunSkipped Skipped, bool Trusted);

/// <summary>What a run left out, kept apart by kind — the spec's <c>skipped.plugins</c> / <c>skipped.mcp</c>.</summary>
public sealed record RunSkipped(IReadOnlyList<string> Plugins, IReadOnlyList<string> Mcp)
{
    public static RunSkipped None { get; } = new([], []);
    public IEnumerable<string> All => Plugins.Concat(Mcp);
}

public sealed record RunSpend(int InputTokens, int OutputTokens, int CachedTokens, decimal? CostUsd, string Model);

public sealed record RunResult(HeadlessEnd End, string? Text, IReadOnlyList<string> Errors,
    string? SessionId, int Turns, long DurationMs, RunSpend Spend, IReadOnlyList<string> KilledJobs);

public sealed record HeadlessIo(TextWriter Out, TextWriter Err);

/// <summary>
/// What a headless run writes, in one of Claude Code's three formats.
///
/// <para>THE SESSION'S OBSERVERS. Text, tool calls and notices reach a headless run through the
/// same two interfaces the TUI's transcript implements, so nothing in Core knows which front end is
/// watching.</para>
/// </summary>
public abstract class HeadlessOutput : ISessionObserver, IToolObserver
{
    private readonly HeadlessIo _io;
    private readonly List<JsonObject> _denials = [];
    private readonly Dictionary<string, string> _parentOfAgent = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private readonly object _gate = new();
    private RunStart? _start;

    protected HeadlessOutput(HeadlessIo io) => _io = io;

    // THE OPTIONS, NOT THEIR FLAGS ONE BY ONE: verbose and partial are both bools, and passed side by
    // side a transposition compiles and quietly swaps what the run prints.
    public static HeadlessOutput For(HeadlessOptions options, HeadlessIo io) => options.Output switch
    {
        OutputFormat.Json => new JsonOutput(io),
        OutputFormat.StreamJson => new StreamOutput(io, options.IncludePartialMessages),
        _ => new TextOutput(io, options.Verbose),
    };

    /// <summary>Set when stdout went away — `cxagent -p … | head -1`. The run still finishes.</summary>
    public bool OutputClosed { get; private set; }

    /// <summary>
    /// The last error the session said — a provider's own message, typically. The result's `errors`
    /// carries it, so a JSON consumer learns why without reading stderr.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Records an error before the format decides where it is shown.</summary>
    protected void Note(Message message)
    {
        if (message.Severity == Severity.Error) LastError = message.Text;
        else if (message.Severity == Severity.Warning) lock (_gate) _warnings.Add(message.Text);
    }

    protected void Out(string line)
    {
        lock (_gate)
        {
            if (OutputClosed) return;
            try
            {
                _io.Out.WriteLine(line);
                _io.Out.Flush();
            }
            catch (IOException)
            {
                // THE READER HUNG UP. Writing stops; the turn and the session save do not, because a
                // run broken off mid-turn would leave a half-written session behind.
                OutputClosed = true;
            }
        }
    }

    protected void Err(string line)
    {
        lock (_gate)
        {
            try { _io.Err.WriteLine(line); _io.Err.Flush(); } catch (IOException) { }
        }
    }

    /// <summary>What the run starts with; kept, so the result can report it too.</summary>
    public void Start(RunStart start)
    {
        lock (_gate) _start = start;
        OnStart(start);
    }

    protected abstract void OnStart(RunStart start);
    public abstract void Finish(RunResult result);

    public void Denied(PermissionDecisionReport report)
    {
        var denial = new JsonObject
        {
            ["tool_name"] = ToolNameFor(report.Kind),
            ["tool_use_id"] = "",
            ["tool_input"] = new JsonObject { ["subject"] = report.Subject },
        };
        lock (_gate) _denials.Add(denial);
        OnDenied(report, denial);
    }

    protected virtual void OnDenied(PermissionDecisionReport report, JsonObject denial) { }

    public void ChildSpawned(string jobId, string childAgentId)
    {
        lock (_gate) _parentOfAgent[childAgentId] = jobId;
    }

    protected string? ParentOf(string agentId)
    {
        lock (_gate) return _parentOfAgent.GetValueOrDefault(agentId);
    }

    protected JsonArray Denials()
    {
        lock (_gate) return new JsonArray([.. _denials.Select(d => (JsonNode)d.DeepClone())]);
    }

    // CLAUDE CODE'S DENIALS NAME A TOOL; cxagent's decider names a kind. The kind's own tool is the
    // nearest honest answer.
    private static string ToolNameFor(PermissionKind kind) => kind switch
    {
        PermissionKind.Shell => "run_shell",
        PermissionKind.FileWrite => "write_file",
        PermissionKind.FileRead => "read_file",
        PermissionKind.Http => "http_request",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The result object every format ends with — Claude Code's `result` shape.</summary>
    protected JsonObject ResultObject(RunResult r)
    {
        var isError = r.End != HeadlessEnd.Answered;
        var o = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = HeadlessEnds.Subtype(r.End),
            ["is_error"] = isError,
        };
        if (!isError) o["result"] = r.Text ?? "";
        else o["errors"] = new JsonArray([.. r.Errors.Select(e => (JsonNode)e)]);
        o["session_id"] = r.SessionId;
        o["num_turns"] = r.Turns;
        o["duration_ms"] = r.DurationMs;
        o["total_cost_usd"] = r.Spend.CostUsd ?? 0m;
        o["usage"] = new JsonObject
        {
            ["input_tokens"] = r.Spend.InputTokens,
            ["output_tokens"] = r.Spend.OutputTokens,
            ["cache_read_input_tokens"] = r.Spend.CachedTokens,
            ["cache_creation_input_tokens"] = 0,
        };
        o["modelUsage"] = new JsonObject
        {
            [r.Spend.Model] = new JsonObject
            {
                ["inputTokens"] = r.Spend.InputTokens,
                ["outputTokens"] = r.Spend.OutputTokens,
                ["cacheReadInputTokens"] = r.Spend.CachedTokens,
                ["costUSD"] = r.Spend.CostUsd ?? 0m,
            },
        };
        o["permission_denials"] = Denials();
        // CXAGENT'S OWN FACTS, never mixed into Claude Code's fields: a consumer written for
        // `claude -p` reads the fields above unchanged.
        RunStart? start;
        string[] warnings;
        lock (_gate) { start = _start; warnings = [.. _warnings]; }
        o["cxagent"] = new JsonObject
        {
            ["exitCode"] = HeadlessEnds.ExitCode(r.End),
            ["trusted"] = start?.Trusted ?? false,
            ["permissionMode"] = start?.PermissionMode,
            ["skipped"] = SkippedObject(start?.Skipped ?? RunSkipped.None),
            ["killedJobs"] = new JsonArray([.. r.KilledJobs.Select(j => (JsonNode)j)]),
            ["warnings"] = new JsonArray([.. warnings.Select(w => (JsonNode)w)]),
        };
        return o;
    }

    // EVERY END A CALL CAN REACH, so a cancelled or skipped call still gets its tool_result and a
    // consumer pairing tool_use with tool_result never waits on one that will not come.
    protected static bool Finished(Job job) =>
        job.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

    protected static JsonObject SkippedObject(RunSkipped s) => new()
    {
        ["plugins"] = new JsonArray([.. s.Plugins.Select(x => (JsonNode)x)]),
        ["mcp"] = new JsonArray([.. s.Mcp.Select(x => (JsonNode)x)]),
    };

    protected static string Rendered(JobResult? result)
    {
        if (result is null) return "";
        if (result.ErrorMessage is { Length: > 0 } error) return error;
        if (result.Output.TryGetValue("content", out var content) && content is not null)
            return content.ToString() ?? "";
        return string.Join("\n", result.Output.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    // ISessionObserver — the formats override what they show.
    public virtual void UserTurnAdded(ChatMessageId id, string text) { }
    public virtual void AssistantTurnBegan(ChatMessageId id) { }
    public virtual void AssistantTextAppended(ChatMessageId id, string token) { }
    public virtual void AssistantReasoningAppended(ChatMessageId id, string text) { }
    public virtual void AssistantTurnEnded(ChatMessageId id) { }
    public virtual void AssistantLabelled(ChatMessageId id, string header) { }
    public virtual void Said(Message message)
    {
        Note(message);
        Err(message.Text);
    }

    // IToolObserver
    public virtual void ToolsChanged(IReadOnlyList<Job> jobs) { }
    public virtual void ToolUpdated(Job job) { }
    public virtual void ToolProgressed(Job job) { }
    public virtual void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public virtual void ToolOutputAppended(string jobId, string delta) { }
}

/// <summary>The answer on stdout; with --verbose, progress on stderr.</summary>
internal sealed class TextOutput(HeadlessIo io, bool verbose) : HeadlessOutput(io)
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    protected override void OnStart(RunStart start)
    {
        if (!verbose) return;
        foreach (var s in start.Skipped.All) Err($"skipped: {s}");
    }

    public override void Said(Message message)
    {
        Note(message);
        // WARNINGS ARE NOT PROGRESS. "This folder is not trusted" or "a server needs a login"
        // explains why the run did what it did, so it is shown without --verbose; only the
        // informational lines wait for it.
        if (verbose || message.Severity is Severity.Error or Severity.Warning)
        {
            if (message.Severity == Severity.Error) lock (_shown) _shown.Add(message.Text);
            Err(message.Text);
        }
    }

    public override void ToolUpdated(Job job)
    {
        if (verbose && Finished(job))
            // THE DISPLAY NAME ALREADY LEADS WITH THE TOOL ("read_file · README.md").
            Err($"{job.DisplayName} · {job.State.ToString().ToLowerInvariant()}");
    }

    protected override void OnDenied(PermissionDecisionReport report, JsonObject denial)
    {
        if (verbose) Err($"refused: {report.Subject}");
    }

    public override void Finish(RunResult r)
    {
        if (r.End == HeadlessEnd.Answered && r.Text is { Length: > 0 } text) Out(text);
        // ONCE ON STDERR. The run's closing error is usually the provider's own message, already
        // printed when the session said it; a second copy reads as a second failure.
        foreach (var e in r.Errors)
        {
            bool shown;
            lock (_shown) shown = _shown.Contains(e);
            if (!shown) Err($"cxagent: {e}");
        }
    }
}

/// <summary>One result object at the end.</summary>
internal sealed class JsonOutput(HeadlessIo io) : HeadlessOutput(io)
{
    protected override void OnStart(RunStart start) { }
    public override void Finish(RunResult r) => Out(ResultObject(r).ToJsonString());
}

/// <summary>One JSON object per line, as it happens.</summary>
internal sealed class StreamOutput(HeadlessIo io, bool partial) : HeadlessOutput(io)
{
    private readonly Dictionary<ChatMessageId, StringBuilder> _text = new();
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private string? _sessionId;

    private void Emit(JsonObject o) => Out(o.ToJsonString());

    protected override void OnStart(RunStart s)
    {
        _sessionId = s.SessionId;
        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = s.SessionId,
            ["cwd"] = s.Cwd,
            ["model"] = s.Model,
            ["tools"] = new JsonArray([.. s.Tools.Select(t => (JsonNode)t)]),
            ["mcp_servers"] = new JsonArray([.. s.McpServers.Select(m =>
                (JsonNode)new JsonObject { ["name"] = m.Name, ["status"] = m.Status })]),
            ["permissionMode"] = s.PermissionMode,
            ["plugins"] = new JsonArray([.. s.Plugins.Select(p =>
                (JsonNode)new JsonObject { ["name"] = p, ["path"] = "" })]),
            ["cxagent"] = new JsonObject
            {
                ["trusted"] = s.Trusted,
                ["skipped"] = SkippedObject(s.Skipped),
            },
        });
    }

    public override void AssistantTurnBegan(ChatMessageId id)
    {
        lock (_text) _text[id] = new StringBuilder();
    }

    public override void AssistantTextAppended(ChatMessageId id, string token)
    {
        lock (_text)
        {
            if (!_text.TryGetValue(id, out var sb)) _text[id] = sb = new StringBuilder();
            sb.Append(token);
        }
        if (partial)
            Emit(new JsonObject
            {
                ["type"] = "stream_event",
                ["session_id"] = _sessionId,
                ["parent_tool_use_id"] = null,
                ["event"] = new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = token },
                },
            });
    }

    public override void AssistantTurnEnded(ChatMessageId id)
    {
        string text;
        lock (_text)
        {
            if (!_text.Remove(id, out var sb)) return;
            text = sb.ToString();
        }
        if (text.Length == 0) return;
        Emit(Message("assistant", new JsonObject { ["type"] = "text", ["text"] = text }, null));
    }

    public override void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        foreach (var job in jobs) Announce(job);
    }

    public override void ToolUpdated(Job job)
    {
        Announce(job);
        if (!Finished(job)) return;
        Emit(Message("user", new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = job.Id,
            ["content"] = Rendered(job.Result),
            ["is_error"] = job.State != JobState.Succeeded || job.Result?.Success == false,
        }, ParentOf(job.AgentId)));
    }

    // ONCE PER CALL: a job is reported by several updates, and a consumer must see one tool_use.
    // THE CHECK AND THE LINE UNDER ONE LOCK: updates arrive on pool threads, and a second caller
    // finding the id already taken must not go on to write the tool_result before this tool_use.
    private void Announce(Job job)
    {
        lock (_announced)
        {
            if (!_announced.Add(job.Id)) return;
            Emit(Message("assistant", new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = job.Id,
                ["name"] = job.PlanLocalId ?? job.JobType,
                // THE CALL'S DESCRIPTION, not its raw arguments: the job carries the line a person
                // reads, and cxagent's tool arguments are not Claude Code's anyway.
                ["input"] = new JsonObject { ["summary"] = job.DisplayName },
            }, ParentOf(job.AgentId)));
        }
    }

    protected override void OnDenied(PermissionDecisionReport report, JsonObject denial) =>
        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "permission_denied",
            ["tool_name"] = denial["tool_name"]!.DeepClone(),
            ["tool_use_id"] = "",
            ["message"] = $"refused: {report.Subject}",
            ["session_id"] = _sessionId,
        });

    private JsonObject Message(string role, JsonObject block, string? parent) => new()
    {
        ["type"] = role,
        ["session_id"] = _sessionId,
        ["parent_tool_use_id"] = parent,
        ["message"] = new JsonObject { ["role"] = role, ["content"] = new JsonArray(block) },
    };

    public override void Finish(RunResult r) => Emit(ResultObject(r));
}
