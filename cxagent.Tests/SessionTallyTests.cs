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
    /// THE PANEL RENDERS THE TALLY IT WAS GIVEN, and counts nothing itself.
    ///
    /// <para>THIS IS THE ASSERTION THE DRIVE NEEDED. The panel once owned the counters and adopted a
    /// tab's only on a SWITCH — so the first session, which never switches, had its turns counted
    /// into an orphan tally, and its panel read zero the moment the user came back to it. Two
    /// existing tests called RecordTurn and stayed green throughout, because both asserted on the
    /// turn CAP rather than the count.</para>
    /// </summary>
    [Fact]
    public void ThePanelShowsTheTurnsOfTheTallyItFollows()
    {
        var panel = new CxAgent.UI.SessionPanel();
        var tally = new SessionTally { Turns = 4, ToolCalls = 9 };

        panel.Follow(tally);
        panel.Refresh(new CxAgent.UI.SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 100,
            ContextWindow = 1000,
            Endpoint = "",
        });

        Assert.Contains("4 turns", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("9 tool calls", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>AND A TURN COMPLETING DOES NOT COUNT ITSELF — the tab is the only writer, so a panel
    /// that also incremented would double every turn on the tab in front.</summary>
    [Fact]
    public void ThePanelDoesNotCountTurnsOfItsOwn()
    {
        var panel = new CxAgent.UI.SessionPanel();
        var tally = new SessionTally();

        panel.Follow(tally);
        panel.TurnCompleted(toolCalls: 3);

        Assert.Equal(0, tally.Turns);
        Assert.Equal(0, tally.ToolCalls);
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
