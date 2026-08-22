using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The agent's tool calls dispatch through the SAME registry that wraps shell/file/http in
/// <see cref="PermissionGatedExecutor"/> before the caller ever sees them. ToolBindings does not
/// change; that is the property under test.
/// </summary>
public class PermissionGatedExecutorTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-perm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JobParameters Params(params (string k, object? v)[] kv) =>
        new(kv.ToDictionary(x => x.k, x => x.v));

    private static Job J(string id, string type, JobParameters p) => new()
    {
        Id = id,
        AgentId = "g",
        JobType = type,
        DisplayName = id,
        Parameters = p,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ToolCall ToolCall(string name, string jsonArgs) => new()
    {
        Name = name,
        Arguments = JsonSerializer.Deserialize<JsonElement>(jsonArgs),
        Id = "call-1",
    };

    [Fact]
    public async Task BuiltinToolPath_ADeniedWrite_ComesBackAsARefusalTheModelCanRead()
    {
        // Chokepoint 2 of 2 (ToolBindings.cs:147) — the path D7/D8/D11/D12 each forgot once.
        // Same registry, ZERO ToolBindings changes: the wrapper sits below both callers.
        var target = Path.Combine(TempDir(), "out.txt");
        var registry = JobRegistry.CreateWithBuiltins(null, PermissionGate.DenyAll);
        var call = ToolCall("write_file", $$"""{"path": "{{target}}", "content": "x"}""");

        var text = (await ToolBindings.InvokeAsync(call,
            new[] { BuiltinTool.WriteFile }, registry, new TestJobContext(), CancellationToken.None)).Text;

        Assert.False(File.Exists(target));
        Assert.Contains("denied by the user", text);     // routable refusal, not an opaque crash
        Assert.Contains("Do not retry", text);
    }

    [Fact]
    public async Task AnAllowedRequest_ExecutesExactlyAsBefore()
    {
        var file = Path.Combine(TempDir(), "in.txt");
        File.WriteAllText(file, "hello");
        var registry = JobRegistry.CreateWithBuiltins(null, PermissionGate.AllowAll);
        registry.TryGet("file", out var executor);

        var result = await executor!.ExecuteAsync(
            Params(("action", "read"), ("path", file)), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Output["content"]);
    }

    /// <summary>Returns one scripted outcome per call, in order — so a test can put an AutoAllow
    /// ahead of a plain Allow and prove which one the wrapper keeps.</summary>
    private sealed class ScriptedGate : IPermissionGate
    {
        private readonly Queue<PermissionOutcome> _outcomes;
        public ScriptedGate(params PermissionOutcome[] outcomes) => _outcomes = new(outcomes);
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct) =>
            Task.FromResult(_outcomes.Dequeue());
    }

    [Fact]
    public async Task AnEarlierAutoApproval_SurvivesALaterSilentAllow()
    {
        // Reported from a live drive: auto mode auto-approved a shell command, the decision was
        // recorded correctly in the history DB and /stats, but the tool row rendered plain
        // "· done · 0.0s" with no "auto-approved" badge. Root cause was `decidedBy =
        // outcome.DeniedBy` in the loop over RequestsFor: `copy` raises TWO requests (read the
        // source, then write the dest), and the dest request — cleared silently, no classifier
        // involved — is a plain Allow whose DeniedBy is null. That null overwrote the "auto" the
        // source request had just recorded, so the badge was gone by the time ExecuteAsync
        // returned, even though the decision itself was never wrong.
        var srcDir = TempDir();
        var src = Path.Combine(srcDir, "in.txt");
        File.WriteAllText(src, "hello");
        var dest = Path.Combine(srcDir, "out.txt");

        var gate = new ScriptedGate(PermissionOutcome.AutoAllow, PermissionOutcome.Allow);
        var registry = JobRegistry.CreateWithBuiltins(null, gate);
        registry.TryGet("file", out var executor);

        var result = await executor!.ExecuteAsync(
            Params(("action", "copy"), ("path", src), ("dest", dest)),
            new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("auto", result.DecidedBy);
    }

    [Fact]
    public void OnlyTheThreeRiskyExecutors_AreWrapped()
    {
        // llm_agent gated would double-prompt (its TOOLS are already gated one level down);
        // wait gated would be noise. The wrapper set is exactly shell|file|http.
        var registry = JobRegistry.CreateWithBuiltins(null, PermissionGate.DenyAll);
        foreach (var name in new[] { "shell", "file", "http" })
        {
            registry.TryGet(name, out var p);
            Assert.IsType<PermissionGatedExecutor>(p);
        }
        registry.TryGet("wait", out var wait);
        Assert.IsNotType<PermissionGatedExecutor>(wait);
    }

}
