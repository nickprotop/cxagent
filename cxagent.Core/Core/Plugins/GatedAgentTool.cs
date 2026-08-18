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

    /// <summary>
    /// Whether GATE 1 has been answered for this session — may this tool run in this folder at all.
    ///
    /// <para>ASKED ONCE, ON FIRST USE. Not once per call: that is gate 2's job. A denial does NOT
    /// latch, per the spec — "if deny one, second time, asking again" — because a refusal is about
    /// this moment, not a permanent verdict on the tool.</para>
    /// </summary>
    private bool _allowedHere;

    public async Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
    {
        // GATE 1: MAY THIS TOOL RUN HERE AT ALL. A PermissionKind.Tool question, distinct from
        // anything the tool itself asks — "may show_diff run in /tmp/cxdiff" is not a file question,
        // and answering "always" to it grants the TOOL, never a bypass of its own checks below.
        //
        // WITHOUT THIS the tool was silently ungated on first use: show_diff's own gate returns a
        // FileRead request, and a trusted folder answers that one silently, so a live drive ran an
        // injected tool with no prompt at any point. Reported from that drive.
        if (!_allowedHere)
        {
            var admission = new PermissionRequest(
                PermissionKind.Tool,
                $"use the {_inner.Definition.Name} tool in this folder",

                // GENERALISABLE BY TOOL NAME, so "always" is answered once per folder per tool and
                // means what it says. It does not name the arguments, because it is not about them.
                AlwaysRule: $"tool {_inner.Definition.Name}");

            context.ReportPermissionWait(true);
            bool admitted;
            try
            {
                admitted = await _gate.RequestAsync(
                    admission with { Requester = context.Requester, Policy = _policy }, ct);
            }
            finally
            {
                context.ReportPermissionWait(false);
            }

            if (!admitted)
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,
                    PermissionDenied = true,
                    ErrorMessage = $"permission denied by the user: {admission.Display}. "
                        + "Do not retry this operation or plan it again unless the user explicitly asks.",
                };

            // ONLY ON A YES. A denial leaves this false so the next call asks again.
            _allowedHere = true;
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
            bool allowed;
            try
            {
                allowed = await _gate.RequestAsync(
                    request with { Requester = context.Requester, Policy = _policy }, ct);
            }
            finally
            {
                context.ReportPermissionWait(false);
            }

            if (!allowed)
            {
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,

                    // NOT MERELY Success=false. AgentHost.ShouldAutoDiagnose reads this to skip
                    // automatic diagnosis — a paid diagnosis round cannot repair a user's decision,
                    // and without the flag every refusal bills the user for diagnosing their own no.
                    PermissionDenied = true,
                    ErrorMessage = $"permission denied by the user: {request.Display}. "
                        + "Do not retry this operation or plan it again unless the user explicitly asks.",
                };
            }
        }

        // EVERY GATE HAS CLEARED — the work starts now. Before this line the elapsed time is the
        // user reading a prompt, which belongs to nobody's stopwatch.
        context.WorkStarting();

        return await _inner.ExecuteAsync(call, context, ct);
    }
}
