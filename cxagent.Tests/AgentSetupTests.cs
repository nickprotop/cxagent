using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// AN AGENT IS BUILT FROM NAMED GROUPS, and each group stays small. The constructor took 31 parameters, grown one
/// reasonable parameter per feature; these tests hold the line CLAUDE.md draws at three, and pin that what the
/// constructor derives from what it is not given is what it always derived.
/// </summary>
public sealed class AgentSetupTests
{
    /// <summary>
    /// NO GROUP TAKES MORE THAN THREE PARAMETERS POSITIONALLY — what is optional is an init member. The groups are
    /// found from AgentSetup itself, so a new group is held to the rule without anyone adding it here.
    /// </summary>
    [Fact]
    public void EveryGroup_TakesAtMostThreePositionalParameters()
    {
        var groups = typeof(AgentSetup).GetProperties().Select(p => p.PropertyType)
            .Where(t => t.Namespace == typeof(AgentSetup).Namespace && t.Name.StartsWith("Agent", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(7, groups.Count);
        Assert.All(groups, g => Assert.True(Positional(g) <= 3, $"{g.Name} takes {Positional(g)} positionally"));
    }

    /// <summary>THE CHECK CAN FAIL: a record of four positional parameters is counted as four.</summary>
    [Fact]
    public void ThePositionalCount_SeesAFourParameterRecord() =>
        Assert.Equal(4, Positional(typeof(FourAtOnce)));

    private sealed record FourAtOnce(int A, int B, int C, int D);

    // THE WIDEST PUBLIC CONSTRUCTOR — a record's primary one; its copy constructor is not public.
    private static int Positional(Type group) =>
        group.GetConstructors().Select(c => c.GetParameters().Length).DefaultIfEmpty(0).Max();

    /// <summary>
    /// WHAT IS NOT GIVEN IS DERIVED AS IT ALWAYS WAS: no spawner, not a sub-agent, no policy, today's date taken when
    /// the agent is built, an unbounded turn cap for 0 — and a conversation of its own for each agent, even two built
    /// from one setup.
    /// </summary>
    [Fact]
    public void TheDefaults_AreTheOldParameterDefaults()
    {
        var setup = Minimal(maxTurns: 0);

        var first = new Agent(setup);
        var second = new Agent(setup);

        Assert.False(first.HasSpawner);
        Assert.False(first.IsSubAgent);
        Assert.Null(first.Policy);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), first.StartedOn);
        Assert.Equal(int.MaxValue, first.MaxTurns);
        Assert.NotSame(first.Context, second.Context);
    }

    /// <summary>
    /// THE SESSION HOST FORWARDS EVERY VALUE: its runtime's tools, guards and brief reach the agent whole, and the
    /// model, outputs, identity and limits it builds carry what the runtime said. A value dropped on the way would
    /// compile cleanly and leave the agent without it — the failure the groups exist to make impossible.
    /// </summary>
    [Fact]
    public void TheHost_ForwardsEveryValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cx-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var policy = new PermissionPolicy(dir, new PermissionRulesStore(new AppPaths(dir)));
        var tools = new AgentTools(JobRegistry.CreateWithBuiltins())
        {
            Injected = [], Dynamic = () => [], PluginInstructions = () => [], ModelFacingCommands = () => [],
            Selection = new ToolSelection(["read_file"]),
        };
        var guards = new AgentGuards { Policy = policy, AskUser = (_, _) => Task.FromResult(new QuestionAnswers([])) };
        var brief = new AgentBrief { Briefing = "b", GlobalInstructionsDir = dir, StartedOn = new DateOnly(2026, 1, 2) };
        var runtime = new AgentHost.AgentRuntime
        {
            Provider = new MockLlmProvider(), InstanceName = "inst", WorkingDir = dir, Tools = tools, Guards = guards,
            Brief = brief,
        };

        var host = new AgentHost(runtime, new BufferedChatSink(), new BufferedJobPanel(), new AgentHost.SessionStores());
        var setup = host.AgentSetup;

        Assert.Same(tools, setup.Tools);
        Assert.Same(guards, setup.Guards);
        Assert.Same(brief, setup.Brief);
        Assert.Same(runtime.Provider, setup.Model.Provider);
        Assert.Equal("inst", setup.Model.InstanceName);
        Assert.Equal(dir, setup.Identity.WorkingDir);
        Assert.Null(setup.Identity.SessionId);   // the session's own agent: its id IS the session's
        Assert.False(setup.Identity.IsSubAgent);
        Assert.Same(host.Context, setup.Conversation);
    }

    /// <summary>
    /// ONE WAY TO BUILD AN AGENT. A second constructor beside the setup is where a flat parameter list grows back,
    /// one reasonable argument per feature, and every value added there is one the groups never see.
    /// </summary>
    [Fact]
    public void TheAgent_HasOneConstructor_TakingAnAgentSetup()
    {
        var ctor = Assert.Single(typeof(Agent).GetConstructors());
        Assert.Equal([typeof(AgentSetup)], ctor.GetParameters().Select(p => p.ParameterType));
    }

    /// <summary>
    /// A CHILD NEVER GETS WHAT ONLY THE SESSION MAY HAVE — the spawner, the model-facing commands, the way to ask
    /// the user — even when the runtime it is built from carries all three. The runtime's groups are the session's,
    /// so the factory is what keeps them out; Agent does not refuse a child a spawner, because nesting is planned.
    /// </summary>
    [Fact]
    public void AChild_NeverGetsTheSessionsSpawnerOrCommands()
    {
        var parent = new SubAgentSpawner(new SubAgentFactory(Runtime(new AgentTools(JobRegistry.CreateWithBuiltins()))));
        var tools = new AgentTools(JobRegistry.CreateWithBuiltins()) { Spawner = parent, ModelFacingCommands = () => [("c", "s")] };
        var runtime = Runtime(tools) with
        {
            Guards = new AgentGuards { AskUser = (_, _) => Task.FromResult(new QuestionAnswers([])) },
        };

        var child = new SubAgentFactory(runtime).Create().Agent;

        Assert.Null(child.Setup.Tools.Spawner);
        Assert.Null(child.Setup.Tools.ModelFacingCommands);
        Assert.Null(child.Setup.Guards.AskUser);
        Assert.False(child.HasSpawner);
    }

    /// <summary>
    /// EVERY VALUE THE FACTORY HOLDS REACHES THE CHILD — each set to something distinct, so a member dropped on the
    /// way is a null where a value was, not a default that happens to match. A child in a folder of its own gets a
    /// policy narrowed to it; everything else arrives as the session holds it.
    /// </summary>
    [Fact]
    public void TheFactory_ForwardsEveryValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cx-setup-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        var policy = new PermissionPolicy(dir, new PermissionRulesStore(new AppPaths(dir)));
        var classifier = new ActionClassifier(new MockLlmProvider());
        var delivery = new NoDelivery();
        var logs = new LogFileManager(new AppPaths(dir));
        var mcp = new Core.Mcp.McpToolset([]);
        IReadOnlyList<IAgentTool> injected = [];
        Func<IReadOnlyList<IAgentTool>> dynamic = () => [];
        Func<IReadOnlyList<Core.Plugins.PluginInstructions>> instructions = () => [];
        var tools = new AgentTools(JobRegistry.CreateWithBuiltins())
        {
            Mcp = mcp, Injected = injected, Dynamic = dynamic, PluginInstructions = instructions,
            Selection = new ToolSelection(["read_file"]),
        };
        var runtime = Runtime(tools) with
        {
            InstanceName = "inst", Guards = new AgentGuards { Policy = policy, Classifier = classifier },
            Delivery = delivery, Logs = logs, GlobalInstructionsDir = dir, WorkingDir = dir,
        };

        var setup = new SubAgentFactory(runtime).Create(new SubAgentFactory.ChildRequest
        {
            Briefing = "brief", CallerContext = "known", Label = "lbl", ParentAgentId = "parent",
            Root = new ChildRoot(sub, "sub"),
        }).Agent.Setup;

        Assert.Same(runtime.Provider, setup.Model.Provider);
        Assert.Same(runtime.Ledger, setup.Model.Ledger);
        Assert.Equal("inst", setup.Model.InstanceName);
        Assert.NotNull(setup.Outputs.Logs);
        Assert.NotSame(logs, setup.Outputs.Logs);   // nested under the parent's
        Assert.Same(delivery, setup.Outputs.Delivery);
        Assert.Equal(sub, setup.Identity.WorkingDir);
        Assert.True(setup.Identity.IsSubAgent);
        Assert.Equal("lbl", setup.Identity.Label);
        Assert.Equal("parent", setup.Identity.SessionId);
        Assert.StartsWith(AgentTypeCatalog.DefaultTypeName + " on inst:", setup.Identity.RunsOn);
        Assert.Equal("sub", setup.Identity.Root);
        Assert.Same(tools.Executors, setup.Tools.Executors);
        Assert.Same(mcp, setup.Tools.Mcp);
        Assert.Same(injected, setup.Tools.Injected);
        Assert.Same(dynamic, setup.Tools.Dynamic);
        Assert.Same(instructions, setup.Tools.PluginInstructions);
        Assert.Equal(tools.Selection, setup.Tools.Selection);
        Assert.Equal(PathResolver.RealFolder(sub), setup.Guards.Policy!.Boundary);
        Assert.Same(classifier, setup.Guards.Classifier);
        Assert.Equal("brief", setup.Brief.Briefing);
        Assert.Equal("known", setup.Brief.CallerContext);
        Assert.Equal(dir, setup.Brief.GlobalInstructionsDir);
        Assert.Null(setup.Brief.StartedOn);   // a child is dated the day it is built
        Assert.Equal(7, setup.Limits.MaxTurns);
        Assert.Equal(40_000, setup.Limits.CompressAbove);
        Assert.Equal(200_000, setup.Conversation!.Window);
    }

    private static SubAgentFactory.SubAgentRuntime Runtime(AgentTools tools) => new()
    {
        Provider = new MockLlmProvider(), Tools = tools, Ledger = new TokenLedger(), MaxTurns = 7,
        CompressAbove = 40_000, ContextWindow = 200_000,
    };

    private sealed class NoDelivery : IAgentDelivery
    {
        public DeliveryOutcome Tell(string agentId, string text) => DeliveryOutcome.Refused;
    }

    private static AgentSetup Minimal(int maxTurns) => new()
    {
        Model = new AgentModel(new MockLlmProvider(), new TokenLedger()),
        Outputs = new AgentOutputs(new BufferedChatSink(), new BufferedJobPanel()),
        Identity = new AgentIdentity(),
        Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        Guards = new AgentGuards(),
        Brief = new AgentBrief(),
        Limits = new AgentLimits(maxTurns),
        Conversation = null,
    };
}
