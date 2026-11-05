using System.Diagnostics;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The events around a turn rather than a tool: the prompt going in, the answer coming out, the session
/// opening and closing, and a permission prompt about to show.
/// </summary>
public class HookTurnEventTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-hook-turn").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Fake
    {
        public Func<HookRun, CancellationToken, Task<HookReply>> Answer { get; set; } =
            (_, _) => Task.FromResult(new HookReply(HookOutcomeKind.Ok, null, null, "", ""));
        public List<HookRun> Runs { get; } = [];

        public Task<HookReply> Run(HookRun run, CancellationToken ct)
        {
            lock (Runs) Runs.Add(run);
            return Answer(run, ct);
        }
    }

    private static HookReply Ok(string json) => new(HookOutcomeKind.Ok, null, JsonNode.Parse(json)!.AsObject(), json, "");
    private static HookReply Block(string reason) => new(HookOutcomeKind.Block, reason, null, "", reason);

    private static HookDefinition H(string ev, int? timeout = null) =>
        new(ev, null, "command", HookSource.User) { Command = "hook", Name = ev.ToLowerInvariant(), TimeoutSeconds = timeout };

    private (PermissionPolicy Policy, HookRunner Runner, Fake Fake) Hooks(params HookDefinition[] hooks)
    {
        var fake = new Fake();
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        var runner = new HookRunner(new HookRunnerDeps(new HookSnapshot(hooks, [], null, new Dictionary<string, string>()),
            new HookHandlers(), () => true, _ => { }) { Run = fake.Run, ProjectDir = _dir });
        policy.Hooks = runner;
        return (policy, runner, fake);
    }

    private RecordingSink _sink = new();

    private Agent AgentWith(ILlmProvider provider, PermissionPolicy policy) =>
        new(TestAgents.Setup(provider, new(_sink = new RecordingSink(), new NullJobPanel()), 8) with
        {
            Identity = new() { WorkingDir = _dir },
            Guards = new() { Policy = policy },
        });

    private static LlmResponse Answer(string text) => new() { Text = text, StopReason = "end_turn" };

    [Fact]
    public async Task UserPromptSubmit_block_keeps_prompt_from_model()
    {
        var (policy, _, fake) = Hooks(H("UserPromptSubmit"));
        fake.Answer = (_, _) => Task.FromResult(Block("no secrets in prompts"));
        var provider = new ScriptedProvider(Answer("hi"));
        var result = await AgentWith(provider, policy).SendAsync("my password is x", CancellationToken.None);
        Assert.Equal(0, provider.Calls);
        Assert.Contains("no secrets in prompts", result.Text);
        Assert.Equal("my password is x", (string?)Assert.Single(fake.Runs).Input["prompt"]);
    }

    [Fact]
    public async Task UserPromptSubmit_context_is_added()
    {
        var (policy, _, fake) = Hooks(H("UserPromptSubmit"));
        fake.Answer = (_, _) => Task.FromResult(new HookReply(HookOutcomeKind.Ok, null, null, "branch: main\n", ""));
        var provider = new ScriptedProvider(Answer("hi"));
        await AgentWith(provider, policy).SendAsync("hello", CancellationToken.None);
        Assert.Contains("branch: main", provider.Last);
        Assert.Contains("hello", provider.Last);
    }

    [Fact]
    public async Task SessionStart_context_reaches_the_model()
    {
        var (policy, runner, fake) = Hooks(H("SessionStart"));
        fake.Answer = (_, _) => Task.FromResult(Ok("""{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"uses bun test"}}"""));
        await HookLifecycle.SessionStartAsync(runner, "startup", CancellationToken.None);
        Assert.Equal("startup", (string?)Assert.Single(fake.Runs).Input["source"]);
        var provider = new ScriptedProvider(Answer("hi"), Answer("again"));
        var agent = AgentWith(provider, policy);
        await agent.SendAsync("hello", CancellationToken.None);
        Assert.Contains("uses bun test", provider.Last);
        await agent.SendAsync("next", CancellationToken.None);
        // ONCE: the start's context is said at the start, not repeated with every prompt.
        Assert.Single(provider.Last.Split("uses bun test")[1..]);
    }

    [Fact]
    public async Task Stop_block_continues_the_turn()
    {
        var (policy, _, fake) = Hooks(H("Stop"));
        var blocks = 0;
        fake.Answer = (_, _) => Task.FromResult(blocks++ == 0 ? Block("run the tests first") : Ok("{}"));
        var provider = new ScriptedProvider(Answer("done"), Answer("tests pass, done"));
        var result = await AgentWith(provider, policy).SendAsync("fix it", CancellationToken.None);
        Assert.Equal(2, provider.Calls);
        Assert.Contains("run the tests first", provider.Last);
        Assert.Equal("tests pass, done", result.Text);
        // SAID ON SCREEN, so the user knows why the agent carried on after it looked finished.
        Assert.Contains(_sink.Notices.Concat(_sink.Messages), n => n.Contains("stop") && n.Contains("run the tests first"));
        Assert.False((bool)fake.Runs[0].Input["stop_hook_active"]!);
        Assert.True((bool)fake.Runs[1].Input["stop_hook_active"]!);
        Assert.Equal("done", (string?)fake.Runs[0].Input["last_assistant_message"]);
    }

    [Fact]
    public async Task Stop_block_is_bounded()
    {
        var (policy, _, fake) = Hooks(H("Stop"));
        fake.Answer = (_, _) => Task.FromResult(Block("never done"));
        var provider = new ScriptedProvider(Answer("a"), Answer("b"), Answer("c"), Answer("d"), Answer("e"), Answer("f"));
        var result = await AgentWith(provider, policy).SendAsync("go", CancellationToken.None);
        Assert.Equal(HookLifecycle.MaxStopBlocks + 1, provider.Calls);
        Assert.Equal("d", result.Text);
    }

    private sealed class Throwing : ILlmProvider
    {
        public string ProviderId => "t";
        public string ModelId => "t";
        public string DisplayName => "t";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct) =>
            throw new HttpRequestException("503 overloaded");
        public IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct) =>
            throw new HttpRequestException("503 overloaded");
    }

    [Fact]
    public async Task StopFailure_fires_on_provider_error()
    {
        var (policy, _, fake) = Hooks(H("StopFailure"));
        await Assert.ThrowsAnyAsync<Exception>(() => AgentWith(new Throwing(), policy).SendAsync("go", CancellationToken.None));
        var deadline = Stopwatch.StartNew();
        while (fake.Runs.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        var input = Assert.Single(fake.Runs).Input;
        Assert.True(((string?)input["error"])?.Contains("503") == true, input.ToJsonString());
    }

    [Fact]
    public async Task SessionEnd_is_budgeted()
    {
        var (_, runner, fake) = Hooks(H("SessionEnd"));
        fake.Answer = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Ok("{}"); };
        var watch = Stopwatch.StartNew();
        await HookLifecycle.SessionEndAsync(runner, "exit");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
        Assert.Equal("exit", (string?)Assert.Single(fake.Runs).Input["reason"]);
    }

    [Fact]
    public void SessionEnd_budget_follows_the_longest_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), HookLifecycle.EndBudget([H("SessionEnd")]));
        Assert.Equal(TimeSpan.FromSeconds(10), HookLifecycle.EndBudget([H("SessionEnd", 10)]));
        Assert.Equal(TimeSpan.FromSeconds(60), HookLifecycle.EndBudget([H("SessionEnd", 600)]));
    }

    private (PermissionDecider Gate, List<PermissionRequest> Shown) Gate(PermissionPolicy policy)
    {
        var shown = new List<PermissionRequest>();
        return (PermissionDecider.ForTesting(policy, new PermissionRulesStore(new AppPaths(_dir)), null,
            (r, _, _) => { shown.Add(r); return Task.FromResult(PermissionChoice.Once); }), shown);
    }

    [Fact]
    public async Task Notification_fires_on_prompt()
    {
        var (policy, _, fake) = Hooks(H("Notification"));
        var (gate, shown) = Gate(policy);
        await gate.RequestAsync(new PermissionRequest(PermissionKind.Shell, "make deploy", "make deploy", "make deploy"), CancellationToken.None);
        Assert.Single(shown);
        var deadline = Stopwatch.StartNew();
        while (fake.Runs.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        var input = Assert.Single(fake.Runs).Input;
        Assert.Equal("permission_prompt", (string?)input["notification_type"]);
        Assert.Contains("make deploy", (string?)input["message"]);
    }

    [Fact]
    public async Task PermissionRequest_deny_answers_prompt()
    {
        var (policy, _, fake) = Hooks(H("PermissionRequest"));
        fake.Answer = (_, _) => Task.FromResult(Ok("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"not here"}}}"""));
        var (gate, shown) = Gate(policy);
        var outcome = await gate.RequestAsync(new PermissionRequest(PermissionKind.Shell, "make deploy", "make deploy", "make deploy"), CancellationToken.None);
        Assert.False(outcome.Allowed);
        Assert.Equal("hook", outcome.DeniedBy);
        Assert.Empty(shown);
        var input = Assert.Single(fake.Runs).Input;
        Assert.Equal("run_shell", (string?)input["tool_name"]);
        Assert.Equal("make deploy", (string?)input["tool_input"]!["command"]);
    }

    [Fact]
    public async Task PermissionRequest_allow_answers_prompt()
    {
        var (policy, _, fake) = Hooks(H("PermissionRequest"));
        fake.Answer = (_, _) => Task.FromResult(Ok("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""));
        var (gate, shown) = Gate(policy);
        Assert.True((await gate.RequestAsync(new PermissionRequest(PermissionKind.Shell, "make", "make", "make"), CancellationToken.None)).Allowed);
        Assert.Empty(shown);
    }
}
