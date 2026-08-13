using CxAgent.Core.Agent;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// How a session is set up to work.
///
/// <para>One axis today. The tests that matter are the ones pinning the SHAPE — that adding an axis
/// costs a property and a default, and that nothing existing has to change — because that is the
/// entire reason this type exists ahead of the axes it will hold.</para>
/// </summary>
public class WorkingModeTests
{
    [Fact]
    public void ASessionStartsAlone()
    {
        Assert.Equal(AgentMode.Single, WorkingMode.Default.Agent);
        Assert.False(WorkingMode.Default.CanDelegate);
    }

    /// <summary>
    /// THE CONVERSION THAT MADE THE REFACTOR MECHANICAL. Twenty-two test initialisers say
    /// <c>Mode = AgentMode.FanOut</c> and none had to change: an agent mode IS a working mode with
    /// nothing else set, so this is a widening and cannot mean something different from the explicit
    /// form.
    /// </summary>
    [Fact]
    public void AnAgentMode_IsAWorkingMode()
    {
        WorkingMode implicitly = AgentMode.FanOut;

        Assert.Equal(new WorkingMode(AgentMode.FanOut), implicitly);
        Assert.True(implicitly.CanDelegate);
    }

    /// <summary>
    /// CHANGED BY REPLACEMENT, NOT MUTATION. A mutable value shared between the host and a running
    /// turn would let a mid-turn switch change what the model is judged against halfway through a
    /// request; a new value means a turn reads whatever was true when it started.
    /// </summary>
    [Fact]
    public void ChangingAnAxis_ProducesANewValue()
    {
        var before = WorkingMode.Default;
        var after = before with { Agent = AgentMode.FanOut };

        Assert.False(before.CanDelegate);   // the original is untouched
        Assert.True(after.CanDelegate);
    }

    /// <summary>
    /// The name comes from one place, so the CLI, /mode and the status bar cannot disagree — and so
    /// there is a single place to decide how a COMBINATION is spelled once there is more than one
    /// axis to combine.
    /// </summary>
    [Theory]
    [InlineData(AgentMode.Single, "single")]
    [InlineData(AgentMode.FanOut, "fan-out")]
    public void ItReadsBackAsTheNameTheUserTyped(AgentMode agent, string expected) =>
        Assert.Equal(expected, new WorkingMode(agent).ToString());

    /// <summary>
    /// Two sessions set up the same way are the same mode. Value equality is what makes "did this
    /// change?" answerable without comparing axis by axis — which is the check that would silently
    /// stop being complete the moment a second axis is added.
    /// </summary>
    [Fact]
    public void TwoModesWithTheSameAxes_AreEqual()
    {
        Assert.Equal(new WorkingMode(AgentMode.FanOut), new WorkingMode(AgentMode.FanOut));
        Assert.NotEqual(new WorkingMode(AgentMode.FanOut), new WorkingMode(AgentMode.Single));
    }

    /// <summary>
    /// CanDelegate is not decoration: it is the question every caller actually asks, and it kept
    /// `Mode == AgentMode.FanOut` out of the agent's spawn check — where a second axis would
    /// otherwise have had to be remembered by hand.
    /// </summary>
    [Fact]
    public void CanDelegate_AnswersTheQuestionCallersAsk()
    {
        Assert.True(new WorkingMode(AgentMode.FanOut).CanDelegate);
        Assert.False(new WorkingMode(AgentMode.Single).CanDelegate);
    }
}
