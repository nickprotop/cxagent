using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// The startup trust question: "ask at first, if the current folder is trusted." Two answers, no
/// third path and no cancel (both answers proceed, one of them just noisily) — rides
/// the same <see cref="MainWindow.ShowPermissionPrompt"/>/<see cref="MainWindow.RestoreComposer"/>
/// composer-swap seam <see cref="PermissionPromptControl"/> uses, since both are just
/// <see cref="IWindowControl"/> as far as that seam cares.
///
/// <para>Same <c>BuildContent()</c>-once contract as <see cref="PermissionPromptControl"/>: a
/// second call returns a NEW control, and <see cref="GridControl.ReplaceControl"/> matches the
/// "old" control by reference — build once, hold the reference, reuse it for the matching
/// Restore.</para>
/// </summary>
public sealed class TrustQuestionControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly string _workingDir;
    private ListControl? _list;

    public TrustQuestionControl(string workingDir) => _workingDir = workingDir;

    /// <summary>True = trusted, false = "don't trust". Resolves once, on the first answer.</summary>
    public Task<bool> Completion => _tcs.Task;

    public IWindowControl BuildContent()
    {
        var panel = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        var markup = Ctl.Markup();
        markup.AddLine("[bold]Trust this folder?[/]");
        markup.AddLine(SharpConsoleUI.Parsing.MarkupParser.Escape(_workingDir));
        // WHAT TRUST ACTUALLY BUYS, kept current. This said "shell commands … always ask, either
        // way" — true when it was written and false since read-only commands stopped prompting.
        // A consent screen that overstates its own restraint is the worst kind to leave stale: the
        // user grants something broader than the words they read.
        markup.AddLine("Trusted folders allow file reads and writes inside this folder, and "
            + "commands that only read — ls, cat, grep — without asking each time.");
        markup.AddLine("Anything that can write, and anything outside this folder, still asks.");
        panel.AddControl(markup.WithMargin(1, 1, 1, 1).Build());

        // THE CAUTIOUS ANSWER FIRST, because the list opens with the first answer highlighted and in
        // the composer's cell — where the user's next Enter is very often already in flight for
        // something else. Whichever answer sits here is what a stray keystroke chooses, and the two
        // are not equally recoverable: "don't trust" costs prompts and is reversible from any file
        // prompt or /trust, while an accidental "trust" silently grants every read and write in the
        // folder and does not ask again.
        //
        // A LIST, NOT TWO BUTTONS. Stacked buttons sat in a panel that uses Up and Down only to
        // scroll, so Down moved nothing and Enter still answered the first — "don't trust", twice on a
        // live drive, from someone who had pressed Down to reach "trust". A list moves with the
        // arrows and takes the highlighted answer on Enter, as the model's own questions do.
        //
        // NO HOVER HIGHLIGHT. A list draws the row under the mouse above the selected one, so a pointer
        // resting on "Don't trust" lit it while Enter would answer "trust". On a consent question the
        // only highlight is the answer Enter takes.
        //
        // AND A CARET ON THE CHOSEN ANSWER, the palette's ▸. The list's own colours leave the selected
        // row a slightly lighter grey and tint the other one — so the screen showed the answer Enter
        // would NOT take as the lit one. A character says it under every theme.
        var list = Ctl.List().WithHoverHighlighting(false).Build();
        list.Items.Add(new ListItem(""));
        list.Items.Add(new ListItem(""));
        list.SelectedIndexChanged += (_, _) => MarkChosen(list);
        list.SelectedIndex = 0;
        MarkChosen(list);
        list.ItemActivated += (_, _) => SubmitFromList();
        _list = list;
        panel.AddControl(list);

        return panel;
    }

    private static readonly string[] Answers = ["Don't trust — ask before everything", "Trust this folder"];

    private static void MarkChosen(ListControl list)
    {
        for (var i = 0; i < Answers.Length; i++)
            list.Items[i].Text = (i == list.SelectedIndex ? "▸ " : "  ") + Answers[i];
    }

    /// <summary>
    /// Takes the highlighted answer, as Enter on the list does.
    ///
    /// <para>A TEST SEAM as well as the Enter handler: <c>ListControl.ProcessKey</c> refuses keys
    /// unless it has focus in a live window, so a test chooses by index and calls this.</para>
    /// </summary>
    public void SubmitFromList()
    {
        if (_list is { SelectedIndex: >= 0 } list) _tcs.TrySetResult(list.SelectedIndex == 1);
    }
}
