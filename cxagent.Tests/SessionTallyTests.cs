using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That one window's panel does not sum two conversations together.
///
/// <para>THE COUNTERS LIVED IN THE PANEL, and there is one panel — the right-hand column is a
/// property of the WINDOW, not of a session. So two sessions shared one turn count, one tool-call
/// count and one elapsed clock: a second tab opened an hour into a session reported an hour of work
/// it had not done, and every turn either session took incremented the same total.</para>
/// </summary>
public class SessionTallyTests
{
    /// <summary>EACH CONVERSATION COUNTS ITS OWN.</summary>
    [Fact]
    public void TwoTalliesDoNotShareCounters()
    {
        var alpha = new SessionTally();
        var beta = new SessionTally();

        alpha.Turns += 3;
        alpha.ToolCalls += 7;

        Assert.Equal(0, beta.Turns);
        Assert.Equal(0, beta.ToolCalls);
    }

    /// <summary>
    /// AND THE CLOCK STARTS WITH THE CONVERSATION, not with the process. A tally made later has a
    /// later start, which is what makes a second tab's elapsed time its own.
    /// </summary>
    [Fact]
    public void EachTallyStartsWhenItIsCreated()
    {
        var first = new SessionTally();
        Thread.Sleep(10);
        var second = new SessionTally();

        Assert.True(second.Started > first.Started,
            $"second tally started at {second.Started:O}, not after {first.Started:O}");
    }

    /// <summary>
    /// A REFERENCE, NOT A COPY — the property the panel depends on. The panel is HANDED a tally and
    /// increments through it, so a value type here would send every recorded turn to a copy nobody
    /// reads again and the panel would sit at zero forever.
    /// </summary>
    [Fact]
    public void ATallyHandedOnIsTheSameObject()
    {
        var tally = new SessionTally();
        var handed = tally;

        handed.Turns++;

        Assert.Equal(1, tally.Turns);
    }
}
