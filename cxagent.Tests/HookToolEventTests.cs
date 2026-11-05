using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Agents;
using CxAgent.Core.Hooks;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Tool hooks fired from the agent's own dispatch: a PreToolUse answer stops or changes the call before it
/// runs, and a PostToolUse answer reaches the model beside the result.
/// </summary>
public class HookToolEventTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-hook-tool").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Fake
    {
        public Func<HookRun, HookReply> Answer { get; set; } = _ => new(HookOutcomeKind.Ok, null, null, "", "");
        public List<HookRun> Runs { get; } = [];

        public Task<HookReply> Run(HookRun run, CancellationToken ct)
        {
            lock (Runs) Runs.Add(run);
            return Task.FromResult(Answer(run));
        }
    }

    private static HookReply Json(string json) => new(HookOutcomeKind.Ok, null, JsonNode.Parse(json)!.AsObject(), json, "");

    private static ToolCall Shell(string command) => new()
    {
        Name = "run_shell", Id = "c1",
        Arguments = JsonSerializer.SerializeToElement(new { command }),
    };

    private (Agent Agent, ScriptedProvider Provider, Fake Fake) Make(params HookDefinition[] hooks)
    {
        var fake = new Fake();
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        policy.Hooks = new HookRunner(new HookRunnerDeps(new HookSnapshot(hooks, [], null, new Dictionary<string, string>()),
            new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [Shell("echo original")] },
            new LlmResponse { Text = "done", StopReason = "end_turn" });
        var agent = new Agent(TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 4) with
        {
            Identity = new() { WorkingDir = _dir },
            Guards = new() { Policy = policy },
        });
        return (agent, provider, fake);
    }

    private static HookDefinition H(string ev, string? matcher = "run_shell") =>
        new(ev, matcher, "command", HookSource.User) { Command = "hook", Name = "guard" };

    [Fact]
    public async Task PreToolUse_deny_stops_the_call()
    {
        var (agent, provider, fake) = Make(H("PreToolUse"));
        fake.Answer = _ => new HookReply(HookOutcomeKind.Block, "no shell today", null, "", "no shell today");
        await agent.SendAsync("go", CancellationToken.None);
        var result = provider.ResultOf(1, "c1");
        Assert.Contains("blocked by hook", result);
        Assert.Contains("no shell today.", result);
        // A USER'S HOOK IS NOT THE PROJECT'S: the refusal names neither, only that a configured hook said no.
        Assert.DoesNotContain("project", result);
        Assert.DoesNotContain("original", result.Replace("echo original", ""));
    }

    [Fact]
    public async Task PreToolUse_rewrite_changes_the_call()
    {
        var (agent, provider, fake) = Make(H("PreToolUse"));
        fake.Answer = _ => Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"echo rewritten"}}}""");
        await agent.SendAsync("go", CancellationToken.None);
        Assert.Contains("rewritten", provider.ResultOf(1, "c1"));
    }

    [Fact]
    public async Task PreToolUse_input_carries_the_call()
    {
        var (agent, _, fake) = Make(H("PreToolUse"));
        await agent.SendAsync("go", CancellationToken.None);
        var input = Assert.Single(fake.Runs).Input;
        Assert.Equal("PreToolUse", (string?)input["hook_event_name"]);
        Assert.Equal("run_shell", (string?)input["tool_name"]);
        Assert.Equal("echo original", (string?)input["tool_input"]!["command"]);
        Assert.Equal("main", (string?)input["agent"]);
    }

    [Fact]
    public async Task PostToolUse_context_reaches_the_model()
    {
        var (agent, provider, fake) = Make(H("PostToolUse"));
        fake.Answer = _ => Json("""{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"lint is clean"}}""");
        await agent.SendAsync("go", CancellationToken.None);
        var result = provider.ResultOf(1, "c1");
        Assert.Contains("original", result);
        Assert.Contains("lint is clean", result);
        Assert.Contains("original", (string?)Assert.Single(fake.Runs).Input["tool_response"]!["output"]);
    }

    [Fact]
    public async Task PostToolUse_block_reason_reaches_the_model()
    {
        var (agent, provider, fake) = Make(H("PostToolUse"));
        fake.Answer = _ => new HookReply(HookOutcomeKind.Block, "format the file", null, "", "format the file");
        await agent.SendAsync("go", CancellationToken.None);
        Assert.Contains("format the file", provider.ResultOf(1, "c1"));
    }

    [Fact]
    public async Task PostToolUseFailure_fires_on_failure()
    {
        var (agent, _, fake) = Make(H("PostToolUse"), H("PostToolUseFailure"));
        fake.Answer = r => r.Hook.Event == "PreToolUse" ? null! : new HookReply(HookOutcomeKind.Ok, null, null, "", "");
        // A command that fails: a non-zero exit is a failed call.
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [Shell("exit 7")] },
            new LlmResponse { Text = "done", StopReason = "end_turn" });
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        policy.Hooks = new HookRunner(new HookRunnerDeps(new HookSnapshot([H("PostToolUse"), H("PostToolUseFailure")], [], null,
            new Dictionary<string, string>()), new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        var agent2 = new Agent(TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 4) with
        {
            Identity = new() { WorkingDir = _dir },
            Guards = new() { Policy = policy },
        });
        await agent2.SendAsync("go", CancellationToken.None);
        var run = Assert.Single(fake.Runs);
        Assert.Equal("PostToolUseFailure", run.Hook.Event);
        Assert.NotNull(run.Input["error"]);
    }

    [Fact]
    public async Task Unmatched_tool_does_not_fire()
    {
        var (agent, _, fake) = Make(H("PreToolUse", matcher: "write_file"));
        await agent.SendAsync("go", CancellationToken.None);
        Assert.Empty(fake.Runs);
    }

    [Fact]
    public async Task Child_agent_calls_fire_hooks()
    {
        var fake = new Fake();
        var parent = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        parent.Hooks = new HookRunner(new HookRunnerDeps(new HookSnapshot([H("PreToolUse")], [], null, new Dictionary<string, string>()),
            new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        var child = parent.NarrowedTo(_dir);
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [Shell("echo hi")] },
            new LlmResponse { Text = "done", StopReason = "end_turn" });
        var agent = new Agent(TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 4) with
        {
            Identity = new() { WorkingDir = _dir, IsSubAgent = true, Label = "explorer" },
            Guards = new() { Policy = child },
        });
        await agent.SendAsync("go", CancellationToken.None);
        var input = Assert.Single(fake.Runs).Input;
        Assert.NotEqual("main", (string?)input["agent"]);
        Assert.Equal(agent.Id, (string?)input["agent_id"]);
    }

    private sealed class McpServer : IMcpServer
    {
        public string Name => "files";
        public string? Instructions => null;
        public string? Error => null;
        public IReadOnlyList<McpToolDef> Tools { get; } =
            [new("read", "reads", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone())];
        public Task<string> CallToolAsync(string tool, JsonElement args, CancellationToken ct) => Task.FromResult("mcp result");
    }

    private sealed class RecordingGate : IPermissionGate
    {
        public List<PermissionRequest> Asked { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(PermissionOutcome.Allow);
        }
    }

    [Fact]
    public async Task PreToolUse_answer_reaches_an_mcp_call()
    {
        var fake = new Fake { Answer = _ => Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask"}}""") };
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        policy.Hooks = new HookRunner(new HookRunnerDeps(new HookSnapshot([H("PreToolUse", matcher: null)], [], null,
            new Dictionary<string, string>()), new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        var gate = new RecordingGate();
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [new ToolCall { Name = "files_read", Id = "m1", Arguments = JsonSerializer.SerializeToElement(new { }) }] },
            new LlmResponse { Text = "done", StopReason = "end_turn" });
        var agent = new Agent(TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 4) with
        {
            Identity = new() { WorkingDir = _dir },
            Guards = new() { Policy = policy },
            Tools = new(JobRegistry.CreateWithBuiltins()) { Mcp = new McpToolset([new McpServer()], gate) },
        });
        await agent.SendAsync("go", CancellationToken.None);
        var asked = Assert.Single(gate.Asked);
        Assert.Equal("ask", asked.HookDecision);
        Assert.Equal("guard", asked.HookDecidedBy);
    }

    [Fact]
    public async Task PreToolUse_ask_prompts_for_a_tool_that_never_reaches_the_gate()
    {
        var fake = new Fake { Answer = _ => Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask"}}""") };
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        policy.Hooks = new HookRunner(new HookRunnerDeps(new HookSnapshot([H("PreToolUse", matcher: "todowrite")], [], null,
            new Dictionary<string, string>()), new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        var gate = new DenyingGate();
        var todo = new ToolCall { Name = "todowrite", Id = "t1",
            Arguments = JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "x", status = "pending" } } }) };
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [todo] },
            new LlmResponse { Text = "done", StopReason = "end_turn" });
        var agent = new Agent(TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 4) with
        {
            Identity = new() { WorkingDir = _dir },
            Guards = new() { Policy = policy, Gate = gate },
        });
        await agent.SendAsync("go", CancellationToken.None);
        var asked = Assert.Single(gate.Asked);
        Assert.Equal("ask", asked.HookDecision);
        Assert.Contains("todowrite", asked.Display);
        Assert.Contains("declined", provider.ResultOf(1, "t1"));
    }

    private sealed class DenyingGate : IPermissionGate
    {
        public List<PermissionRequest> Asked { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(PermissionOutcome.ByUser);
        }
    }

    [Fact]
    public async Task PostToolUse_continue_false_ends_the_turn()
    {
        var (agent, provider, fake) = Make(H("PostToolUse"));
        fake.Answer = _ => Json("""{"continue":false,"stopReason":"budget exceeded"}""");
        var result = await agent.SendAsync("go", CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("budget exceeded", result.Text);
    }

    [Fact]
    public async Task PreToolUse_continue_false_stops_the_call_and_the_turn()
    {
        var (agent, provider, fake) = Make(H("PreToolUse"));
        fake.Answer = _ => Json("""{"continue":false,"stopReason":"maintenance window"}""");
        var result = await agent.SendAsync("go", CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("maintenance window", result.Text);
    }
}
