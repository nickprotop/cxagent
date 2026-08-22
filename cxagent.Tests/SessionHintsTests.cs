using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The two lines the front end volunteers about sessions nobody asked about.
///
/// <para>THESE MOVED OUT OF SessionsCommandTests with the code they cover. Decide and RenderPlain
/// answer a question the user ASKED and belong to every front end; these are unprompted, and one of
/// them names this app's binary — which is what makes them the app's rather than the session
/// layer's.</para>
/// </summary>
public class SessionHintsTests
{
    /// <summary>Nothing recorded here means nothing to say. Silence is the correct output.</summary>
    [Fact]
    public void StartupHintIsAbsentWhenTheFolderHasNoHistory()
    {
        Assert.Null(SessionHints.Startup(0, null));
    }

    /// <summary>
    /// THE UNFINISHED ONE IS NAMED, because "ended without closing" is the case where someone lost
    /// work and is looking for it.
    /// </summary>
    [Fact]
    public void StartupHintNamesAnUnfinishedSessionAndItsSize()
    {
        var hint = SessionHints.Startup(4, unfinishedMessages: 13)!;

        Assert.Contains("without closing", hint);
        Assert.Contains("13 messages", hint);
        Assert.Contains("/sessions", hint);
        Assert.Contains("4 in this folder", hint);
    }

    /// <summary>With everything closed cleanly there is no alarm to raise — just a pointer.</summary>
    [Fact]
    public void StartupHintIsJustACountWhenNothingWasLeftOpen()
    {
        var hint = SessionHints.Startup(4, unfinishedMessages: null)!;

        Assert.DoesNotContain("without closing", hint);
        Assert.Contains("4 earlier sessions", hint);
        Assert.Contains("/sessions", hint);
    }

    [Fact]
    public void StartupHintReadsCorrectlyForASingleSession()
    {
        var hint = SessionHints.Startup(1, unfinishedMessages: null)!;

        Assert.Contains("1 earlier session in", hint);   // not "sessions"
        Assert.Contains("to see it", hint);              // not "them"
    }

    // --- the exit hint ---

    /// <summary>
    /// PASTEABLE, which is the whole point: the id is an implementation detail everywhere except
    /// this one moment, where it turns "I closed that by accident" into a command.
    /// </summary>
    [Fact]
    public void ExitHintIsACommandTheUserCanRun()
    {
        var hint = SessionHints.Exit("XNND4VH6W0GR0701KZXC5H9Q");

        Assert.Contains("cxagent --resume XNND4V", hint);
        Assert.DoesNotContain("[", hint);   // a bare terminal, after the TUI has released it
    }
}

/// <summary>
/// What the terminal shows on the way out.
///
/// <para>THE CASE THAT LOOKED LIKE A BUG: opening the app and closing it without taking a turn
/// printed nothing at all. That is correct — no turn means no saved session and no spend — but it
/// reads as a malfunction, and it is what exposed how bare the exit was even in the normal case.</para>
/// </summary>
public class FarewellTests
{
    // NOTHING HAPPENED, NOTHING SAID. A summary of zeros beside "resume nothing" is worse than
    // silence: it reports a session that never existed.
    [Fact]
    public void ALaunchThatTookNoTurn_SaysNothing()
    {
        Assert.Null(SessionHints.Farewell(uid: null, spend: null));
    }

    // SPEND ALONE IS WORTH PRINTING, even when nothing was saved to resume — a turn that ran and was
    // never persisted still cost something the user paid for.
    [Fact]
    public void SpendWithoutASession_StillReportsTheCost()
    {
        var text = SessionHints.Farewell(null, new SessionSpend(1500, 1200, 300, 0))!;

        Assert.Contains("1,500 tokens", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--resume", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionWithSpend_ReportsBoth()
    {
        var text = SessionHints.Farewell("XNND4VH6W0GR0701KZXC5H9Q",
            new SessionSpend(2_000_000, 1_900_000, 100_000, 1_500_000)
            {
                CacheHitRate = 0.94,
                Cost = 3.25m,
            })!;

        Assert.Contains("2,000,000 tokens", text, StringComparison.Ordinal);
        Assert.Contains("94% cache", text, StringComparison.Ordinal);
        Assert.Contains("1,500,000 in sub-agents", text, StringComparison.Ordinal);
        Assert.Contains("$3.25", text, StringComparison.Ordinal);
        Assert.Contains("--resume XNND4V", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONLY WHAT IS KNOWN. A null cache rate means the provider reports none and a null cost means no
    /// price is configured — printing "0% cache" or "$0.00" would be a number the user cannot act on
    /// and cannot distinguish from a real zero.
    /// </summary>
    [Fact]
    public void UnknownCacheAndCost_AreOmitted_NotPrintedAsZero()
    {
        var text = SessionHints.Farewell("ABCDEF0123456789",
            new SessionSpend(900, 700, 200, 0))!;

        Assert.DoesNotContain("cache", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sub-agents", text, StringComparison.Ordinal);
    }

    // NO MARKUP. This is written after the TUI released the terminal, so nothing is left to interpret
    // tags — they would print literally.
    [Fact]
    public void TheFarewellCarriesNoMarkup()
    {
        var text = SessionHints.Farewell("ABCDEF0123456789",
            new SessionSpend(900, 700, 200, 0) { CacheHitRate = 0.5, Cost = 1m })!;

        Assert.DoesNotContain("[", text, StringComparison.Ordinal);
    }
}
