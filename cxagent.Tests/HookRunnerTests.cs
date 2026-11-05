using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// One session's hook runner: which hooks an event reaches, the trust gate on the repository's, and how
/// several answers become one. Processes are faked; <see cref="HookProcessTests"/> covers real ones.
/// </summary>
public class HookRunnerTests
{
    private sealed class Fake
    {
        public Dictionary<string, HookReply> Replies { get; } = [];
        public List<string> Ran { get; } = [];
        public List<JsonObject> Inputs { get; } = [];

        public Task<HookReply> Run(HookRun run, CancellationToken ct)
        {
            lock (Ran) { Ran.Add(run.Hook.Command!); Inputs.Add(run.Input); }
            return Task.FromResult(Replies.TryGetValue(run.Hook.Command!, out var r)
                ? r : new HookReply(HookOutcomeKind.Ok, null, null, "", ""));
        }
    }

    private static HookDefinition H(string command, HookSource source = HookSource.User, string ev = "PreToolUse",
        string? matcher = null, bool onFailureBlock = false, string type = "command") =>
        new(ev, matcher, type, source) { Command = command, OnFailureBlock = onFailureBlock };

    private static HookReply Json(string json) =>
        new(HookOutcomeKind.Ok, null, JsonNode.Parse(json)!.AsObject(), json, "");

    private static HookReply Decide(string decision, string reason = "r") =>
        Json($$$"""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"{{{decision}}}","permissionDecisionReason":"{{{reason}}}"}}""");

    private static (HookRunner Runner, Fake Fake, List<HookNotice> Notices) Make(IEnumerable<HookDefinition> hooks,
        Func<bool>? trusted = null, HookHandlers? handlers = null)
    {
        var fake = new Fake();
        var notices = new List<HookNotice>();
        var snapshot = new HookSnapshot([.. hooks], [], "/repo", new Dictionary<string, string>());
        var runner = new HookRunner(new HookRunnerDeps(snapshot, handlers ?? new HookHandlers(), trusted ?? (() => true),
            n => { lock (notices) notices.Add(n); }) { Run = fake.Run, ProjectDir = "/repo" });
        return (runner, fake, notices);
    }

    private static Task<HookVerdict> Pre(HookRunner r, string tool = "run_shell") =>
        r.RunAsync("PreToolUse", tool, new JsonObject { ["tool_name"] = tool, ["tool_input"] = new JsonObject { ["command"] = "ls" } },
            CancellationToken.None);

    [Fact]
    public async Task Project_hook_does_not_run_until_trusted()
    {
        var trusted = false;
        var (r, fake, _) = Make([H("user"), H("proj", HookSource.Project), H("local", HookSource.Local)], () => trusted);
        await Pre(r);
        Assert.Equal(["user"], fake.Ran);
        trusted = true;
        await Pre(r);
        Assert.Equal(["local", "proj", "user", "user"], fake.Ran.Order());
    }

    [Fact]
    public async Task Deny_beats_ask_beats_allow()
    {
        var (r, fake, _) = Make([H("a"), H("b"), H("c")]);
        fake.Replies["a"] = Decide("allow");
        fake.Replies["b"] = Decide("ask");
        Assert.Equal("ask", (await Pre(r)).Decision);
        fake.Replies["c"] = Decide("deny", "no");
        var v = await Pre(r);
        Assert.Equal("deny", v.Decision);
        Assert.Equal("no", v.Reason);
        Assert.Equal("PreToolUse c", v.DecidedBy);
    }

    [Fact]
    public async Task Defer_sits_between_deny_and_ask()
    {
        var (r, fake, _) = Make([H("a"), H("b")]);
        fake.Replies["a"] = Decide("ask");
        fake.Replies["b"] = Decide("defer");
        Assert.Equal("defer", (await Pre(r)).Decision);
    }

    [Fact]
    public async Task Exit_two_on_PreToolUse_is_deny()
    {
        var (r, fake, _) = Make([H("a")]);
        fake.Replies["a"] = new HookReply(HookOutcomeKind.Block, "no rm", null, "", "no rm");
        var v = await Pre(r);
        Assert.Equal("deny", v.Decision);
        Assert.Equal("no rm", v.Reason);
    }

    [Fact]
    public async Task Rewrites_apply_in_source_order()
    {
        var (r, fake, _) = Make([H("local", HookSource.Local), H("user")]);
        fake.Replies["user"] = Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"from-user"}}}""");
        fake.Replies["local"] = Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"from-local"}}}""");
        Assert.Equal("from-local", (string?)(await Pre(r)).UpdatedInput!["command"]);
    }

    [Fact]
    public async Task Rewrite_in_alias_names_comes_back_in_cxagent_names()
    {
        var (r, fake, _) = Make([H("a")]);
        fake.Replies["a"] = Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"file_path":"x"}}}""");
        var v = await Pre(r, "read_file");
        Assert.Equal("x", (string?)v.UpdatedInput!["path"]);
    }

    [Fact]
    public async Task Failed_hook_continues_by_default_with_notice()
    {
        var (r, fake, notices) = Make([H("a")]);
        fake.Replies["a"] = HookReply.Failed("exited 3");
        var v = await Pre(r);
        Assert.Null(v.Decision);
        var n = Assert.Single(notices);
        Assert.True(n.IsError);
        Assert.Contains("exited 3", n.Text);
    }

    [Fact]
    public async Task Failed_hook_blocks_with_onFailure_block()
    {
        var (r, fake, _) = Make([H("a", onFailureBlock: true)]);
        fake.Replies["a"] = HookReply.Failed("timed out after 5 s");
        var v = await Pre(r);
        Assert.Equal("deny", v.Decision);
        Assert.Contains("failed", v.Reason);
        Assert.Contains("timed out", v.Reason);
    }

    [Fact]
    public async Task OnFailure_block_is_ignored_where_nothing_can_block()
    {
        var (r, fake, _) = Make([H("a", ev: "PostToolUse", onFailureBlock: true)]);
        fake.Replies["a"] = HookReply.Failed("x");
        var v = await r.RunAsync("PostToolUse", "run_shell", [], CancellationToken.None);
        Assert.Null(v.Decision);
    }

    [Fact]
    public async Task Unsupported_hooks_never_run()
    {
        var (r, fake, _) = Make([H("http", type: "http"), H("setup", ev: "Setup")]);
        await Pre(r);
        await r.RunAsync("Setup", null, [], CancellationToken.None);
        Assert.Empty(fake.Ran);
    }

    [Fact]
    public async Task Matcher_filters_tool_events()
    {
        var (r, fake, _) = Make([H("shell", matcher: "Bash"), H("files", matcher: "Edit|Write")]);
        await Pre(r, "run_shell");
        Assert.Equal(["shell"], fake.Ran);
    }

    [Fact]
    public async Task Context_is_collected()
    {
        var (r, fake, _) = Make([H("a", ev: "UserPromptSubmit"), H("b", ev: "UserPromptSubmit")]);
        fake.Replies["a"] = Json("""{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"one"}}""");
        fake.Replies["b"] = new HookReply(HookOutcomeKind.Ok, null, null, "two\n", "");
        var v = await r.RunAsync("UserPromptSubmit", null, [], CancellationToken.None);
        Assert.Equal(["one", "two"], v.Context.Order());
    }

    [Fact]
    public async Task Handlers_run_after_files()
    {
        var handlers = new HookHandlers();
        string? seen = null;
        handlers.OnPreToolUse(input => { seen = (string?)input.ToolInput?["command"]; return HookAnswer.None; });
        var (r, fake, _) = Make([H("a")], handlers: handlers);
        fake.Replies["a"] = Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"rewritten"}}}""");
        await Pre(r);
        Assert.Equal("rewritten", seen);
    }

    [Fact]
    public async Task Handler_deny_is_a_decision()
    {
        var handlers = new HookHandlers();
        handlers.OnPreToolUse(_ => HookAnswer.Deny("embedder says no"));
        var (r, _, _) = Make([], handlers: handlers);
        var v = await Pre(r);
        Assert.Equal("deny", v.Decision);
        Assert.Equal("embedder says no", v.Reason);
    }

    [Fact]
    public async Task Replace_swaps_the_snapshot()
    {
        var (r, fake, _) = Make([H("old")]);
        r.Replace(new HookSnapshot([H("new")], [], "/repo", new Dictionary<string, string>()));
        await Pre(r);
        Assert.Equal(["new"], fake.Ran);
    }
}
