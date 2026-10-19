using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The startup trust question is a list: Up/Down move between the two answers and Enter takes the
/// highlighted one. Driven with real keys in a headless window — see <see cref="HeadlessKeys"/>.
/// </summary>
public class TrustQuestionControlTests
{
    private sealed record Asked(TrustQuestionControl Question, ListControl List, HeadlessKeys Keys);

    private static ListControl ListIn(IWindowControl content) =>
        ((ScrollablePanelControl)content).Children.OfType<ListControl>().Single();

    private static Asked Ask()
    {
        var question = new TrustQuestionControl("/repo");
        var content = question.BuildContent();
        var list = ListIn(content);
        return new Asked(question, list, HeadlessKeys.Focus(content, list));
    }

    [Fact]
    public void TheCautiousAnswer_IsFirst_AndHighlighted()
    {
        var (_, list, _) = Ask();

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Contains("Don't trust", list.Items[0].Text);
    }

    /// <summary>Enter on the first answer, as opened, does not trust: a stray Enter is the safe one.</summary>
    [Fact]
    public async Task Enter_AsOpened_DoesNotTrust()
    {
        var (question, _, keys) = Ask();

        Assert.True(keys.Enter());

        Assert.False(await question.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DownThenEnter_Trusts()
    {
        var (question, _, keys) = Ask();

        Assert.True(keys.Down());
        Assert.True(keys.Enter());

        Assert.True(await question.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// Only the selection is drawn. A list's hover outranks its selection, so a pointer resting on
    /// "Don't trust" showed it lit while Enter would answer "trust" — on the one question where the
    /// highlight must not mislead.
    /// </summary>
    [Fact]
    public void TheHighlight_IsTheSelection_NotTheMouse() =>
        Assert.False(Ask().List.HoverHighlightsItems);

    /// <summary>
    /// The chosen answer carries the caret, and it moves with the selection. The list's own colours
    /// drew the OTHER row brighter — the selected one was a slightly lighter grey — so on a live drive
    /// the screen showed "Don't trust" lit while Enter would trust.
    /// </summary>
    [Fact]
    public void TheCaret_FollowsDown()
    {
        var (_, list, keys) = Ask();

        Assert.StartsWith("▸ Don't trust", list.Items[0].Text);
        Assert.StartsWith("  Trust this folder", list.Items[1].Text);

        keys.Down();

        Assert.StartsWith("  Don't trust", list.Items[0].Text);
        Assert.StartsWith("▸ Trust this folder", list.Items[1].Text);
    }

    /// <summary>Writes pass silently only under accept-edits; always-ask asks for them even in a trusted
    /// folder, so a promise of silent writes without the mode is one the folder does not keep.</summary>
    [Fact]
    public void ThePromise_IsTrueInEveryMode()
    {
        var content = (ScrollablePanelControl)new TrustQuestionControl("/repo").BuildContent();
        var text = string.Join("\n", content.Children.OfType<MarkupControl>()
            .Select(m => SharpConsoleUI.Parsing.MarkupParser.Remove(m.Text)));

        Assert.Contains("writes inside this folder go through without asking in accept-edits mode", text, StringComparison.Ordinal);
        Assert.Contains("Commands that can write, and anything outside this folder, still ask.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reads and writes inside this folder", text, StringComparison.Ordinal);
    }
}
