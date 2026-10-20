using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
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
