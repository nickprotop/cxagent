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
    /// <param name="active">Whether this turn already went on because a Stop hook said so — the contract's guard against loops.</param>
    public static Task<HookVerdict> StopAsync(HookRunner runner, HookEventContext ctx, string? lastMessage, bool active, CancellationToken ct) =>
        runner.RunAsync(HookEvents.Stop, null,
            HookPayload.For(ctx, HookEvents.Stop, new JsonObject
            {
                ["stop_hook_active"] = active,
                ["last_assistant_message"] = lastMessage,
            }), ct);

    /// <summary>Fires StopFailure without waiting: the turn has already failed, and nothing a hook says changes that.</summary>
    public static void StopFailure(HookRunner runner, HookEventContext ctx, Exception error)
    {
        if (!runner.Listens(HookEvents.StopFailure)) return;
        _ = Task.Run(() => runner.RunAsync(HookEvents.StopFailure, "unknown",
            HookPayload.For(ctx, HookEvents.StopFailure, new JsonObject { ["error"] = error.Message, ["error_type"] = "unknown" }),
            CancellationToken.None));
    }

    /// <summary>The tool a permission request is about, by the name a matcher uses, and its input.</summary>
    internal static (string Tool, JsonObject Input) ToolOf(PermissionRequest request) => request.Kind switch
    {
        PermissionKind.Shell => ("run_shell", new JsonObject { ["command"] = request.What }),
        PermissionKind.FileRead => ("read_file", new JsonObject { ["path"] = request.What }),
        PermissionKind.FileWrite => ("write_file", new JsonObject { ["path"] = request.What }),
        PermissionKind.Http => ("http_request", new JsonObject { ["url"] = request.What }),
        PermissionKind.Mcp => (request.AlwaysRule is { } r && r.StartsWith("mcp:", StringComparison.Ordinal) ? r[4..] : "mcp",
            new JsonObject { ["call"] = request.Display }),
        _ => (request.Kind.ToString().ToLowerInvariant(), new JsonObject { ["request"] = request.Display }),
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
}
