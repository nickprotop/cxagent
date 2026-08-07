using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// The startup trust question (Task 2.5): "ask at first, if the current folder is trusted." Two
/// buttons, no third path and no cancel (both answers proceed, one of them just noisily) — rides
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

    public TrustQuestionControl(string workingDir) => _workingDir = workingDir;

    /// <summary>True = trusted, false = "don't trust". Resolves once, on the first button click.</summary>
    public Task<bool> Completion => _tcs.Task;

    public IWindowControl BuildContent()
    {
        var panel = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        var markup = Ctl.Markup();
        markup.AddLine("[bold]Trust this folder?[/]");
        markup.AddLine(SharpConsoleUI.Parsing.MarkupParser.Escape(_workingDir));
        markup.AddLine("Trusted folders allow in-boundary file reads and writes without asking "
            + "each time. Shell commands and anything outside this folder always ask, either way.");
        panel.AddControl(markup.WithMargin(1, 1, 1, 1).Build());

        AddButton(panel, "Trust this folder", true);
        AddButton(panel, "Don't trust — ask before everything", false);

        return panel;
    }

    private void AddButton(ScrollablePanelControl panel, string label, bool trusted)
    {
        // Escaped for the same reason as PermissionPromptControl's buttons: an unescaped '[' would
        // render as an empty row rather than error (P7/ChoiceStepContent precedent) — moot for this
        // control's fixed labels today, but keeping the same escaping discipline costs nothing and
        // avoids a silent trap if the wording ever grows a variable.
        var btn = Ctl.Button(SharpConsoleUI.Parsing.MarkupParser.Escape($"[ {label} ]"))
            .WithMargin(1, 0, 1, 0).Build();
        btn.Click += (_, _) => _tcs.TrySetResult(trusted);
        panel.AddControl(btn);
    }
}
