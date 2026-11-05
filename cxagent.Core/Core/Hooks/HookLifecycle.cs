using System.Text.Json.Nodes;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Hooks;

/// <summary>
/// The events around a turn rather than a tool call — a session opening and closing, a prompt going in,
/// an answer coming out, a permission prompt about to show — each with the input its hooks read.
/// </summary>
public static class HookLifecycle
{
    /// <summary>
    /// How many times in a row Stop hooks may send the agent back to work in one turn.
    ///
    /// <para>BOUNDED, because a Stop hook that blocks every time — a test that never passes, a check with a
    /// bug — would otherwise run the model, and the user's money, without end. Past this the turn ends and
    /// the user is told which hook kept it going.</para>
    /// </summary>
    public const int MaxStopBlocks = 3;

    /// <summary>All SessionEnd hooks together get this long, unless one sets a longer timeout.</summary>
    public static readonly TimeSpan EndFloor = TimeSpan.FromSeconds(1.5);

    /// <summary>And never longer than this: quitting must not appear to hang.</summary>
    public static readonly TimeSpan EndCeiling = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fires SessionStart. What its hooks add for the model is held on the runner and goes in front of the
    /// first prompt this session sends — the earliest point anything reaches the model.
    /// </summary>
    /// <param name="source"><c>startup</c>, <c>resume</c> or <c>clear</c>.</param>
    public static async Task SessionStartAsync(HookRunner runner, string source, CancellationToken ct)
    {
        if (!runner.Listens(HookEvents.SessionStart)) return;
        var verdict = await runner.RunAsync(HookEvents.SessionStart, source,
            HookPayload.For(runner.Session(), HookEvents.SessionStart, new JsonObject { ["source"] = source }), ct);
        runner.HoldStartContext(verdict.Context);
        runner.StartWaitsForTrust = !runner.ProjectTrusted && runner.Snapshot.Hooks.Any(IsProjectStart);
    }

    private static bool IsProjectStart(HookDefinition h) =>
        h.Event == HookEvents.SessionStart && h.Runs && h.Source is HookSource.Project or HookSource.Local;

    /// <summary>
    /// Runs the repository's SessionStart hooks that were skipped because the folder was not trusted yet, once
    /// it is — a folder trusted by the startup question is trusted after the start has already fired. Once.
    /// </summary>
    public static async Task CatchUpStartAsync(HookRunner runner, CancellationToken ct)
    {
        if (!runner.StartWaitsForTrust || !runner.ProjectTrusted) return;
        runner.StartWaitsForTrust = false;
        var verdict = await runner.RunAsync(HookEvents.SessionStart, "startup",
            HookPayload.For(runner.Session(), HookEvents.SessionStart, new JsonObject { ["source"] = "startup" }),
            IsProjectStart, ct);
        runner.HoldStartContext(verdict.Context);
    }

    /// <summary>The time all SessionEnd hooks share: the floor, raised by the longest timeout a hook sets, up to the ceiling.</summary>
    public static TimeSpan EndBudget(IEnumerable<HookDefinition> hooks)
    {
        var longest = hooks.Where(h => h.Event == HookEvents.SessionEnd && h.TimeoutSeconds is not null)
            .Select(h => TimeSpan.FromSeconds(h.TimeoutSeconds!.Value))
            .DefaultIfEmpty(TimeSpan.Zero).Max();
        return longest > EndCeiling ? EndCeiling : longest > EndFloor ? longest : EndFloor;
    }

    /// <summary>
    /// Fires SessionEnd and waits at most <see cref="EndBudget"/>. Hooks still running then are cancelled,
    /// which kills their processes; the session closes regardless.
    /// </summary>
    /// <param name="reason"><c>exit</c>, <c>clear</c> or <c>other</c>.</param>
    public static async Task SessionEndAsync(HookRunner runner, string reason)
    {
        if (!runner.Listens(HookEvents.SessionEnd)) return;
        using var budget = new CancellationTokenSource(EndBudget(runner.Snapshot.Hooks));
        try
        {
            await runner.RunAsync(HookEvents.SessionEnd, reason,
                HookPayload.For(runner.Session(), HookEvents.SessionEnd, new JsonObject { ["reason"] = reason }), budget.Token);
        }
        catch (OperationCanceledException)
        {
            // NAMED, NOT DROPPED: a SessionEnd hook that did not finish may have left its work half done.
            var names = runner.Snapshot.Hooks.Where(h => h.Event == HookEvents.SessionEnd && h.Runs).Select(h => h.DisplayName);
            runner.Report(new($"SessionEnd hook(s) cut off after {EndBudget(runner.Snapshot.Hooks).TotalSeconds:0.#} s: "
                              + string.Join(", ", names), IsError: true));
        }
    }

    /// <summary>Fires UserPromptSubmit for a prompt the user (or a plugin on their behalf) sent.</summary>
    public static Task<HookVerdict> PromptAsync(HookRunner runner, HookEventContext ctx, string prompt, CancellationToken ct) =>
        runner.RunAsync(HookEvents.UserPromptSubmit, null,
            HookPayload.For(ctx, HookEvents.UserPromptSubmit, new JsonObject { ["prompt"] = prompt }), ct);

    /// <summary>Fires Stop as the agent is about to hand its answer back.</summary>
    public static Task<HookVerdict> StopAsync(HookRunner runner, HookStop stop, CancellationToken ct)
    {
        var fields = new JsonObject
        {
            ["stop_hook_active"] = stop.Active,
            ["last_assistant_message"] = stop.LastMessage,
        };
        if (stop.Event == HookEvents.SubagentStop)
        {
            fields["agent_id"] = stop.Context.AgentId;
            fields["agent_type"] = stop.AgentType;
            fields["agent_transcript_path"] = stop.Context.TranscriptPath;
        }
        // A SUB-AGENT'S STOP MATCHES ON ITS TYPE; the session's Stop takes no matcher.
        return runner.RunAsync(stop.Event, stop.Event == HookEvents.SubagentStop ? stop.AgentType : null,
            HookPayload.For(stop.Context, stop.Event, fields), ct);
    }

    /// <summary>Fires SubagentStart as a sub-agent takes its first task; its context goes to the sub-agent.</summary>
    public static Task<HookVerdict> SubagentStartAsync(HookRunner runner, HookEventContext ctx, string agentType, CancellationToken ct) =>
        runner.RunAsync(HookEvents.SubagentStart, agentType,
            HookPayload.For(ctx, HookEvents.SubagentStart, new JsonObject { ["agent_id"] = ctx.AgentId, ["agent_type"] = agentType }), ct);

    /// <summary>
    /// Fires MessageDisplay for a reply as it is shown, without waiting. The reply is shown once it is whole, so
    /// this fires once per reply with <c>final</c> true and the whole text as <c>delta</c>.
    /// </summary>
    public static void MessageDisplayed(HookRunner runner, HookEventContext ctx, string text)
    {
        if (!runner.Listens(HookEvents.MessageDisplay)) return;
        _ = Task.Run(() => runner.RunAsync(HookEvents.MessageDisplay, null, HookPayload.For(ctx, HookEvents.MessageDisplay,
            new JsonObject { ["turn_id"] = ctx.SessionId, ["message_id"] = Guid.NewGuid().ToString("N"), ["index"] = 0, ["final"] = true, ["delta"] = text }),
            CancellationToken.None));
    }

    /// <summary>Fires PostToolBatch once every call from one model reply has its result.</summary>
    public static Task<HookVerdict> PostToolBatchAsync(HookRunner runner, HookEventContext ctx,
        IReadOnlyList<(Models.ToolCall Call, string Result)> calls, CancellationToken ct) =>
        runner.RunAsync(HookEvents.PostToolBatch, null, HookPayload.For(ctx, HookEvents.PostToolBatch, new JsonObject
        {
            ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
            {
                ["tool_name"] = c.Call.Name,
                ["tool_input"] = HookNames.WithAliases(c.Call.Name, HookPayload.InputOf(c.Call)),
                ["tool_use_id"] = c.Call.Id,
                ["tool_response"] = new JsonObject { ["output"] = c.Result },
            })]),
        }), ct);

    /// <summary>
    /// Fires TaskCreated for each item a to-do write adds and TaskCompleted for each it closes. The first refusal
    /// is the answer: the write is refused whole, so the list stays as the model last had it.
    /// </summary>
    public static async Task<HookVerdict?> TasksAsync(HookRunner runner, HookEventContext ctx, HookTasks tasks, CancellationToken ct)
    {
        var before = tasks.Before.ToDictionary(i => i.Text, i => i.Status, StringComparer.Ordinal);
        foreach (var item in tasks.After)
        {
            var ev = !before.TryGetValue(item.Text, out var was) ? HookEvents.TaskCreated
                : item.Status == Agents.TodoStatus.Completed && was != Agents.TodoStatus.Completed ? HookEvents.TaskCompleted
                : null;
            if (ev is null || !runner.Listens(ev)) continue;
            var verdict = await runner.RunAsync(ev, null, HookPayload.For(ctx, ev, new JsonObject
            {
                ["task_id"] = TaskId(item.Text),
                ["task_subject"] = item.Text,
            }), ct);
            if (verdict.Stops) return verdict;
        }
        return null;
    }

    /// <summary>A to-do item's id: stable for its text, so a hook sees the same id when it is created and when it is closed.</summary>
    private static string TaskId(string text) =>
        "task-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    /// <summary>Fires StopFailure without waiting: the turn has already failed, and nothing a hook says changes that.</summary>
    public static void StopFailure(HookRunner runner, HookEventContext ctx, Exception error)
    {
        if (!runner.Listens(HookEvents.StopFailure)) return;
        _ = Task.Run(() => runner.RunAsync(HookEvents.StopFailure, "unknown",
            HookPayload.For(ctx, HookEvents.StopFailure, new JsonObject { ["error"] = error.Message, ["error_type"] = "unknown" }),
            CancellationToken.None));
    }

    /// <summary>The tool a permission request is about, by the name a matcher uses, and its input.</summary>
    internal static HookTool ToolOf(PermissionRequest request) => request.Kind switch
    {
        PermissionKind.Shell => new("run_shell", new JsonObject { ["command"] = request.What }),
        PermissionKind.FileRead => new("read_file", new JsonObject { ["path"] = request.What }),
        PermissionKind.FileWrite => new("write_file", new JsonObject { ["path"] = request.What }),
        PermissionKind.Http => new("http_request", new JsonObject { ["url"] = request.What }),
        PermissionKind.Mcp => new(request.AlwaysRule is { } r && r.StartsWith("mcp:", StringComparison.Ordinal) ? r[4..] : "mcp",
            new JsonObject { ["call"] = request.Display }),
        _ => new(request.Kind.ToString().ToLowerInvariant(), new JsonObject { ["request"] = request.Display }),
    };

    /// <summary>
    /// Fires PermissionRequest as a prompt is about to show. A hook's <c>allow</c> or <c>deny</c> answers the
    /// prompt instead of the user; null means the prompt shows.
    /// </summary>
    public static async Task<HookVerdict?> PermissionRequestAsync(HookRunner runner, PermissionRequest request, CancellationToken ct)
    {
        if (!runner.Listens(HookEvents.PermissionRequest)) return null;
        var (tool, input) = ToolOf(request);
        var verdict = await runner.RunAsync(HookEvents.PermissionRequest, tool,
            HookPayload.For(ContextFor(runner, request), HookEvents.PermissionRequest,
                new JsonObject { ["tool_name"] = tool, ["tool_input"] = HookNames.WithAliases(tool, input) }), ct);
        return verdict.Decision is "allow" or "deny" ? verdict : null;
    }

    /// <summary>Fires Notification for a waiting prompt, without waiting for its hooks.</summary>
    public static void PromptWaiting(HookRunner runner, PermissionRequest request)
    {
        if (!runner.Listens(HookEvents.Notification)) return;
        var who = request.Requester is { Length: > 0 } r ? $"{r} needs" : "cxagent needs";
        _ = Task.Run(() => runner.RunAsync(HookEvents.Notification, "permission_prompt",
            HookPayload.For(ContextFor(runner, request), HookEvents.Notification, new JsonObject
            {
                ["message"] = $"{who} your permission: {request.Display}",
                ["notification_type"] = "permission_prompt",
            }), CancellationToken.None));
    }

    private static HookEventContext ContextFor(HookRunner runner, PermissionRequest request) =>
        request.Requester is { Length: > 0 } label
            ? runner.Session() with { Agent = $"{label} {request.RequesterAgentId}".Trim(), AgentId = request.RequesterAgentId, AgentType = label }
            : runner.Session();

    // --- the session's own events ----------------------------------------------------------------------

    /// <summary>Fires an event without waiting — for the ones nothing can block and nobody waits on.</summary>
    private static void Notify(HookRunner runner, string ev, string? matchOn, JsonObject fields)
    {
        if (!runner.Listens(ev)) return;
        _ = Task.Run(async () =>
        {
            try { await runner.RunAsync(ev, matchOn, HookPayload.For(runner.Session(), ev, fields), CancellationToken.None); }
            catch (Exception ex) { runner.Report(new($"{ev} hook failed: {ex.Message}", IsError: true)); }
        });
    }

    /// <summary>Fires CwdChanged when the session's folder moves (a resume of a conversation from another folder).</summary>
    public static void CwdChanged(HookRunner runner, string oldCwd, string newCwd) =>
        Notify(runner, HookEvents.CwdChanged, null, new JsonObject { ["old_cwd"] = oldCwd, ["new_cwd"] = newCwd });

    /// <summary>Fires InstructionsLoaded for an instruction file the session read. Observability only.</summary>
    /// <param name="memoryType"><c>Project</c> for a repository's file, <c>User</c> for the one in the config folder.</param>
    public static void InstructionsLoaded(HookRunner runner, string file, string memoryType) =>
        Notify(runner, HookEvents.InstructionsLoaded, "session_start", new JsonObject
        {
            ["file_path"] = file,
            ["memory_type"] = memoryType,
            ["load_reason"] = "session_start",
        });

    /// <summary>Fires PostCompact after a compaction, with the summary it made. Observability only.</summary>
    public static void PostCompact(HookRunner runner, string trigger, string summary) =>
        Notify(runner, HookEvents.PostCompact, trigger, new JsonObject { ["trigger"] = trigger, ["compact_summary"] = summary });

    /// <summary>Fires PreCompact; a block skips the compaction.</summary>
    /// <param name="trigger"><c>manual</c> for <c>/compress</c>, <c>auto</c> otherwise.</param>
    public static Task<HookVerdict> PreCompactAsync(HookRunner runner, string trigger, CancellationToken ct) =>
        runner.RunAsync(HookEvents.PreCompact, trigger, HookPayload.For(runner.Session(), HookEvents.PreCompact,
            new JsonObject { ["trigger"] = trigger, ["custom_instructions"] = null }), ct);

    /// <summary>Fires UserPromptExpansion for a <c>/command</c> or skill about to become a prompt; a block keeps it from the model.</summary>
    public static Task<HookVerdict> ExpansionAsync(HookRunner runner, HookExpansion expansion, CancellationToken ct) =>
        runner.RunAsync(HookEvents.UserPromptExpansion, expansion.Name, HookPayload.For(runner.Session(), HookEvents.UserPromptExpansion,
            new JsonObject
            {
                ["expansion_type"] = "slash_command",
                ["command_name"] = expansion.Name,
                ["command_args"] = expansion.Arguments,
                ["command_source"] = expansion.Source,
                ["prompt"] = expansion.Prompt,
            }), ct);

    /// <summary>Fires PreModelSwitch or PostModelSwitch. Run off the caller's thread and bounded: a switch is a command a person typed.</summary>
    public static HookVerdict ModelSwitch(HookRunner runner, string ev, HookModelSwitch change)
    {
        if (!runner.Listens(ev)) return HookVerdict.None;
        var input = HookPayload.For(runner.Session(), ev, new JsonObject
        {
            ["from_model"] = change.From,
            ["to_model"] = change.To,
            ["requested_model"] = change.Requested,
            ["source"] = "command",
            ["context_tokens"] = change.ContextTokens,
        });
        return RunBounded(runner, ev, change.To, input);
    }

    /// <summary>Fires ConfigChange before a saved config reaches a session; a block keeps the session on what it had.</summary>
    public static HookVerdict ConfigChange(HookRunner runner, string source, string? file) =>
        runner.Listens(HookEvents.ConfigChange)
            ? RunBounded(runner, HookEvents.ConfigChange, source,
                HookPayload.For(runner.Session(), HookEvents.ConfigChange, new JsonObject { ["source"] = source, ["file_path"] = file }))
            : HookVerdict.None;

    /// <summary>
    /// Runs an event from a synchronous caller: on the thread pool, waited for at most the slowest matching hook's
    /// deadline. A hook that does not answer in time counts as a block, as the contract has it for a switch.
    /// </summary>
    private static HookVerdict RunBounded(HookRunner runner, string ev, string? matchOn, JsonObject input)
    {
        var budget = runner.Snapshot.Hooks.Where(h => h.Event == ev && h.Runs)
            .Select(HookDispatch.TimeoutSeconds).DefaultIfEmpty(30).Max();
        var task = Task.Run(() => runner.RunAsync(ev, matchOn, input, CancellationToken.None));
        try
        {
            return task.Wait(TimeSpan.FromSeconds(budget + 1))
                ? task.Result
                : new HookVerdict("block", "its hooks did not answer in time", null, [], ev);
        }
        catch (AggregateException ex)
        {
            return new HookVerdict("block", ex.InnerException?.Message ?? ex.Message, null, [], ev);
        }
    }

    /// <summary>Fires PermissionDenied after auto mode's reviewer refused a call; a hook's <c>retry</c> tells the model it may try again.</summary>
    public static async Task<HookVerdict?> PermissionDeniedAsync(HookRunner runner, PermissionRequest request, string? reason, CancellationToken ct)
    {
        if (!runner.Listens(HookEvents.PermissionDenied)) return null;
        var tool = ToolOf(request);
        return await runner.RunAsync(HookEvents.PermissionDenied, tool.Name, HookPayload.For(runner.Session(), HookEvents.PermissionDenied,
            new JsonObject { ["tool_name"] = tool.Name, ["tool_input"] = HookNames.WithAliases(tool.Name, tool.Input), ["reason"] = reason }), ct);
    }
}

/// <summary>What a Stop hook is told: who is stopping, what they last said, and whether a Stop hook already sent them back.</summary>
/// <param name="Active">Whether this turn already went on because a Stop hook said so — the contract's guard against loops.</param>
public sealed record HookStop(HookEventContext Context, string? LastMessage, bool Active)
{
    /// <summary><c>Stop</c> for the session's agent, <c>SubagentStop</c> for a sub-agent.</summary>
    public string Event { get; init; } = HookEvents.Stop;

    /// <summary>A sub-agent's type, which SubagentStop matches on.</summary>
    public string? AgentType { get; init; }
}

/// <summary>A to-do write: the list before it, and the list it would leave.</summary>
public sealed record HookTasks(IReadOnlyList<Agents.TodoItem> Before, IReadOnlyList<Agents.TodoItem> After);

/// <summary>A tool by the name a hook's matcher uses, and its arguments as the hook reads them.</summary>
internal sealed record HookTool(string Name, JsonObject Input);

/// <summary>A <c>/command</c> or skill about to become a prompt.</summary>
/// <param name="Source"><c>project</c> or <c>user</c>: where its file came from.</param>
public sealed record HookExpansion(string Name, string Arguments, string Source, string Prompt);

/// <summary>A model switch: from which model to which, what was asked for, and how much context it re-sends.</summary>
public sealed record HookModelSwitch(string From, string To, string? Requested, int ContextTokens);
