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
        var inner = new RecordingTool(gatesEveryCall: false);
        var gate = new CountingGate(allow: false);   // would refuse if it were ever asked
        var tool = new GatedAgentTool(inner, gate);

        var result = await tool.ExecuteAsync(Call("a"), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, inner.ExecuteCalls);
        Assert.Equal(0, gate.Asked);
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

        Assert.Equal([true, false], context.PermissionWaits);
    }
}
