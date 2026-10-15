using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The startup trust question is a list: Up/Down move between the two answers and Enter takes the
/// highlighted one. Two stacked buttons sat in a panel that used the arrows only for scrolling, so
/// Down did nothing and Enter answered "don't trust" — twice, on a live drive.
/// </summary>
public class TrustQuestionControlTests
{
    private static ListControl ListIn(IWindowControl content) =>
        ((ScrollablePanelControl)content).Children.OfType<ListControl>().Single();

    [Fact]
    public void TheCautiousAnswer_IsFirst_AndHighlighted()
    {
        var question = new TrustQuestionControl("/repo");
        var list = ListIn(question.BuildContent());

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Contains("Don't trust", list.Items[0].Text);
    }

    [Fact]
    public async Task ChoosingTheSecond_Trusts()
    {
        var question = new TrustQuestionControl("/repo");
        var list = ListIn(question.BuildContent());

        list.SelectedIndex = 1;
        question.SubmitFromList();

        Assert.True(await question.Completion);
    }

    [Fact]
    public async Task ChoosingTheFirst_DoesNotTrust()
    {
        var question = new TrustQuestionControl("/repo");
        ListIn(question.BuildContent());

        question.SubmitFromList();

        Assert.False(await question.Completion);
    }

    /// <summary>
    /// Only the selection is drawn. A list's hover outranks its selection, so a pointer resting on
    /// "Don't trust" showed it lit while Enter would answer "trust" — on the one question where the
    /// highlight must not mislead.
    /// </summary>
    [Fact]
    public void TheHighlight_IsTheSelection_NotTheMouse() =>
        Assert.False(ListIn(new TrustQuestionControl("/repo").BuildContent()).HoverHighlightsItems);

    /// <summary>
    /// The chosen answer carries the caret, and it moves with the selection. The list's own colours
    /// drew the OTHER row brighter — the selected one was a slightly lighter grey — so on a live drive
    /// the screen showed "Don't trust" lit while Enter would trust.
    /// </summary>
    [Fact]
    public void TheCaret_MarksTheAnswerEnterTakes()
    {
        var list = ListIn(new TrustQuestionControl("/repo").BuildContent());

        Assert.StartsWith("▸ Don't trust", list.Items[0].Text);
        Assert.StartsWith("  Trust this folder", list.Items[1].Text);

        list.SelectedIndex = 1;

        Assert.StartsWith("  Don't trust", list.Items[0].Text);
        Assert.StartsWith("▸ Trust this folder", list.Items[1].Text);
    }
}
