using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The events beyond the core set that fire inside an agent: sub-agents starting and stopping, a batch of
/// tool calls finishing, the to-do list, compaction, and a reply shown.
/// </summary>
public class HookMoreEventsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-hook-more").FullName;
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
        public List<HookRun> Of(string ev) { lock (Runs) return [.. Runs.Where(r => r.Hook.Event == ev)]; }
    }

    private static HookReply Json(string json) => new(HookOutcomeKind.Ok, null, JsonNode.Parse(json)!.AsObject(), json, "");
    private static HookReply Block(string reason) => new(HookOutcomeKind.Block, reason, null, "", reason);

    private static HookDefinition H(string ev, string? matcher = null) =>
        new(ev, matcher, "command", HookSource.User) { Command = "hook", Name = ev.ToLowerInvariant() };

    private (PermissionPolicy Policy, Fake Fake) Hooks(params HookDefinition[] hooks)
    {
        var fake = new Fake();
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        policy.Hooks = new HookRunner(new HookRunnerDeps(new HookSnapshot(hooks, [], null, new Dictionary<string, string>()),
            new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        return (policy, fake);
    }

    private Agent AgentWith(ILlmProvider provider, PermissionPolicy policy, bool child = false, string? label = null) =>
        new(TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 10) with
        {
            Identity = new() { WorkingDir = _dir, IsSubAgent = child, Label = label },
            Guards = new() { Policy = policy },
        });

    private static LlmResponse Answer(string text) => new() { Text = text, StopReason = "end_turn" };

    private static LlmResponse Calls(params ToolCall[] calls) => new() { Text = "", StopReason = "tool_use", ToolCalls = [.. calls] };

    private static ToolCall Shell(string command, string id) =>
        new() { Name = "run_shell", Id = id, Arguments = JsonSerializer.SerializeToElement(new { command }) };

    private static ToolCall Todo(string id, params (string Text, string Status)[] items) => new()
    {
        Name = "todowrite", Id = id,
        Arguments = JsonSerializer.SerializeToElement(new { todos = items.Select(i => new { content = i.Text, status = i.Status }).ToArray() }),
    };

    // --- sub-agents ------------------------------------------------------------------------------------

    [Fact]
    public async Task SubagentStart_context_reaches_the_child()
    {
        var (policy, fake) = Hooks(H("SubagentStart"));
        fake.Answer = _ => Json("""{"hookSpecificOutput":{"hookEventName":"SubagentStart","additionalContext":"use pnpm"}}""");
        var provider = new ScriptedProvider(Answer("ok"));
        await AgentWith(provider, policy, child: true, label: "explorer").SendAsync("look around", CancellationToken.None, mailFirst: true);
        Assert.Contains("use pnpm", provider.Last);
        var input = Assert.Single(fake.Of("SubagentStart")).Input;
        Assert.Equal("explorer", (string?)input["agent_type"]);
    }

    [Fact]
    public async Task SubagentStop_block_keeps_the_child_working()
    {
        var (policy, fake) = Hooks(H("SubagentStop"), H("Stop"));
        var blocks = 0;
        fake.Answer = r => r.Hook.Event == "SubagentStop" && blocks++ == 0 ? Block("also list the tests") : Json("{}");
        var provider = new ScriptedProvider(Answer("found 3 files"), Answer("found 3 files and 2 tests"));
        var result = await AgentWith(provider, policy, child: true, label: "explorer").SendAsync("look", CancellationToken.None, mailFirst: true);
        Assert.Equal(2, provider.Calls);
        Assert.Contains("also list the tests", provider.Last);
        Assert.Equal("found 3 files and 2 tests", result.Text);
        // A CHILD'S END IS SubagentStop, NEVER Stop.
        Assert.Empty(fake.Of("Stop"));
        Assert.Equal("found 3 files", (string?)fake.Of("SubagentStop")[0].Input["last_assistant_message"]);
    }

    // --- PostToolBatch -----------------------------------------------------------------------------------

    [Fact]
    public async Task PostToolBatch_sees_every_call_and_adds_context()
    {
        var (policy, fake) = Hooks(H("PostToolBatch"));
        fake.Answer = _ => Json("""{"hookSpecificOutput":{"hookEventName":"PostToolBatch","additionalContext":"both done"}}""");
        var provider = new ScriptedProvider(Calls(Shell("echo a", "c1"), Shell("echo b", "c2")), Answer("fin"));
        await AgentWith(provider, policy).SendAsync("go", CancellationToken.None);
        var calls = Assert.Single(fake.Of("PostToolBatch")).Input["tool_calls"]!.AsArray();
        Assert.Equal(2, calls.Count);
        Assert.Contains("both done", provider.Last);
    }

    [Fact]
    public async Task PostToolBatch_block_stops_before_the_next_model_call()
    {
        var (policy, fake) = Hooks(H("PostToolBatch"));
        fake.Answer = _ => Block("enough for now");
        var provider = new ScriptedProvider(Calls(Shell("echo a", "c1")), Answer("fin"));
        var result = await AgentWith(provider, policy).SendAsync("go", CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("enough for now", result.Text);
    }

    // --- the to-do list ----------------------------------------------------------------------------------

    [Fact]
    public async Task TaskCreated_fires_per_new_item_and_can_refuse()
    {
        var (policy, fake) = Hooks(H("TaskCreated"));
        fake.Answer = r => (string?)r.Input["task_subject"] == "delete prod" ? Block("not that one") : Json("{}");
        var provider = new ScriptedProvider(Calls(Todo("t1", ("write tests", "pending"), ("delete prod", "pending"))), Answer("fin"));
        var agent = AgentWith(provider, policy);
        await agent.SendAsync("go", CancellationToken.None);
        Assert.Equal(2, fake.Of("TaskCreated").Count);
        Assert.Contains("not that one", provider.ResultOf(1, "t1"));
        Assert.Empty(agent.Todos);
    }

    [Fact]
    public async Task TaskCompleted_block_keeps_the_item_open()
    {
        var (policy, fake) = Hooks(H("TaskCompleted"));
        fake.Answer = _ => Block("tests still fail");
        var provider = new ScriptedProvider(
            Calls(Todo("t1", ("fix bug", "in_progress"))),
            Calls(Todo("t2", ("fix bug", "completed"))),
            Answer("fin"));
        var agent = AgentWith(provider, policy);
        await agent.SendAsync("go", CancellationToken.None);
        Assert.Equal("fix bug", (string?)Assert.Single(fake.Of("TaskCompleted")).Input["task_subject"]);
        Assert.Contains("tests still fail", provider.ResultOf(2, "t2"));
        Assert.True(Assert.Single(agent.Todos).IsOpen);
    }

    // --- MessageDisplay ------------------------------------------------------------------------------------

    [Fact]
    public async Task MessageDisplay_fires_with_the_reply()
    {
        var (policy, fake) = Hooks(H("MessageDisplay"));
        await AgentWith(new ScriptedProvider(Answer("hello there")), policy).SendAsync("hi", CancellationToken.None);
        for (var i = 0; i < 50 && fake.Of("MessageDisplay").Count == 0; i++) await Task.Delay(20);
        var input = Assert.Single(fake.Of("MessageDisplay")).Input;
        Assert.Equal("hello there", (string?)input["delta"]);
        Assert.True((bool)input["final"]!);
    }
}
