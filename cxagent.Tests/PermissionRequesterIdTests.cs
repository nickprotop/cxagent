using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A gated call carries the asking agent's id, so a front end can show the prompt where that agent is.</summary>
public class PermissionRequesterIdTests
{
    private sealed class Capturing : IPermissionGate
    {
        public List<PermissionRequest> Seen { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(PermissionOutcome.Allow);
        }
    }

    [Fact]
    public async Task AGatedCall_CarriesTheAskingAgentsId()
    {
        var file = Path.Combine(Path.GetTempPath(), "requester-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "x");
        var gate = new Capturing();
        var registry = JobRegistry.CreateWithBuiltins(null, gate);
        registry.TryGet("file", out var executor);

        await executor!.ExecuteAsync(
            new JobParameters(new Dictionary<string, object?> { ["action"] = "read", ["path"] = file }),
            new JobContext("child-7", "job-1", new Dictionary<string, JobResult>(), null) { Requester = "find the loader" },
            CancellationToken.None);

        var asked = Assert.Single(gate.Seen);
        Assert.Equal("child-7", asked.RequesterAgentId);
        Assert.Equal("find the loader", asked.Requester);
        File.Delete(file);
    }

    private sealed class Denying : IPermissionGate
    {
        public List<PermissionRequest> Seen { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(PermissionOutcome.ByUser);
        }
    }

    /// <summary>
    /// THROUGH THE REAL GATED EXECUTOR: replace_in_file on a file asks, and a no leaves the file as it
    /// was. With no request raised, the gate's loop had nothing to ask and the edit simply ran.
    /// </summary>
    [Fact]
    public async Task AReplace_Asks_AndADenialLeavesTheFileUntouched()
    {
        var file = Path.Combine(Path.GetTempPath(), "replace-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "keep this");
        var gate = new Denying();
        JobRegistry.CreateWithBuiltins(null, gate).TryGet("file", out var executor);

        var result = await executor!.ExecuteAsync(
            new JobParameters(new Dictionary<string, object?>
                { ["action"] = "replace", ["path"] = file, ["pattern"] = "keep", ["replacement"] = "lost" }),
            new JobContext("agent", "job", new Dictionary<string, JobResult>(), null),
            CancellationToken.None);

        Assert.Equal(PermissionKind.FileWrite, Assert.Single(gate.Seen).Kind);
        Assert.False(result.Success);
        Assert.Equal("keep this", File.ReadAllText(file));
        File.Delete(file);
    }
}
