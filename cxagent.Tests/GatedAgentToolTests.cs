using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The wrapper that makes an injected tool safe to offer at all.
///
/// <para>WHY THIS EXISTS AS ITS OWN TYPE: <see cref="PermissionGatedPlugin"/> cannot be reused. It
/// asks <c>PermissionPolicy.RequestsFor(TypeName, ...)</c>, which keys off the names "shell",
/// "file" and "http" — a consumer tool matches none of them, so RequestsFor returns nothing and the
/// call proceeds UNGATED AND SILENTLY. An injected tool without this wrapper is a hole, not a
/// missing feature, which is why it is built before anything can be injected through it.</para>
/// </summary>
public class GatedAgentToolTests
{
    /// <summary>Counts what it was asked, so a test can prove a gate ran rather than infer it.</summary>
    private sealed class RecordingTool : IAgentTool
    {
        private readonly bool _gatesEveryCall;
        public RecordingTool(bool gatesEveryCall = false) => _gatesEveryCall = gatesEveryCall;

        public int GateCalls { get; private set; }
        public int ExecuteCalls { get; private set; }

        public ToolDefinition Definition { get; } = new(
            "recorder", "records", JsonSerializer.SerializeToElement(new { type = "object" }));

        public PermissionRequest? Gate(JobParameters call)
        {
            GateCalls++;
            return _gatesEveryCall
                ? new PermissionRequest(PermissionKind.Tool, "recorder call", AlwaysRule: null)
                : null;
        }

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            ExecuteCalls++;
            return Task.FromResult(new JobResult { Success = true });
        }
    }

    /// <summary>Records every request it was handed, so a test can assert WHAT was asked.</summary>
    private sealed class RecordingGate : IPermissionGate
    {
        private readonly bool _allow;
        public RecordingGate(bool allow) => _allow = allow;
        public List<PermissionRequest> Seen { get; } = [];

        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(_allow);
        }
    }

    /// <summary>Says yes to everything, and counts how often it was asked.</summary>
    private sealed class CountingGate : IPermissionGate
    {
        private readonly bool _allow;
        public CountingGate(bool allow) => _allow = allow;
        public int Asked { get; private set; }

        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(_allow);
        }
    }

    private static JobParameters Call(string value) =>
        new(new Dictionary<string, object?> { ["arg"] = value });

    [Fact]
    public async Task TheToolItselfIsAdmittedOnceBeforeTheFirstCall()
    {
        // GATE 1: may this tool run in this folder at all. Without it an injected tool was silently
        // ungated on first use — seen on a live drive, where show_diff's own FileRead request was
        // answered silently by a trusted folder and no prompt appeared at any point.
        var gate = new RecordingGate(allow: true);
        var tool = new GatedAgentTool(new RecordingTool(gatesEveryCall: false), gate);

        await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);

        var admission = Assert.Single(gate.Seen);
        Assert.Equal(PermissionKind.Tool, admission.Kind);

        // Generalisable BY TOOL NAME, so "Always" stores one rule per folder per tool — which is
        // what makes asking every call cost the user nothing after they have said always once.
        Assert.Equal("tool recorder", admission.AlwaysRule);
    }

    [Fact]
    public async Task AllowOnceDoesNotBecomeAllowAlways()
    {
        // REPORTED FROM A DRIVE: the user pressed "Allow once" and the tool then ran on three more
        // files without asking again. The first version cached a bool after the first yes, which is
        // "remember" implemented on a signal that cannot tell once from always —
        // IPermissionGate.RequestAsync returns a BARE BOOL, and both answers return true.
        //
        // So the gate is asked every call. That is not a second prompt for the user: "Always" writes
        // a rule and PermissionPolicy.IsSilentlyAllowed matches it before the prompt is ever built,
        // which is remembering done in the layer that knows what was actually answered.
        var gate = new RecordingGate(allow: true);
        var tool = new GatedAgentTool(new RecordingTool(gatesEveryCall: false), gate);

        await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);
        await tool.ExecuteAsync(Call("b"), new TestJobContext(), CancellationToken.None);
        await tool.ExecuteAsync(Call("c"), new TestJobContext(), CancellationToken.None);

        Assert.Equal(3, gate.Seen.Count);
        Assert.All(gate.Seen, r => Assert.Equal(PermissionKind.Tool, r.Kind));
    }

    [Fact]
    public async Task ADeniedAdmissionAsksAgainNextTime()
    {
        // Per the spec: "if deny one, second time, asking again." A refusal is about this moment,
        // not a permanent verdict on the tool — latching it would mean one mis-typed no disabled a
        // tool for the rest of the session with no way back.
        var gate = new RecordingGate(allow: false);
        var inner = new RecordingTool(gatesEveryCall: false);
        var tool = new GatedAgentTool(inner, gate);

        await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);
        await tool.ExecuteAsync(Call("b"), new TestJobContext(), CancellationToken.None);

        Assert.Equal(2, gate.Seen.Count);
        Assert.Equal(0, inner.ExecuteCalls);
    }

    [Fact]
    public async Task TrustedToolStillRunsItsOwnGateOnEveryCall()
    {
        // THE POINT OF THE WHOLE DESIGN. "Always allow" answers gate 1 — may this tool run here.
        // It must not answer gate 2, which is the tool's own check on THIS call's arguments.
        var inner = new RecordingTool(gatesEveryCall: true);
        var tool = new GatedAgentTool(inner, new CountingGate(allow: true));

        await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);
        await tool.ExecuteAsync(Call("b"), new TestJobContext(), CancellationToken.None);

        Assert.Equal(2, inner.GateCalls);   // not 1, and not 0
        Assert.Equal(2, inner.ExecuteCalls);
    }

    [Fact]
    public async Task AnUngatedCallNeedsNoHumanAndStillRuns()
    {
        // Gate returning null means "no human needed", NOT "do not run".
        //
        // The gate is still asked — that is gate 1, admitting the tool to this folder, and it is a
        // different question from the one this tool declines to ask about its arguments. It is asked
        // per call because only the store may decide that an answer persists.
        var inner = new RecordingTool(gatesEveryCall: false);
        var gate = new CountingGate(allow: true);
        var tool = new GatedAgentTool(inner, gate);

        await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);
        var result = await tool.ExecuteAsync(Call("b"), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, inner.ExecuteCalls);
        Assert.Equal(2, gate.Asked);   // admission only — never the tool's own gate, which is null
    }

    [Fact]
    public async Task DeniedCallDoesNotReachTheInnerTool()
    {
        var inner = new RecordingTool(gatesEveryCall: true);
        var tool = new GatedAgentTool(inner, new CountingGate(allow: false));

        var result = await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, inner.ExecuteCalls);
    }

    [Fact]
    public async Task ARefusalIsFlaggedAsPermissionDeniedSoItIsNotDiagnosed()
    {
        // AgentHost.ShouldAutoDiagnose reads this flag to skip automatic diagnosis: a paid
        // diagnosis round cannot repair a user's decision. Setting only Success=false bills the
        // user for diagnosing their own "no".
        var tool = new GatedAgentTool(new RecordingTool(gatesEveryCall: true), new CountingGate(allow: false));

        var result = await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);   // the half that is easy to forget
    }

    [Fact]
    public async Task TheAskIsReportedSoTheRowReadsAsWaitingNotWorking()
    {
        // Same contract PermissionGatedPlugin holds: without this a parked job's row keeps ticking
        // elapsed time and reads as a working one, and with several up the user cannot tell which
        // row their answer releases.
        var context = new TestJobContext();
        var tool = new GatedAgentTool(new RecordingTool(gatesEveryCall: true), new CountingGate(allow: true));

        await tool.ExecuteAsync(Call("a"), context, CancellationToken.None);

        // Twice: once for gate 1 (admitting the tool) and once for gate 2 (this call's own check).
        Assert.Equal([true, false, true, false], context.PermissionWaits);
    }
}
