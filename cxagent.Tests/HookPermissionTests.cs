using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Headless;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a hook's answer to a tool call does at the permission gate: an allow answers a prompt and only a
/// prompt, an ask brings one back, and neither crosses the lines no mode crosses.
/// </summary>
public class HookPermissionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cxagent-hook-perm").FullName;
    private readonly string _cfg = Directory.CreateTempSubdirectory("cxagent-hook-perm-cfg").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_cfg, recursive: true);
    }

    private sealed class Prompt
    {
        public List<PermissionRequest> Shown { get; } = [];
        public PermissionChoice Answer { get; set; } = PermissionChoice.Deny;

        public Task<PermissionChoice> Show(PermissionRequest r, bool offerTrust, CancellationToken ct)
        {
            Shown.Add(r);
            return Task.FromResult(Answer);
        }
    }

    private (PermissionDecider Gate, Prompt Prompt) Make(EditMode mode = EditMode.AcceptEdits, bool trusted = true,
        Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>>? promptHook = null)
    {
        var store = new PermissionRulesStore(new AppPaths(_cfg));
        if (trusted) store.SetTrust(_root, TrustState.Trusted);
        var policy = new PermissionPolicy(_root, store, mode);
        var prompt = new Prompt();
        return (PermissionDecider.ForTesting(policy, store, null, promptHook ?? prompt.Show), prompt);
    }

    private string P(string relative) => Path.Combine(_root, relative);

    private static PermissionRequest Write(string path, string? hook = null) =>
        new(PermissionKind.FileWrite, path, path) { HookDecision = hook, HookDecidedBy = hook is null ? null : "guard" };

    private static PermissionRequest Shell(string command, string? hook = null) =>
        new(PermissionKind.Shell, command, command, command) { HookDecision = hook, HookDecidedBy = hook is null ? null : "guard" };

    [Fact]
    public async Task Hook_allow_skips_prompt()
    {
        var (gate, prompt) = Make();
        var outcome = await gate.RequestAsync(Shell("make deploy", "allow"), CancellationToken.None);
        Assert.True(outcome.Allowed);
        Assert.Equal("hook", outcome.DeniedBy);
        Assert.Empty(prompt.Shown);
    }

    [Fact]
    public async Task Without_a_hook_the_same_call_prompts()
    {
        var (gate, prompt) = Make();
        await gate.RequestAsync(Shell("make deploy"), CancellationToken.None);
        Assert.Single(prompt.Shown);
    }

    [Fact]
    public async Task Hook_allow_stops_at_protected_write()
    {
        var (gate, prompt) = Make();
        var outcome = await gate.RequestAsync(Write(P(".cxagent/hooks.json"), "allow"), CancellationToken.None);
        Assert.False(outcome.Allowed);
        var shown = Assert.Single(prompt.Shown);
        Assert.True(shown.HookAllowedProtected);
    }

    [Fact]
    public async Task Hook_allow_stops_at_child_boundary()
    {
        var (gate, prompt) = Make();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "x.txt");
        var request = Write(outside, "allow") with { Requester = "explorer" };
        await gate.RequestAsync(request, CancellationToken.None);
        Assert.Single(prompt.Shown);
    }

    [Fact]
    public async Task Hook_allow_outside_folder_for_main_agent_is_allowed()
    {
        var (gate, prompt) = Make();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "x.txt");
        Assert.True((await gate.RequestAsync(Write(outside, "allow"), CancellationToken.None)).Allowed);
        Assert.Empty(prompt.Shown);
    }

    [Fact]
    public async Task Hook_ask_prompts_even_when_silent()
    {
        var (gate, prompt) = Make(EditMode.AcceptEdits);
        // A write inside a trusted folder in accept-edits is silent — until a hook asks.
        Assert.True((await gate.RequestAsync(Write(P("a.txt")), CancellationToken.None)).Allowed);
        Assert.Empty(prompt.Shown);
        prompt.Answer = PermissionChoice.Once;
        Assert.True((await gate.RequestAsync(Write(P("a.txt"), "ask"), CancellationToken.None)).Allowed);
        Assert.Single(prompt.Shown);
    }

    [Fact]
    public async Task Hook_ask_in_headless_is_refused()
    {
        var headless = new HeadlessPrompt(bypass: false);
        var (gate, _) = Make(promptHook: headless.Answer);
        Assert.False((await gate.RequestAsync(Write(P("a.txt"), "ask"), CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task Hook_ask_under_bypass_is_approved()
    {
        var headless = new HeadlessPrompt(bypass: true);
        var (gate, _) = Make(promptHook: headless.Answer);
        Assert.True((await gate.RequestAsync(Write(P("a.txt"), "ask"), CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task Hook_allow_in_headless_runs_without_bypass()
    {
        var headless = new HeadlessPrompt(bypass: false);
        var (gate, _) = Make(promptHook: headless.Answer);
        Assert.True((await gate.RequestAsync(Shell("npm test", "allow"), CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task Hook_allow_does_not_exempt_tool_gate()
    {
        var (gate, prompt) = Make();
        var request = new PermissionRequest(PermissionKind.Tool, "use the deploy tool in this folder", "tool:deploy")
            { HookDecision = "allow", HookDecidedBy = "guard" };
        await gate.RequestAsync(request, CancellationToken.None);
        Assert.Single(prompt.Shown);
    }

    [Fact]
    public async Task Hook_allow_is_recorded_as_hook()
    {
        var (gate, _) = Make();
        var decisions = new List<string>();
        gate.OnDecision = d => decisions.Add(d.Decision);
        await gate.RequestAsync(Shell("make deploy", "allow"), CancellationToken.None);
        Assert.Equal(["hook"], decisions);
    }

    [Fact]
    public void Prompt_says_why_a_hook_allow_still_asks()
    {
        var heading = PermissionPromptControl.HeadingFor(Write(P(".git/hooks/pre-commit"), "allow") with { HookAllowedProtected = true });
        Assert.Contains("hook 'guard' allowed this", heading);
        Assert.Contains("protected", heading);
    }

    [Fact]
    public void Prompt_names_the_hook_that_asked()
    {
        Assert.Contains("hook 'guard' asked", PermissionPromptControl.HeadingFor(Shell("ls", "ask")));
    }
}
