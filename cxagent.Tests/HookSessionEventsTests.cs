using System.Text.Json.Nodes;
using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Config;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The events a session fires around its turns: a working folder changing, instruction files loading, a model
/// switch, a command expanding, a config change, a classifier refusal, and compaction.
/// </summary>
public class HookSessionEventsTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("cxagent-hook-sev-cfg").FullName;
    private readonly string _repo = Directory.CreateTempSubdirectory("cxagent-hook-sev-repo").FullName;
    private readonly SessionManager _manager;
    private readonly PermissionRulesStore _rules;
    private string Log => Path.Combine(_repo, "hook.log");

    public HookSessionEventsTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent"));
        _manager = SessionManager.Create(new AppPaths(_cfg));
        _rules = new PermissionRulesStore(new AppPaths(_cfg));
        _rules.SetTrust(_repo, TrustState.Trusted);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_cfg, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_repo, recursive: true); } catch (Exception) { }
    }

    /// <summary>A project hook on <paramref name="ev"/> that appends its input to the log, then prints <paramref name="answer"/>.</summary>
    private void Hook(string ev, string answer = "", int exit = 0)
    {
        var answerFile = Path.Combine(_repo, $"{ev}.answer");
        File.WriteAllText(answerFile, answer);
        var command = $"cat >> '{Log}'; echo >> '{Log}'; cat '{answerFile}'; exit {exit}";
        var file = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                [ev] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["timeout"] = 10, ["command"] = command }),
                }),
            },
        };
        File.WriteAllText(Path.Combine(_repo, ".cxagent", "hooks.json"), file.ToJsonString());
    }

    private string Logged() => File.Exists(Log) ? File.ReadAllText(Log) : "";

    private static async Task<bool> Until(Func<bool> check)
    {
        for (var i = 0; i < 100 && !check(); i++) await Task.Delay(50);
        return check();
    }

    private Session Open(ResolvedConfig? config = null, string? folder = null) =>
        _manager.Open(folder ?? _repo, config ?? ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts
            {
                Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(),
                Policy = new PermissionPolicy(folder ?? _repo, _rules, EditMode.AcceptEdits),
            }, AgentMode.Single);

    [Fact]
    public async Task InstructionsLoaded_fires_for_each_file_at_start()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# rules");
        Hook("InstructionsLoaded");
        Open();
        Assert.True(await Until(() => Logged().Contains("AGENTS.md")));
        Assert.Contains("\"load_reason\":\"session_start\"", Logged());
        Assert.Contains("\"memory_type\":\"Project\"", Logged());
    }

    [Fact]
    public async Task CwdChanged_fires_when_a_resume_moves_the_session()
    {
        var other = Directory.CreateTempSubdirectory("cxagent-hook-sev-other").FullName;
        try
        {
            File.WriteAllText(Path.Combine(_repo, ".cxagent", "hooks.json"), "{}");
            var session = Open();
            Directory.CreateDirectory(Path.Combine(other, ".git"));
            Directory.CreateDirectory(Path.Combine(other, ".cxagent"));
            var log = Path.Combine(other, "cwd.log");
            File.WriteAllText(Path.Combine(other, ".cxagent", "hooks.json"), $$"""
                { "hooks": { "CwdChanged": [ { "hooks": [ { "type": "command", "command": "cat > '{{log}}'" } ] } ] } }
                """);
            _rules.SetTrust(other, TrustState.Trusted);
            Assert.True(session.MoveTo(other));
            _manager.Open(session, ResolvedConfig.ForTesting(new MockLlmProvider()), new SessionPorts
            {
                Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(),
                Policy = new PermissionPolicy(other, _rules, EditMode.AcceptEdits),
            });
            Assert.True(await Until(() => File.Exists(log) && File.ReadAllText(log).Contains("new_cwd")));
            var input = JsonNode.Parse(File.ReadAllText(log))!;
            Assert.Equal(Path.GetFullPath(_repo), Path.GetFullPath((string)input["old_cwd"]!));
            Assert.Equal(Path.GetFullPath(other), Path.GetFullPath((string)input["new_cwd"]!));
        }
        finally { Directory.Delete(other, recursive: true); }
    }

    private ResolvedConfig TwoModels()
    {
        var a = new MockLlmProvider();
        var config = ResolvedConfig.ForTesting(a, "a");
        return config with
        {
            Catalog = config.Catalog with
            {
                Instances = ProviderRegistry.FromProviders(
                    new Dictionary<string, ILlmProvider> { ["a"] = a, ["b"] = new MockLlmProvider() }, "a"),
            },
        };
    }

    [Fact]
    public void PreModelSwitch_block_keeps_the_model()
    {
        Hook("PreModelSwitch", exit: 2);
        var session = Open(TwoModels());
        Assert.Equal(CommandStatus.Refused, session.Use("b"));
        Assert.Equal("a", session.InstanceName);
        Assert.Contains("\"source\":\"command\"", Logged());
    }

    [Fact]
    public async Task PostModelSwitch_fires_after_the_switch()
    {
        Hook("PostModelSwitch", answer: "now on b");
        var session = Open(TwoModels());
        Assert.Equal(CommandStatus.Changed, session.Use("b"));
        Assert.Equal("b", session.InstanceName);
        Assert.True(await Until(() => Logged().Contains("\"to_model\"")));
    }

    [Fact]
    public async Task UserPromptExpansion_block_keeps_the_command_from_the_model()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent", "commands"));
        File.WriteAllText(Path.Combine(_repo, ".cxagent", "commands", "deploy.md"), "deploy to prod now");
        Hook("UserPromptExpansion", answer: """{"decision":"block","reason":"not on fridays"}""");
        var model = new MockLlmProvider();
        var sink = new BufferedChatSink();
        var session = _manager.Open(_repo, ResolvedConfig.ForTesting(model), new SessionPorts
        {
            Observer = sink, ToolObserver = new BufferedJobPanel(),
            Policy = new PermissionPolicy(_repo, _rules, EditMode.AcceptEdits),
        }, AgentMode.Single);
        var outcome = session.Submit("/deploy");
        if (outcome is Session.SubmitOutcome.Started started) await started.Turn;
        Assert.True(Logged().Contains("\"command_name\":\"deploy\""), $"{outcome.GetType().Name} | {Logged()} | {sink.Transcript} | listens={session.Hooks!.Listens("UserPromptExpansion")} trusted={session.Hooks.ProjectTrusted} hooks={session.Hooks.Snapshot.Hooks.Count}");
        Assert.Equal(0, model.ChatCallCount);
    }

    [Fact]
    public void ConfigChange_block_keeps_a_session_on_its_config()
    {
        Hook("ConfigChange", exit: 2);
        var session = Open();
        var before = session.Resolution;
        _manager.ApplyConfig(ResolvedConfig.ForTesting(new MockLlmProvider(), "test"),
            new ConfigChange(new HashSet<string> { "test" }, new Dictionary<string, string>(), Everyone: true, Note: "x"));
        Assert.Contains("\"source\":\"user_settings\"", Logged());
        Assert.Same(before, session.Resolution);
    }

    private sealed class Reply(string text) : ILlmProvider
    {
        public int Calls { get; private set; }
        public string ProviderId => "t";
        public string ModelId => "t";
        public string DisplayName => "t";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse { Text = text, StopReason = "end_turn" });
        }
        public IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private HookRunner Runner(string ev, Func<HookRun, HookReply> answer, List<HookRun> runs) =>
        new(new HookRunnerDeps(new HookSnapshot([new HookDefinition(ev, null, "command", HookSource.User) { Command = "h", Name = "h" }],
            [], null, new Dictionary<string, string>()), new HookHandlers(), () => true, _ => { })
        {
            Run = (r, _) => { lock (runs) runs.Add(r); return Task.FromResult(answer(r)); },
            ProjectDir = _repo,
        });

    private static AgentContext Conversation()
    {
        var context = new AgentContext(window: 100_000);
        for (var i = 0; i < 6; i++)
        {
            context.Messages.Add(new ChatMessage { Role = "user", Content = $"question {i} " + new string('x', 400) });
            context.Messages.Add(new ChatMessage { Role = "assistant", Content = $"answer {i} " + new string('y', 400) });
        }
        return context;
    }

    [Fact]
    public async Task PreCompact_block_skips_the_compaction()
    {
        var runs = new List<HookRun>();
        var model = new Reply("summary");
        var context = Conversation();
        var before = context.Count;
        var result = await CompressionRun.RunAsync(
            new CompressionRun.CompressionWork(context, model)
            {
                Hooks = Runner("PreCompact", _ => new HookReply(HookOutcomeKind.Block, "keep it all", null, "", "keep it all"), runs),
                Trigger = "manual",
            },
            new CompressionRun.CompressionReport(new BufferedJobPanel(), "a", "compress"), CancellationToken.None);
        Assert.False(result.Summarised);
        Assert.Equal(0, model.Calls);
        Assert.Equal(before, context.Count);
        Assert.Equal("manual", (string?)Assert.Single(runs).Input["trigger"]);
    }

    [Fact]
    public async Task PermissionDenied_retry_tells_the_model_it_may_try_again()
    {
        var runs = new List<HookRun>();
        _rules.SetTrust(_repo, TrustState.Trusted);
        var policy = new PermissionPolicy(_repo, _rules, EditMode.Auto)
        {
            Hooks = Runner("PermissionDenied", _ => new HookReply(HookOutcomeKind.Ok,
                null, JsonNode.Parse("""{"hookSpecificOutput":{"hookEventName":"PermissionDenied","retry":true}}""")!.AsObject(), "", ""), runs),
        };
        var gate = PermissionDecider.ForTesting(policy, _rules, null, (_, _, _) => Task.FromResult(PermissionChoice.Deny));
        gate.Classifier = new ActionClassifier(new Reply("DENY: rewrites the build script"));
        var path = Path.Combine(_repo, "a.cs");
        var outcome = await gate.RequestAsync(new PermissionRequest(PermissionKind.FileWrite, path, path), CancellationToken.None);
        Assert.False(outcome.Allowed);
        Assert.Contains("may retry", outcome.Reason);
        var input = Assert.Single(runs).Input;
        Assert.Equal("write_file", (string?)input["tool_name"]);
        Assert.Contains("build script", (string?)input["reason"]);
    }
}
