using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Hooks for an app that embeds the agent: the same JSON a hooks file holds, and typed handlers in code,
/// both reaching the session through <see cref="AgentConfig.Hooks"/>.
/// </summary>
public class HooksConfigTests
{
    private static AgentConfig With(HooksConfig hooks) => new()
    {
        Models = { ["local"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://localhost:1/v1" } },
        DefaultModel = "local",
        Hooks = hooks,
    };

    private static HookRunner RunnerFor(ResolvedConfig resolved) =>
        new(new HookRunnerDeps(HookSources.Read(resolved.Hooks, Path.GetTempPath()),
            resolved.Hooks.Handlers ?? new HookHandlers(), () => false, _ => { }) { ProjectDir = Path.GetTempPath() });

    private static JsonObject Shell(string command) =>
        new() { ["tool_name"] = "run_shell", ["tool_input"] = new JsonObject { ["command"] = command } };

    [Fact]
    public void AgentConfig_json_hooks_reach_the_session_as_library_hooks()
    {
        var resolved = With(new HooksConfig
        {
            Json = """{ "PreToolUse": [ { "matcher": "run_shell", "hooks": [ { "type": "command", "command": "exit 2" } ] } ] }""",
        }).Resolve();
        Assert.Empty(resolved.Errors);
        var hook = Assert.Single(HookSources.Read(resolved.Hooks, Path.GetTempPath()).Hooks);
        Assert.Equal(HookSource.Library, hook.Source);
    }

    [Fact]
    public async Task AgentConfig_json_hooks_fire_even_untrusted()
    {
        var resolved = With(new HooksConfig
        {
            Json = """{ "PreToolUse": [ { "hooks": [ { "type": "command", "command": "echo no >&2; exit 2" } ] } ] }""",
        }).Resolve();
        var verdict = await RunnerFor(resolved).RunAsync("PreToolUse", "run_shell", Shell("ls"), CancellationToken.None);
        Assert.Equal("deny", verdict.Decision);
    }

    [Fact]
    public void Invalid_json_is_a_resolution_error()
    {
        var resolved = With(new HooksConfig { Json = "{ nope" }).Resolve();
        Assert.Contains(resolved.Errors, e => e.Contains("Hooks"));
    }

    [Fact]
    public async Task Handler_deny_blocks_a_call()
    {
        var hooks = new HooksConfig();
        hooks.OnPreToolUse(call => call.ToolName == "run_shell" && ((string?)call.ToolInput?["command"])!.StartsWith("rm")
            ? HookAnswer.Deny("not on this machine")
            : HookAnswer.None);
        var runner = RunnerFor(With(hooks).Resolve());
        Assert.Equal("deny", (await runner.RunAsync("PreToolUse", "run_shell", Shell("rm -rf x"), CancellationToken.None)).Decision);
        Assert.Null((await runner.RunAsync("PreToolUse", "run_shell", Shell("ls"), CancellationToken.None)).Decision);
    }

    [Fact]
    public async Task Handler_rewrite_changes_a_call()
    {
        var hooks = new HooksConfig();
        hooks.OnPreToolUse(_ => HookAnswer.WithInput(new JsonObject { ["command"] = "ls -la" }));
        var verdict = await RunnerFor(With(hooks).Resolve()).RunAsync("PreToolUse", "run_shell", Shell("ls"), CancellationToken.None);
        Assert.Equal("ls -la", (string?)verdict.UpdatedInput!["command"]);
    }

    [Fact]
    public void No_hooks_is_none()
    {
        var resolved = new AgentConfig
        {
            Models = { ["local"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://localhost:1/v1" } },
            DefaultModel = "local",
        }.Resolve();
        Assert.Null(resolved.Hooks.Block);
        Assert.Null(resolved.Hooks.Handlers);
    }
}
