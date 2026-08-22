using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Wraps a consumer's <see cref="IAgentTool"/> so its calls reach the permission gate.
///
/// <para>WHY NOT <see cref="PermissionGatedPlugin"/>. That type asks
/// <c>PermissionPolicy.RequestsFor(TypeName, ...)</c>, which knows the names "shell", "file" and
/// "http". A consumer tool matches none of them, so RequestsFor returns an empty list, the foreach
/// never runs, and the call proceeds UNGATED — silently, with every layer behaving as written. An
/// injected tool without a wrapper of its own is a hole rather than a missing feature, which is why
/// this is built before anything can be injected through it.</para>
///
/// <para>THE ASYMMETRY WITH ITS SIBLING: PermissionGatedPlugin derives its requests from a POLICY
/// that reads the arguments of tools it knows. Nothing here knows what a consumer's arguments mean,
/// so the tool declares its own requirement through <see cref="IAgentTool.Gate"/> and this only
/// carries the answer to the gate. That is the whole reason Gate exists on the interface.</para>
/// </summary>
public sealed class GatedAgentTool : IAgentTool
{
    private readonly IAgentTool _inner;
    private readonly IPermissionGate _gate;

    /// <summary>This session's policy, stamped onto the request so one process-wide gate can judge
    /// each session by its own root and edit mode. Null where there is no policy — the fixed test
    /// gates — and the gate's own is then the only one there is.</summary>
    private readonly PermissionPolicy? _policy;

    public GatedAgentTool(IAgentTool inner, IPermissionGate gate, PermissionPolicy? policy = null)
    {
        _inner = inner;
        _gate = gate;
        _policy = policy;
    }

    public ToolDefinition Definition => _inner.Definition;

    /// <summary>Delegates, so wrapping is invisible: a wrapped tool answers the same question the
    /// bare one would. Nothing consults this on the execute path — <see cref="ExecuteAsync"/> calls
    /// the inner Gate directly — but a caller that inspects a tool must not get a different answer
    /// depending on whether it happened to be handed the wrapper.</summary>
    public PermissionRequest? Gate(JobParameters call) => _inner.Gate(call);

    public async Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
    {
        // GATE 1: MAY THIS TOOL RUN HERE AT ALL. A PermissionKind.Tool question, distinct from
        // anything the tool itself asks — "may show_diff run in /tmp/cxdiff" is not a file question,
        // and answering "always" to it grants the TOOL, never a bypass of its own checks below.
        //
        // WITHOUT THIS the tool was silently ungated on first use: show_diff's own gate returns a
        // FileRead request, and a trusted folder answers that one silently, so a live drive ran an
        // injected tool with no prompt at any point. Reported from that drive.
        //
        // ASKED ON EVERY CALL, AND NO LOCAL "ALREADY ALLOWED" FLAG. The first version cached a bool
        // after the first yes, which turned "Allow once" into "always, for this session" — reported
        // from a drive where one `once` covered four files silently. IPermissionGate.RequestAsync
        // returns a BARE BOOL, so once and always are indistinguishable here, and remembering on
        // that signal can only ever remember too much.
        //
        // REMEMBERING IS THE STORE'S JOB, and it already does it: `Always` writes a rule and
        // PermissionPolicy.IsSilentlyAllowed matches it on the next call, per folder and per kind,
        // so a granted tool never reaches the prompt again. Asking every time is therefore not a
        // second prompt for the user — it is the one question, routed through the one layer that
        // can tell what they actually answered.
        // CARRIES FORWARD to whichever JobResult this call eventually returns (Task 8), so the tool
        // row can badge "auto-approved" / "auto-denied" — the two gates below both write it, and
        // the last one that ran wins, which is right: the classifier's most recent word on this
        // call is the one worth showing.
        string? decidedBy = null;

        {
            var admission = new PermissionRequest(
                PermissionKind.Tool,
                $"use the {_inner.Definition.Name} tool in this folder",

                // GENERALISABLE BY TOOL NAME, so "always" is answered once per folder per tool and
                // means what it says. It does not name the arguments, because it is not about them.
                AlwaysRule: $"tool {_inner.Definition.Name}");

            context.ReportPermissionWait(true);
            PermissionOutcome outcome;
            try
            {
                outcome = await _gate.RequestAsync(
                    admission with { Requester = context.Requester, Policy = _policy }, ct);
            }
            finally
            {
                context.ReportPermissionWait(false);
            }

            decidedBy = outcome.DeniedBy;

            if (!outcome.Allowed)
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,
                    PermissionDenied = true,
                    ErrorMessage = DenialMessage.For(outcome, admission.Display),
                    DecidedBy = decidedBy,
                };
        }

        // GATE 2, ON EVERY CALL. Gate 1 — may this tool run in this folder — is answered by the
        // stored Tool rule the gate consults below, once per folder. This one is the tool's own
        // check on THIS call's arguments and has no memory by design: being trusted is permission
        // to USE the tool, never an exemption from what the tool itself insists on. Caching this
        // answer after the first allow is the bug the design exists to prevent.
        var request = _inner.Gate(call);

        if (request is not null)
        {
            // MARKED WAITING FOR THE DURATION OF THE ASK, as PermissionGatedPlugin does: the row
            // above this job keeps ticking elapsed time while a prompt sits unanswered, so without
            // this a parked job reads as a working one. In a finally because a request cancelled
            // while queued never returns here.
            context.ReportPermissionWait(true);
            PermissionOutcome outcome;
            try
            {
                outcome = await _gate.RequestAsync(
                    request with { Requester = context.Requester, Policy = _policy }, ct);
            }
            finally
            {
                context.ReportPermissionWait(false);
            }

            decidedBy = outcome.DeniedBy;

            if (!outcome.Allowed)
            {
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,

                    // NOT MERELY Success=false. AgentHost.ShouldAutoDiagnose reads this to skip
                    // automatic diagnosis — a paid diagnosis round cannot repair a user's decision,
                    // and without the flag every refusal bills the user for diagnosing their own no.
                    PermissionDenied = true,
                    ErrorMessage = DenialMessage.For(outcome, request.Display),
                    DecidedBy = decidedBy,
                };
            }
        }

        // EVERY GATE HAS CLEARED — the work starts now. Before this line the elapsed time is the
        // user reading a prompt, which belongs to nobody's stopwatch.
        context.WorkStarting();

        var result = await _inner.ExecuteAsync(call, context, ct);

        // STAMPED ON THE WAY OUT, not threaded into the inner call — the inner tool has no idea a
        // gate exists and must not need to. Only overwrites when a gate actually ran (decidedBy is
        // null on the ordinary path, where nothing should change), and never overwrites a DecidedBy
        // the inner tool itself set — none does today, but a future one composing its own gated
        // calls should not have this silently clobber a decision it recorded.
        return decidedBy is null || result.DecidedBy is not null ? result : result with { DecidedBy = decidedBy };
    }
}
