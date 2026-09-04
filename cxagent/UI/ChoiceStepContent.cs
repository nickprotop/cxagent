using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Flows;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// A one-of-N choice presented as a button per option, self-resolving on click. The framework's
/// primitive step contents are internal and <c>FlowContext</c> has no choice verb, so the wizard
/// supplies its own; presented via <c>ctx.Show(..., FlowButtons.None)</c> since it resolves itself.
/// </summary>
internal sealed class ChoiceStepContent : IFlowStepContent<string>
{
    private readonly TaskCompletionSource<string?> _tcs = new();
    private readonly string _prompt;
    private readonly IReadOnlyList<string> _choices;

    public ChoiceStepContent(string prompt, IReadOnlyList<string> choices)
    {
        _prompt = prompt;
        _choices = choices;
    }

    /// <inheritdoc/>
    public Task<string?> Completion => _tcs.Task;

    /// <inheritdoc/>
    public event Action? StateChanged;

    /// <inheritdoc/>
    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var panel = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        // One AddLine per source line: a multi-line prompt (the final summary) would otherwise paint
        // its "\n" literally. The first line is the question/heading and is bolded; any remaining
        // lines are detail and stay plain.
        var markup = Ctl.Markup();
        var lines = _prompt.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var esc = SharpConsoleUI.Parsing.MarkupParser.Escape(lines[i]);
            markup.AddLine(i == 0 ? $"[bold]{esc}[/]" : esc);
        }
        panel.AddControl(markup.WithMargin(1, 1, 1, 1).Build());

        foreach (var choice in _choices)
        {
            var capture = choice;
            // Escape the LABEL, not just the prompt above. A choice like "[ Add provider… ]" is
            // otherwise read as markup and rendered as an EMPTY ROW — the button still works and is
            // still selectable, so nothing errors; the entry is simply invisible. P7's live drive
            // found this on the provider list, where "[ Add provider… ]" and "[ Set default… ]"
            // both vanished, leaving a user no discoverable way to add a provider at all. The same
            // bracketed convention is used by RoleEditor's "[ New role… ]" and ModelPicker's
            // "[ Filter… ]", so all three were affected. `capture` stays UNESCAPED because it is the
            // value compared against and returned to the caller — only the rendering is escaped.
            var btn = Ctl.Button(SharpConsoleUI.Parsing.MarkupParser.Escape(capture))
                .WithMargin(1, 0, 1, 0).Build();
            btn.Click += (_, _) =>
            {
                // Mutate state THEN raise, per the IFlowStepContent contract (IFlowStepContent.cs):
                // the host re-evaluates dynamic button enable-predicates when StateChanged fires, so
                // it must observe the settled state. Resolving the TCS IS this content's state change,
                // hence it goes first. Inert while presented with FlowButtons.None (no host button row
                // consumes StateChanged), but wrong ordering here is a live bug the moment this is
                // reused with a real button row — cf. MaskedPromptStep, which orders it the same way.
                _tcs.TrySetResult(capture);
                StateChanged?.Invoke();
            };
            panel.AddControl(btn);
        }

        return panel;
    }
}
