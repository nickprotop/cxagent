using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Whether a shell call runs contained, asks to leave, or meets a sandbox that will not start — decided once, at
/// the gate, and handed to the executor so the two can never disagree about the same call.
/// </summary>
public sealed class ShellSandboxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbx-decide-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private PermissionPolicy Policy(SandboxStatus? status, SandboxSettings? settings = null)
    {
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        if (status is not null)
            policy.Sandbox = new SandboxState(settings ?? SandboxSettings.AppDefault, new SandboxHome(_dir, null, null), () => status);
        return policy;
    }

    private static JobParameters Call(params (string k, object? v)[] extra) =>
        new(new Dictionary<string, object?> { ["command"] = "ls" }.Concat(extra.Select(e => KeyValuePair.Create(e.k, e.v)))
            .ToDictionary(kv => kv.Key, kv => kv.Value));

    private static readonly SandboxStatus Failed = new(SandboxStatusKind.Failed, "userns blocked");
    private static readonly SandboxStatus Unavailable = new(SandboxStatusKind.Unavailable, "not installed");

    [Fact]
    public void No_policy_is_off() => Assert.Equal(SandboxUse.Off, ShellSandbox.Decide(null, Call()).Use);

    [Fact]
    public void No_sandbox_state_is_off() => Assert.Equal(SandboxUse.Off, ShellSandbox.Decide(Policy(null), Call()).Use);

    [Fact]
    public void Disabled_is_off() =>
        Assert.Equal(SandboxUse.Off, ShellSandbox.Decide(Policy(SandboxStatus.Active, SandboxSettings.Off), Call()).Use);

    [Fact]
    public void Unavailable_is_off() =>
        Assert.Equal(SandboxUse.Off, ShellSandbox.Decide(Policy(Unavailable), Call()).Use);

    [Fact]
    public void Unsandboxed_is_ignored_when_off() =>
        Assert.Equal(SandboxUse.Off, ShellSandbox.Decide(Policy(Unavailable), Call(("unsandboxed", true))).Use);

    [Fact]
    public void Active_is_contained() =>
        Assert.Equal(SandboxUse.Contained, ShellSandbox.Decide(Policy(SandboxStatus.Active), Call()).Use);

    [Fact]
    public void Failed_carries_its_reason()
    {
        var decision = ShellSandbox.Decide(Policy(Failed), Call());
        Assert.Equal(SandboxUse.Failed, decision.Use);
        Assert.Equal("userns blocked", decision.Reason);
    }

    [Fact]
    public void Escape_carries_the_calls_reason()
    {
        var decision = ShellSandbox.Decide(Policy(SandboxStatus.Active), Call(("unsandboxed", true), ("reason", "needs ~/.ssh to push")));
        Assert.Equal(SandboxUse.Escape, decision.Use);
        Assert.Equal("needs ~/.ssh to push", decision.Reason);
    }

    [Fact]
    public void Escape_without_a_reason_says_so() =>
        Assert.Equal("no reason given", ShellSandbox.Decide(Policy(SandboxStatus.Active), Call(("unsandboxed", true))).Reason);

    [Theory]
    [InlineData("true")]
    [InlineData(1)]
    public void Stringified_unsandboxed_reads_the_same(object value) =>
        Assert.Equal(SandboxUse.Escape, ShellSandbox.Decide(Policy(SandboxStatus.Active), Call(("unsandboxed", value))).Use);

    private static PermissionRequest Plain => new(PermissionKind.Shell, "ls", "ls*", Subject: "ls");

    [Fact]
    public void Contained_request_is_marked() =>
        Assert.True(ShellSandbox.Stamp(Plain, new SandboxDecision(SandboxUse.Contained, null)).Contained);

    [Fact]
    public void Escape_request_has_no_always_rule()
    {
        var r = ShellSandbox.Stamp(Plain, new SandboxDecision(SandboxUse.Escape, "why"));
        Assert.Null(r.AlwaysRule);
        Assert.Equal("why", r.UnsandboxedReason);
        Assert.False(r.Contained);
    }

    [Fact]
    public void Failed_request_has_no_always_rule()
    {
        var r = ShellSandbox.Stamp(Plain, new SandboxDecision(SandboxUse.Failed, "userns"));
        Assert.Null(r.AlwaysRule);
        Assert.Equal("userns", r.SandboxFailure);
    }

    [Fact]
    public void Off_request_is_unchanged() =>
        Assert.Same(Plain.AlwaysRule, ShellSandbox.Stamp(Plain, new SandboxDecision(SandboxUse.Off, null)).AlwaysRule);

    /// <summary>The executor sees the gate's decision, not a fresh one.</summary>
    [Fact]
    public async Task Gate_hands_its_decision_to_the_executor()
    {
        var gate = new RecordingGate();
        var inner = new RecordingShell();
        var policy = Policy(SandboxStatus.Active);
        var context = new JobContext("a", "j", new Dictionary<string, JobResult>(), null) { Policy = policy, WorkingDirectory = _dir };

        await new PermissionGatedExecutor(inner, gate, policy).ExecuteAsync(Call(), context, CancellationToken.None);

        Assert.True(Assert.Single(gate.Requests).Contained);
        Assert.Equal(SandboxUse.Contained, inner.Seen?.Use);
    }

    private sealed class RecordingGate : IPermissionGate
    {
        public List<PermissionRequest> Requests { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(PermissionOutcome.Allow);
        }
    }

    private sealed class RecordingShell : IJobExecutor
    {
        public SandboxDecision? Seen { get; private set; }
        public string TypeName => "shell";
        public string DisplayName => "shell";
        public JobSchema GetSchema() => new("shell", "shell", []);
        public JobValidation Validate(JobParameters parameters) => JobValidation.Valid();
        public Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
        {
            Seen = (context as JobContext)?.JudgedSandbox;
            return Task.FromResult(new JobResult { Success = true });
        }
    }
}
