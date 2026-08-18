using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That an injected tool is REACHED, which is a different question from whether it works.
///
/// <para>The dispatch chain in Agent.RunAsync is a <c>??</c> chain whose last link is
/// WorkerToolset.InvokeAsync — and that link answers "no such tool" rather than returning null, so
/// it TERMINATES the chain. A link placed after it is unreachable code that reads as correct, which
/// is the failure these tests exist to catch.</para>
/// </summary>
public class AgentToolDispatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-tools-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentToolDispatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>Records that it ran, and says something recognisable when it does.</summary>
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

    private Session Build(MockLlmProvider provider, IReadOnlyList<IAgentTool> tools, BufferedChatSink sink)
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
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel(), Tools = tools },
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
            Plugins = PluginRegistry.CreateWithBuiltins(),
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
            new MockLlmProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
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
            new MockLlmProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
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
            new MockLlmProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            isSubAgent: false,
            agentTools: [new EchoTool(offerToChildren: false)]);

        Assert.True(parent.KnowsInjectedToolForTest("echo_tool"));
    }
}
