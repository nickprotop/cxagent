using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
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
}
