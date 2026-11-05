using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The four hook types beside command: a prompt to a model, an agent that may look before it answers, a web
/// request, and an MCP tool — each mapped onto the same answers a command hook gives.
/// </summary>
public class HookTypesTests
{
    private static HookDefinition Parse(string hookJson, string ev = "PreToolUse") =>
        Assert.Single(HookConfigParser.Parse(JsonDocument.Parse(
            $$"""{ "{{ev}}": [ { "hooks": [ {{hookJson}} ] } ] }""").RootElement, HookSource.User).Hooks);

    private static readonly JsonObject Input = new() { ["hook_event_name"] = "PreToolUse", ["tool_name"] = "run_shell",
        ["tool_input"] = new JsonObject { ["command"] = "rm -rf build", ["path"] = "src/a.cs" } };

    private static Task<HookReply> Run(HookDefinition hook, HookServices services, JsonObject? input = null) =>
        HookDispatch.RunAsync(new HookRun(hook, input ?? Input, "/repo"), services, CancellationToken.None);

    private sealed class Answering(string text) : ILlmProvider
    {
        public List<List<ChatMessage>> Asked { get; } = [];
        public string ProviderId => "t";
        public string ModelId => "t";
        public string DisplayName => "t";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Asked.Add(messages);
            return Task.FromResult(new LlmResponse { Text = text, StopReason = "end_turn" });
        }
        public IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    // --- parsing ---------------------------------------------------------------------------------------

    [Fact]
    public void All_five_types_run()
    {
        foreach (var t in new[] { "command", "prompt", "agent", "http", "mcp_tool" }) Assert.True(HookDefinition.TypeRuns(t), t);
    }

    [Fact]
    public void Prompt_and_mcp_fields_are_read()
    {
        var p = Parse("""{ "type": "prompt", "prompt": "Safe? $ARGUMENTS", "model": "local", "continueOnBlock": true }""");
        Assert.Equal("Safe? $ARGUMENTS", p.Prompt);
        Assert.Equal("local", p.Model);
        Assert.True(p.ContinueOnBlock);
        var m = Parse("""{ "type": "mcp_tool", "server": "scan", "tool": "check", "input": { "file": "${tool_input.path}" } }""");
        Assert.Equal("scan", m.Server);
        Assert.Equal("check", m.Tool);
        Assert.NotNull(m.Input);
    }

    [Fact]
    public void Missing_required_keys_are_problems()
    {
        foreach (var bad in new[] { """{ "type": "prompt" }""", """{ "type": "agent" }""", """{ "type": "http" }""",
                     """{ "type": "mcp_tool", "server": "s" }""" })
        {
            var parse = HookConfigParser.Parse(JsonDocument.Parse($$"""{ "Stop": [ { "hooks": [ {{bad}} ] } ] }""").RootElement, HookSource.User);
            Assert.Empty(parse.Hooks);
            Assert.Single(parse.Problems);
        }
    }

    // --- default timeouts -------------------------------------------------------------------------------

    [Fact]
    public void Default_timeouts_follow_the_contract()
    {
        Assert.Equal(600, HookDispatch.TimeoutSeconds(Parse("""{ "type": "command", "command": "x" }""")));
        Assert.Equal(30, HookDispatch.TimeoutSeconds(Parse("""{ "type": "command", "command": "x" }""", "UserPromptSubmit")));
        Assert.Equal(30, HookDispatch.TimeoutSeconds(Parse("""{ "type": "prompt", "prompt": "x" }""")));
        Assert.Equal(60, HookDispatch.TimeoutSeconds(Parse("""{ "type": "agent", "prompt": "x" }""")));
        Assert.Equal(5, HookDispatch.TimeoutSeconds(Parse("""{ "type": "http", "url": "http://x", "timeout": 5 }""")));
    }

    // --- prompt ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_ok_true_is_no_objection()
    {
        var model = new Answering("""{"ok": true}""");
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "Safe? $ARGUMENTS", "model": "m" }"""),
            new HookServices { Model = n => n == "m" ? model : null });
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
        Assert.Null(r.Decision);
        var asked = string.Join("\n", model.Asked[0].Select(m => m.Content));
        Assert.Contains("rm -rf build", asked);
        Assert.DoesNotContain("$ARGUMENTS", asked);
    }

    [Fact]
    public async Task Prompt_ok_false_on_PreToolUse_denies_and_ends_the_turn()
    {
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "Safe?", "model": "m" }"""),
            new HookServices { Model = _ => new Answering("""Sure. {"ok": false, "reason": "destroys the build"}""") });
        Assert.Equal("block", r.Decision);
        Assert.Equal("destroys the build", r.DecisionReason);
        Assert.False(r.Continue);
    }

    [Fact]
    public async Task Prompt_continueOnBlock_keeps_the_turn()
    {
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "Safe?", "model": "m", "continueOnBlock": true }"""),
            new HookServices { Model = _ => new Answering("""{"ok": false, "reason": "no"}""") });
        Assert.Equal("block", r.Decision);
        Assert.True(r.Continue);
    }

    [Fact]
    public async Task Prompt_impossible_lets_a_Stop_end()
    {
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "Done?", "model": "m" }""", "Stop"),
            new HookServices { Model = _ => new Answering("""{"ok": false, "reason": "x", "impossible": true}""") });
        Assert.Null(r.Decision);
    }

    [Fact]
    public async Task Prompt_falls_back_to_the_default_model()
    {
        var model = new Answering("""{"ok": true}""");
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "x" }"""),
            new HookServices { Model = n => n == "cheap" ? model : null, DefaultModel = "cheap" });
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
        Assert.Single(model.Asked);
    }

    [Fact]
    public async Task Prompt_with_no_model_fails_never_borrows()
    {
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "x" }"""), new HookServices());
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
        Assert.Contains("defaultModel", r.Reason);
    }

    [Fact]
    public async Task Prompt_with_an_unknown_model_fails()
    {
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "x", "model": "theirs" }"""), new HookServices());
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
        Assert.Contains("theirs", r.Reason);
    }

    [Fact]
    public async Task Prompt_answer_that_is_not_json_fails()
    {
        var r = await Run(Parse("""{ "type": "prompt", "prompt": "x", "model": "m" }"""),
            new HookServices { Model = _ => new Answering("I think it is fine") });
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
    }

    // --- agent ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Agent_ok_false_blocks_and_keeps_the_turn()
    {
        HookAgentRequest? seen = null;
        var r = await Run(Parse("""{ "type": "agent", "prompt": "Do the tests pass? $ARGUMENTS", "model": "m" }""", "Stop"),
            new HookServices
            {
                Model = _ => new Answering(""),
                Agent = (req, _) => { seen = req; return Task.FromResult("""Checked. {"ok": false, "reason": "2 tests fail"}"""); },
            });
        Assert.Equal("block", r.Decision);
        Assert.Equal("2 tests fail", r.DecisionReason);
        Assert.True(r.Continue);
        Assert.Contains("Do the tests pass?", seen!.Prompt);
    }

    // --- http -----------------------------------------------------------------------------------------

    private sealed class Stub(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public string? SeenBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task Http_posts_the_input_and_reads_the_json_answer()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"policy"}}""");
        var r = await Run(Parse("""{ "type": "http", "url": "http://hooks.test/pre", "headers": { "Authorization": "Bearer $TOKEN", "X-Other": "$SECRET" }, "allowedEnvVars": ["TOKEN"] }"""),
            new HookServices { Http = new HttpClient(stub), Env = n => n switch { "TOKEN" => "abc", "SECRET" => "leak", _ => null } });
        Assert.Equal("deny", r.Decision);
        Assert.Equal(HttpMethod.Post, stub.Seen!.Method);
        Assert.Contains("rm -rf build", stub.SeenBody);
        Assert.Equal("Bearer abc", stub.Seen.Headers.Authorization!.ToString());
        Assert.Equal("", stub.Seen.Headers.GetValues("X-Other").Single());
    }

    [Fact]
    public async Task Http_empty_body_is_ok()
    {
        var r = await Run(Parse("""{ "type": "http", "url": "http://hooks.test/x" }"""),
            new HookServices { Http = new HttpClient(new Stub(HttpStatusCode.NoContent, "")) });
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
    }

    [Fact]
    public async Task Http_error_status_and_plain_text_fail()
    {
        var bad = await Run(Parse("""{ "type": "http", "url": "http://hooks.test/x" }"""),
            new HookServices { Http = new HttpClient(new Stub(HttpStatusCode.InternalServerError, "boom")) });
        Assert.Equal(HookOutcomeKind.Failed, bad.Kind);
        var text = await Run(Parse("""{ "type": "http", "url": "http://hooks.test/x" }"""),
            new HookServices { Http = new HttpClient(new Stub(HttpStatusCode.OK, "hello")) });
        Assert.Equal(HookOutcomeKind.Failed, text.Kind);
    }

    // --- mcp_tool -------------------------------------------------------------------------------------

    private sealed class Server(string answer, bool error = false) : IMcpServer
    {
        public JsonElement? Args { get; private set; }
        public string Name => "scan";
        public string? Instructions => null;
        public string? Error => null;
        public IReadOnlyList<McpToolDef> Tools { get; } = [];
        public Task<string> CallToolAsync(string tool, JsonElement args, CancellationToken ct)
        {
            Args = args;
            return Task.FromResult(error ? "error: " + answer : answer);
        }
    }

    [Fact]
    public async Task Mcp_tool_substitutes_input_and_reads_its_answer()
    {
        var server = new Server("""{"decision":"block","reason":"secret found"}""");
        var r = await Run(Parse("""{ "type": "mcp_tool", "server": "scan", "tool": "check", "input": { "file": "${tool_input.path}", "fixed": 3 } }"""),
            new HookServices { McpServer = n => n == "scan" ? server : null });
        Assert.Equal("block", r.Decision);
        Assert.Equal("src/a.cs", server.Args!.Value.GetProperty("file").GetString());
        Assert.Equal(3, server.Args.Value.GetProperty("fixed").GetInt32());
    }

    [Fact]
    public async Task Mcp_tool_error_fails()
    {
        var r = await Run(Parse("""{ "type": "mcp_tool", "server": "scan", "tool": "t" }"""),
            new HookServices { McpServer = _ => new Server("scanner down", error: true) });
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
        Assert.Contains("scanner down", r.Reason);
    }

    [Fact]
    public async Task Mcp_tool_on_an_unknown_server_fails()
    {
        var r = await Run(Parse("""{ "type": "mcp_tool", "server": "nope", "tool": "t" }"""), new HookServices());
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
        Assert.Contains("nope", r.Reason);
    }
}
