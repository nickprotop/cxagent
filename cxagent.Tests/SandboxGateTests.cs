using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Headless;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The gate for a shell command the sandbox contains. Trust is the floor and the edit mode decides, exactly as
/// for a write inside the folder; leaving the sandbox, or running when it will not start, only the user answers.
/// </summary>
public sealed class SandboxGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbx-gate-").FullName;
    private readonly PermissionRulesStore _rules;
    private readonly List<string> _decisions = [];
    private int _prompts;

    public SandboxGateTests() => _rules = new PermissionRulesStore(new AppPaths(_dir));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private (PermissionDecider Gate, PermissionPolicy Policy, MockLlmProvider Judge) Gate(
        EditMode mode, TrustState trust = TrustState.Trusted, PermissionChoice answer = PermissionChoice.Deny)
    {
        _rules.SetTrust(_dir, trust);
        var policy = new PermissionPolicy(_dir, _rules, mode);
        var gate = PermissionDecider.ForTesting(policy, _rules, notice: null, (_, _, _) =>
        {
            _prompts++;
            return Task.FromResult(answer);
        });
        var judge = new MockLlmProvider();
        judge.EnqueueResponse(new LlmResponse { Text = "ALLOW" });
        gate.Classifier = new ActionClassifier(judge);
        gate.OnDecision = report => _decisions.Add(report.Decision);
        return (gate, policy, judge);
    }

    /// <summary>A command that writes, so the read-only free pass never applies unless a test wants it.</summary>
    private static PermissionRequest Contained(string command = "touch out.txt") =>
        new(PermissionKind.Shell, command, "touch*", Subject: command) { Contained = true };

    private static PermissionRequest Escape => new(PermissionKind.Shell, "git push", null, Subject: "git push")
    { UnsandboxedReason = "needs the ssh agent" };

    private static PermissionRequest FailedStart => new(PermissionKind.Shell, "make", null, Subject: "make")
    { SandboxFailure = "userns blocked" };

    private static async Task<bool> Allowed(PermissionDecider gate, PermissionRequest request) =>
        (await gate.RequestAsync(request, CancellationToken.None)).Allowed;

    [Fact]
    public async Task Untrusted_contained_asks()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits, TrustState.Untrusted);
        Assert.False(await Allowed(gate, Contained()));
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Unknown_trust_contained_asks()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits, TrustState.Unknown);
        await Allowed(gate, Contained());
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Always_ask_contained_asks()
    {
        var (gate, _, _) = Gate(EditMode.AlwaysAsk);
        await Allowed(gate, Contained());
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Always_ask_read_only_still_silent()
    {
        var (gate, _, _) = Gate(EditMode.AlwaysAsk);
        Assert.True(await Allowed(gate, Contained("ls")));
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Accept_edits_contained_is_silent()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        Assert.True(await Allowed(gate, Contained()));
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Accept_edits_uncontained_still_asks()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        await Allowed(gate, Contained() with { Contained = false });
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Auto_contained_is_silent_without_classifier()
    {
        var (gate, _, judge) = Gate(EditMode.Auto);
        Assert.True(await Allowed(gate, Contained()));
        Assert.Equal(0, _prompts);
        Assert.Equal(0, judge.ChatCallCount);
    }

    [Fact]
    public async Task Contained_silent_logs_sandbox()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        await Allowed(gate, Contained());
        Assert.Equal(["sandbox"], _decisions);
    }

    [Fact]
    public async Task Background_contained_follows_the_table()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        Assert.True(await Allowed(gate, Contained() with { Unattended = true }));
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Hook_ask_still_prompts_contained()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        await Allowed(gate, Contained() with { HookDecision = "ask" });
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Escape_prompts_even_with_an_allow_rule()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        _rules.Add(_dir, PermissionKind.Shell, "git push*");
        await Allowed(gate, Escape);
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Escape_prompts_under_auto()
    {
        var (gate, _, judge) = Gate(EditMode.Auto);
        await Allowed(gate, Escape);
        Assert.Equal(1, _prompts);
        Assert.Equal(0, judge.ChatCallCount);
    }

    [Fact]
    public async Task Escape_hook_allow_still_prompts()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        await Allowed(gate, Escape with { HookDecision = "allow", HookDecidedBy = "h" });
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Escape_runs_when_the_user_says_yes()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits, answer: PermissionChoice.Once);
        Assert.True(await Allowed(gate, Escape));
    }

    [Fact]
    public async Task Failed_start_prompts()
    {
        var (gate, _, _) = Gate(EditMode.AcceptEdits);
        await Allowed(gate, FailedStart);
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public void Failed_start_heading_has_the_copy() =>
        Assert.Equal("Sandbox could not start: userns blocked. Run this command uncontained?",
            PermissionPromptControl.HeadingFor(FailedStart));

    [Fact]
    public void Escape_heading_has_the_marked_line() =>
        Assert.Contains("wants to run outside the sandbox: needs the ssh agent", PermissionPromptControl.HeadingFor(Escape));

    [Fact]
    public async Task Headless_refuses_escape() =>
        Assert.Equal(PermissionChoice.Deny, await new HeadlessPrompt(bypass: false).Answer(Escape, false, CancellationToken.None));

    [Fact]
    public async Task Headless_bypass_runs_escape_with_a_visible_line()
    {
        var said = new List<string>();
        Assert.Equal(PermissionChoice.Once, await new HeadlessPrompt(bypass: true, said.Add).Answer(Escape, false, CancellationToken.None));
        Assert.Contains(said, s => s.Contains("outside the sandbox") && s.Contains("needs the ssh agent"));
    }

    [Fact]
    public async Task Headless_bypass_runs_failed_start_with_a_visible_line()
    {
        var said = new List<string>();
        Assert.Equal(PermissionChoice.Once, await new HeadlessPrompt(bypass: true, said.Add).Answer(FailedStart, false, CancellationToken.None));
        Assert.Contains(said, s => s.Contains("running uncontained: userns blocked"));
    }
}
