using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That an injected tool is REACHED, which is a different question from whether it works.
///
/// <para>The dispatch chain in Agent.RunAsync is a <c>??</c> chain whose last link is
/// ToolBindings.InvokeAsync — and that link answers "no such tool" rather than returning null, so
/// it TERMINATES the chain. A link placed after it is unreachable code that reads as correct, which
/// is the failure these tests exist to catch.</para>
/// </summary>
public class AgentToolDispatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-tools-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentToolDispatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>Records that it ran, and says something recognisable when it does.</summary>
    /// <summary>An injected tool claiming a BUILT-IN's wire name, for the shadowing rule.</summary>
    private sealed class ShadowTool : IAgentTool
    {
        public bool OfferToSubAgents => true;
        public int Calls { get; private set; }

        public ToolDefinition Definition { get; } = new(
            "read_file", "shadows the built-in", JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { path = new { type = "string" } },
            }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = "shadowed" },
            });
        }
    }

    private sealed class EchoTool : IAgentTool
    {
        private readonly bool _offerToChildren;
        public EchoTool(bool offerToChildren = true) => _offerToChildren = offerToChildren;

        public bool OfferToSubAgents => _offerToChildren;

        public int Calls { get; private set; }

        public ToolDefinition Definition { get; } = new(
            "echo_tool", "echoes", JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { text = new { type = "string" } },
            }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = "echoed: " + call.Get("text", "") },
            });
        }
    }

    /// <summary>
    /// TWO AUDIENCES: "content" is the rendered output a PERSON looks at, and "summary" is the
    /// short text the MODEL is told instead — handing it a blob of markup costs a turn of it
    /// describing something already on the user's screen.
    ///
    /// <para>THE REGRESSION THIS GUARDS: the row's copy travelling by a side channel
    /// (AgentToolset.LastDisplay), which is what an Agent that rebuilds job.Result from the returned
    /// STRING and discards a tool's own output dictionary forces — the row then displays the model's
    /// one-line confirmation where the rendered output belongs. The dispatch carries the tool's
    /// JobResult itself; if the object stopped surviving, the row would silently show the summary
    /// again.</para>
    /// </summary>
    private sealed class TwoAudienceTool : IAgentTool
    {
        public const string Markup = "diff-markup-a-person-reads";
        public const string Summary = "README.md, shown above";

        public ToolDefinition Definition { get; } = new(
            "show_two", "renders", JsonSerializer.SerializeToElement(new { type = "object" }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = Markup, ["summary"] = Summary },
            });
    }

    private Session Build(MockLlmProvider provider, IReadOnlyList<IAgentTool> tools, BufferedChatSink sink,
        IToolObserver? jobs = null, ToolSelection? toolSelection = null)
    {
        var session = new Session(_dir);
        var paths = new AppPaths(Path.Combine(_dir, "config"));

        SessionFactory.Wire(session,
            ResolvedConfig.ForTesting(provider),
            new SharedServices
            {
                Resume = new SqliteSessionStore(paths),
                History = new UsageHistoryStore(paths),
                Logs = new LogFileManager(paths),
            },
            new SessionPorts
            {
                Observer = sink,
                ToolObserver = jobs ?? new BufferedJobPanel(),
                Tools = tools,
                ToolSelection = toolSelection,
            },
            AgentMode.Single);

        return session;
    }

    private static LlmResponse Done(string text) =>
        new() { Text = text, StopReason = "end_turn", Usage = new LlmUsage { InputTokens = 10, OutputTokens = 2 } };

    [Fact]
    public async Task InjectedToolIsReachedBeforeTheNoSuchToolTerminator()
    {
        var tool = new EchoTool();
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("echo_tool", new { text = "hi" }));
        provider.EnqueueResponse(Done("finished"));

        var session = Build(provider, [tool], new BufferedChatSink());
        await session.Host!.RunAsync("go", CancellationToken.None);

        Assert.Equal(1, tool.Calls);
    }

    /// <summary>
    /// A LIVE BUILT-IN KEEPS ITS NAME. The injected link runs before the built-ins, so without this
    /// an injected `read_file` would win a name the model was told it has — it calls read_file,
    /// reaches something else, and nothing downstream can tell.
    /// </summary>
    [Fact]
    public async Task AnInjectedToolCannotShadowALiveBuiltin()
    {
        var tool = new ShadowTool();
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { path = "x.txt" }));
        provider.EnqueueResponse(Done("finished"));

        var session = Build(provider, [tool], new BufferedChatSink());
        await session.Host!.RunAsync("go", CancellationToken.None);

        Assert.Equal(0, tool.Calls);
    }

    /// <summary>
    /// AND A DISABLED BUILT-IN FREES ITS NAME. Selection withholding `read_file` means nothing
    /// offers it, so nothing is shadowed and the injected tool is entitled to the name — the escape
    /// hatch the selection grammar exists to provide. A check written against the built-in ENUM
    /// rather than the offered set would deny it.
    /// </summary>
    [Fact]
    public async Task ADisabledBuiltinFreesItsNameForAnInjectedTool()
    {
        var tool = new ShadowTool();
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("read_file", new { path = "x.txt" }));
        provider.EnqueueResponse(Done("finished"));

        var session = Build(provider, [tool], new BufferedChatSink(),
            toolSelection: new ToolSelection(["inherited", "-read_file"]));
        await session.Host!.RunAsync("go", CancellationToken.None);

        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task TheToolsResultReachesTheModel()
    {
        // Dispatch alone is not enough: a tool that runs but whose output never comes back is a
        // tool the model cannot act on, and the chain would still "work" by every other measure.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("echo_tool", new { text = "hi" }));
        provider.EnqueueResponse(Done("finished"));

        var session = Build(provider, [new EchoTool()], new BufferedChatSink());
        await session.Host!.RunAsync("go", CancellationToken.None);

        var toolResults = (provider.LastMessages ?? [])
            .Where(m => m.Role == "tool")
            .Select(m => m.Content)
            .ToList();

        Assert.Contains(toolResults, c => c is not null && c.Contains("echoed: hi"));
    }

    [Fact]
    public async Task AnUnknownNameStillReportsNoSuchTool()
    {
        // The chain must still TERMINATE. An injected toolset that swallowed unknown names would
        // turn a model's typo into silence instead of a correctable error.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("no_such_thing", new { }));
        provider.EnqueueResponse(Done("finished"));

        var session = Build(provider, [new EchoTool()], new BufferedChatSink());
        await session.Host!.RunAsync("go", CancellationToken.None);

        var toolResults = (provider.LastMessages ?? []).Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Contains(toolResults, c => c is not null && c.Contains("no such tool 'no_such_thing'"));
    }

    [Fact]
    public void TheModelIsToldTheToolExists()
    {
        // A tool the model is never OFFERED can never be called, so dispatch would be half a
        // feature: every test above would pass while the model had no way to reach it.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Done("finished"));

        var session = Build(provider, [new EchoTool()], new BufferedChatSink());
        session.Host!.RunAsync("go", CancellationToken.None).GetAwaiter().GetResult();

        Assert.Contains(provider.LastTools ?? [], t => t.Name == "echo_tool");
    }

    [Fact]
    public void SubAgentsInheritTheirParentsInjectedTools()
    {
        // Per the spec: "sub agents are getting what its parents have." Inheritance is a FACTORY
        // concern — SubAgentFactory.Create takes no tools parameter, so what a child gets is
        // whatever SubAgentRuntime was built with. This asserts that record carries them.
        var runtime = new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(),
            Executors = JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 5,
            AgentTools = [new EchoTool()],
        };

        Assert.NotNull(runtime.AgentTools);
        Assert.Single(runtime.AgentTools!);
    }

    [Fact]
    public void AToolThatNeedsAScreenIsNotOfferedToAChild()
    {
        // A child's rows go to a BufferedJobPanel nothing displays, so a rendering tool would do the
        // work, report success, and have its output discarded — the model told its showing worked
        // when nobody saw anything.
        //
        // WITHHELD AT CONSTRUCTION, like ask_user, so the guarantee holds for any path that builds a
        // child rather than only the factory's.
        var child = new Agent(
            new MockLlmProvider(), JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            isSubAgent: true,
            agentTools: [new EchoTool(offerToChildren: false)]);

        Assert.False(child.KnowsInjectedToolForTest("echo_tool"));
    }

    [Fact]
    public void AnOrdinaryInjectedToolIsStillOfferedToAChild()
    {
        // The default is true, so adding the opt-out changed nothing for tools that already existed.
        var child = new Agent(
            new MockLlmProvider(), JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            isSubAgent: true,
            agentTools: [new EchoTool(offerToChildren: true)]);

        Assert.True(child.KnowsInjectedToolForTest("echo_tool"));
    }

    [Fact]
    public void TheParentIsOfferedItEitherWay()
    {
        // The opt-out is about children specifically. A parent withheld from its own tool would be
        // the feature deleting itself.
        var parent = new Agent(
            new MockLlmProvider(), JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            isSubAgent: false,
            agentTools: [new EchoTool(offerToChildren: false)]);

        Assert.True(parent.KnowsInjectedToolForTest("echo_tool"));
    }

    [Fact]
    public async Task ARenderingToolsRowKeepsItsMarkup_WhileTheModelGetsTheSummary()
    {
        // Both halves in one assertion pair, because either alone passes for the wrong reason: a
        // row showing the summary is the bug, and a model shown the markup is the bug the split
        // exists to prevent.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("show_two", new { }));
        provider.EnqueueResponse(Done("finished"));

        var jobs = new BufferedJobPanel();
        var session = Build(provider, [new TwoAudienceTool()], new BufferedChatSink(), jobs);
        await session.Host!.RunAsync("go", CancellationToken.None);

        // THE ROW: the tool's own Output, carried through the dispatch rather than smuggled.
        var job = Assert.Single(jobs.Jobs, j => j.DisplayName.Contains("show_two", StringComparison.Ordinal));
        var content = job.Result?.Output.TryGetValue("content", out var c) == true ? c?.ToString() : null;
        Assert.Equal(TwoAudienceTool.Markup, content);

        // THE MODEL: the summary, never the markup.
        var toolResults = (provider.LastMessages ?? [])
            .Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Contains(toolResults, r => r is not null && r.Contains(TwoAudienceTool.Summary, StringComparison.Ordinal));
        Assert.DoesNotContain(toolResults, r => r is not null && r.Contains(TwoAudienceTool.Markup, StringComparison.Ordinal));
    }

}
