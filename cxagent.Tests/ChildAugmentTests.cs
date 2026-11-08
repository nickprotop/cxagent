using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The factory's one door for a child's origin: unused, it changes nothing; used, it can only add what
/// <see cref="ChildAugment"/> names; broken, it costs the module and never the spawn.
/// </summary>
public sealed class ChildAugmentTests
{
    // ONE TOOLS INSTANCE FOR EVERY RUNTIME, so two factories differing only in their hook can be compared whole.
    private static readonly AgentTools Tools = new(JobRegistry.CreateWithBuiltins());

    private static SubAgentFactory.SubAgentRuntime Runtime(Func<string, ChildAugment?>? augment = null) => new()
    {
        Provider = new MockLlmProvider(),
        Tools = Tools,
        Ledger = new TokenLedger(),
        MaxTurns = 7,
        Augment = augment,
    };

    [Fact]
    public void No_origin_builds_the_same_child_as_before()
    {
        var asked = false;
        var request = new SubAgentFactory.ChildRequest { CallerContext = "CTX", Label = "lbl" };

        var plain = new SubAgentFactory(Runtime()).Create(request);
        var hooked = new SubAgentFactory(Runtime(_ => { asked = true; return new ChildAugment { Context = "EXP" }; })).Create(request);

        Assert.False(asked);
        Assert.Equal(plain.Agent.Setup.Brief, hooked.Agent.Setup.Brief);
        Assert.Equal(plain.Agent.Setup.Tools with { }, hooked.Agent.Setup.Tools with { });
        Assert.Null(hooked.Origin);
        Assert.Null(hooked.AugmentRefusal);
    }

    [Fact]
    public void Origin_without_a_hook_is_a_plain_child_that_keeps_its_origin()
    {
        var child = new SubAgentFactory(Runtime()).Create(new SubAgentFactory.ChildRequest { Origin = "colleague:Charting", CallerContext = "CTX" });

        Assert.Equal("colleague:Charting", child.Origin);
        Assert.Equal("CTX", child.Agent.Setup.Brief.CallerContext);
        Assert.Null(child.AugmentRefusal);
    }

    [Fact]
    public void Hook_context_heads_the_caller_context()
    {
        var child = new SubAgentFactory(Runtime(_ => new ChildAugment { Context = "EXP" }))
            .Create(new SubAgentFactory.ChildRequest { Origin = "colleague:Charting", CallerContext = "CTX" });

        Assert.Equal("EXP\n\nCTX", child.Agent.Setup.Brief.CallerContext);
    }

    [Fact]
    public void Hook_context_alone_is_the_caller_context()
    {
        var child = new SubAgentFactory(Runtime(_ => new ChildAugment { Context = "EXP" }))
            .Create(new SubAgentFactory.ChildRequest { Origin = "colleague:Charting" });

        Assert.Equal("EXP", child.Agent.Setup.Brief.CallerContext);
    }

    [Fact]
    public void A_throwing_hook_yields_a_plain_child_and_says_why()
    {
        var child = new SubAgentFactory(Runtime(_ => throw new InvalidOperationException("boom")))
            .Create(new SubAgentFactory.ChildRequest { Origin = "colleague:Charting", CallerContext = "CTX" });

        Assert.Equal("boom", child.AugmentRefusal);
        Assert.Equal("CTX", child.Agent.Setup.Brief.CallerContext);
        Assert.Equal("colleague:Charting", child.Origin);
    }

    [Fact]
    public void The_hook_is_asked_with_the_origin_verbatim()
    {
        string? seen = null;
        new SubAgentFactory(Runtime(o => { seen = o; return null; }))
            .Create(new SubAgentFactory.ChildRequest { Origin = "colleague:Charting" });

        Assert.Equal("colleague:Charting", seen);
    }

    /// <summary>A tool of the hook's, standing in for recall: a name, and a count of the calls that reached it.</summary>
    private sealed class Named(string name) : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } = new(name, "a module's tool",
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }));
        public CxAgent.Core.Permissions.PermissionRequest? Gate(JobParameters call) => null;
        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new JobResult { Success = true, Output = new() { ["text"] = name + " answered" } });
        }
    }

    private static readonly SubAgentFactory.ChildRequest Colleague = new() { Origin = "colleague:Charting", CallerContext = "CTX" };

    [Fact]
    public void Extra_tools_are_offered()
    {
        var child = new SubAgentFactory(Runtime(_ => new ChildAugment { ExtraTools = [new Named("recall")] })).Create(Colleague);

        Assert.Contains("recall", child.Agent.OfferedToolNames());
    }

    /// <summary>
    /// With no selection anywhere a child is offered everything; the hook's tool is added to that, never put in its place:
    /// a selection of the added name alone would have been the child's whole tool set.
    /// </summary>
    [Fact]
    public void A_default_child_keeps_all_its_tools()
    {
        var plain = new SubAgentFactory(Runtime()).Create(Colleague).Agent.OfferedToolNames();
        var hooked = new SubAgentFactory(Runtime(_ => new ChildAugment { ExtraTools = [new Named("recall")] })).Create(Colleague)
            .Agent.OfferedToolNames();

        Assert.Equal(plain.Append("recall").Order(), hooked.Order());
    }

    [Fact]
    public void Extra_tools_survive_an_exact_selection()
    {
        var runtime = Runtime(_ => new ChildAugment { ExtraTools = [new Named("recall")] }) with
        {
            Tools = Tools with { Selection = new ToolSelection(["read_file"]) },
        };

        var offered = new SubAgentFactory(runtime).Create(Colleague).Agent.OfferedToolNames();

        Assert.Equal(["read_file", "recall"], offered.Order());
    }

    /// <summary>
    /// A name a built-in holds is left out — never shadowed — and the worker stays what it is: refusing the whole door
    /// would un-mark a colleague, and its rounds would stop being kept.
    /// </summary>
    [Fact]
    public void A_built_in_name_is_left_out()
    {
        var child = new SubAgentFactory(Runtime(_ => new ChildAugment { ExtraTools = [new Named("read_file")], Context = "EXP" }))
            .Create(Colleague);

        Assert.Equal(["read_file"], child.ToolsLeftOut);
        Assert.Null(child.AugmentRefusal);
        Assert.Equal("colleague:Charting", child.Origin);
        Assert.False(child.Agent.KnowsInjectedTool("read_file"));
    }

    /// <summary>
    /// A plugin offering the same name is left out for this child, whenever it arrives: two tools of one name are a request
    /// a provider refuses, and the module's tool is the one this worker was given. (An MCP tool's name always carries its
    /// server's, so it cannot take the bare name.)
    /// </summary>
    [Fact]
    public async Task A_plugin_tool_that_takes_the_name_is_left_out()
    {
        var mine = new Named("recall");
        var plugin = new Named("recall");
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("recall", new { }));
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var runtime = Runtime(_ => new ChildAugment { ExtraTools = [mine] }) with
        {
            Provider = provider,
            Tools = Tools with { Dynamic = () => [plugin] },
        };

        var child = new SubAgentFactory(runtime).Create(Colleague);
        Assert.Single(child.Agent.OfferedToolNames(), n => n == "recall");
        await child.Agent.SendAsync("look it up", CancellationToken.None);

        Assert.Equal(1, mine.Calls);
        Assert.Equal(0, plugin.Calls);
    }

    [Fact]
    public void Read_also_reaches_the_child_s_policy()
    {
        var top = CxAgent.Core.Permissions.PermissionPolicy.RealFolder(Directory.CreateTempSubdirectory("door-read-").FullName);
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(top, "p")).FullName;
            var workplace = Directory.CreateDirectory(Path.Combine(top, "w")).FullName;
            var rules = new CxAgent.Core.Permissions.PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(Path.Combine(top, "cfg")));
            rules.SetTrust(project, CxAgent.Core.Permissions.TrustState.Trusted);
            rules.SetTrust(workplace, CxAgent.Core.Permissions.TrustState.Trusted);
            var runtime = Runtime(_ => new ChildAugment { ReadAlso = new([workplace], null) }) with
            {
                Guards = new AgentGuards { Policy = new CxAgent.Core.Permissions.PermissionPolicy(project, rules) },
            };

            var policy = new SubAgentFactory(runtime).Create(Colleague).Agent.Setup.Guards.Policy!;

            Assert.True(policy.IsSilentlyAllowed(new(CxAgent.Core.Permissions.PermissionKind.FileRead, Path.Combine(workplace, "a.cs"), null)));
        }
        finally { Directory.Delete(top, recursive: true); }
    }
}
