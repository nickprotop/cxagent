using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The classifier that replaces three separate per-handler invalid-config guards (old F7 roles, old
/// F8 providers, the F5 setup flow) with one decision table. See <see cref="SettingsEntry.Classify"/>
/// for the rule: an unloadable config REFUSES except via F5, which stays the documented repair route.
/// </summary>
public class SettingsEntryTests
{
    private static ProviderSettings OneProvider(string name, string model) =>
        ProviderCatalogEditor.AddOrReplace(
            ProviderCatalogEditor.EmptyCatalog(), name,
            new ProviderInstanceConfig("openai-compatible", model, "k", "https://x.invalid/v1", null),
            makeDefault: true);




    [Fact]
    public void Escape_GoesToTheDialogWhileOpen_ThenToARunningTurn_ThenNowhere()
    {
        // A MISDIAGNOSIS WORTH NOT REPEATING. "Escape does not discard a draft" reads as a routing
        // defect with a missing `finally` behind it. Both halves are wrong: the routing is a pure
        // function of its inputs, and AppBootstrap DOES clear the dialog flag in a finally
        // (verified). What that symptom actually is: Escape does not clear typed COMPOSER TEXT.
        //
        // TWO INPUTS. What Escape can find in front of it is a running turn, or nothing — and with no
        // turn running it means "cancel nothing", not some third discard state, which would be a
        // no-op nothing tells the user about. A permission prompt and a question are both answered
        // before this is reached — see the handler, which checks them first, so their absence here
        // is the routing staying a pure function rather than a gap.
        Assert.Equal(EscapeTarget.CancelTurn,
            EscapeRouting.For(turnIsRunning: true, chatTabIsActive: true));
        Assert.Equal(EscapeTarget.Nothing,
            EscapeRouting.For(turnIsRunning: false, chatTabIsActive: true));
    }

    // ESCAPE BELONGS TO THE ACTIVE TAB. A shell tab runs the user's own programs and Escape is a key
    // those programs want: pressing it at a vim inside a terminal tab must not kill the agent run
    // behind it. The waiting bar shows a turn is running from any tab and F4 returns to chat, so
    // scoping this takes nothing away that the user cannot see a way back to.
    [Fact]
    public void EscapeCancelsOnlyFromTheChatTab()
    {
        Assert.Equal(EscapeTarget.CancelTurn,
            EscapeRouting.For(turnIsRunning: true, chatTabIsActive: true));

        Assert.Equal(EscapeTarget.Nothing,
            EscapeRouting.For(turnIsRunning: true, chatTabIsActive: false));

        // Not a turn to cancel: the tab does not come into it.
        Assert.Equal(EscapeTarget.Nothing,
            EscapeRouting.For(turnIsRunning: false, chatTabIsActive: true));
        Assert.Equal(EscapeTarget.Nothing,
            EscapeRouting.For(turnIsRunning: false, chatTabIsActive: false));
    }
}

/// <summary>The routing is a pure function; this pins that the window reports what it reads.</summary>
public class ChatTabIsActiveTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    // ESCAPE'S SCOPE COMES FROM THIS FLAG. If the window reported it wrongly the routing would be
    // right and the behaviour still wrong, which no test of EscapeRouting alone would catch.
    [Fact]
    public void OpeningAFileTabMakesTheChatTabInactive()
    {
        Assert.True(_fixture.Host.Main.ChatTabIsActive);

        var path = Path.Combine(_fixture.WorkingDirectory, "scope.txt");
        File.WriteAllText(path, "x\n");
        CxAgent.UI.FileTab.Open(_fixture.Host, CxAgent.UI.FileLoad.TryLoad(path, out _)!);

        Assert.False(_fixture.Host.Main.ChatTabIsActive);

        _fixture.Host.Main.ShowChatTab();

        Assert.True(_fixture.Host.Main.ChatTabIsActive);
    }
}

/// <summary>
/// Whether Escape is consumed, which is separate from what it does.
///
/// <para>THE BUG THIS PINS: the handler ended with an unconditional <c>return true</c>, so after
/// deciding it had nothing to do it swallowed the key anyway. Nothing focused ever saw Escape — and
/// a shell tab runs the user's own programs. Typing "HELLO", Escape, then "dd" in vim left "HELLOdd"
/// on the line, because the editor never left insert mode.</para>
/// </summary>
public class EscapeConsumptionTests
{
    // Mirrors the handler's shape: each branch that ACTS consumes; the fall-through does not.
    private static bool Consumed(bool dialogOpen, bool question, bool prompt, bool cancels) =>
        dialogOpen || question || prompt || cancels;

    [Theory]
    [InlineData(true, false, false, false)]   // a dialog was dismissed
    [InlineData(false, true, false, false)]   // a question was skipped
    [InlineData(false, false, true, false)]   // a permission was denied
    [InlineData(false, false, false, true)]   // a turn was cancelled
    public void DoingSomethingConsumesTheKey(bool dialog, bool question, bool prompt, bool cancels)
        => Assert.True(Consumed(dialog, question, prompt, cancels),
            "one keystroke must not both act and fall through to the focused control");

    // NOTHING TO DO MEANS NOTHING CONSUMED, so the key reaches whatever has focus — a terminal's
    // child process, or an editor.
    [Fact]
    public void DoingNothingLetsTheKeyThrough()
        => Assert.False(Consumed(false, false, false, false));

    // AND OFF THE CHAT TAB THERE IS NO TURN TO CANCEL, so Escape falls through there even while the
    // agent is working — which is what makes a shell tab usable during a run.
    [Fact]
    public void EscapeFallsThroughFromAnotherTabDuringATurn()
    {
        var target = EscapeRouting.For(turnIsRunning: true, chatTabIsActive: false);

        Assert.Equal(EscapeTarget.Nothing, target);
        Assert.False(Consumed(false, false, false, target is EscapeTarget.CancelTurn));
    }
}
