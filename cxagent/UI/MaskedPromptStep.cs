using System;
using System.Threading.Tasks;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Flows;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// A flow-step content that collects a single line of masked text (e.g. an API key). The framework's
/// <c>FlowContext.Prompt</c> has no masking parameter, so this wraps a <see cref="PromptControl"/>
/// directly, setting <see cref="PromptControl.MaskCharacter"/> so typed characters are not echoed
/// in plain text.
/// </summary>
public sealed class MaskedPromptStep : IFlowStepContent<string>
{
    private readonly string _message;
    private readonly char _mask;
    private readonly TaskCompletionSource<string?> _tcs = new();
    private PromptControl? _prompt;
    private string _currentText = string.Empty;

    /// <summary>
    /// Initializes a new <see cref="MaskedPromptStep"/>.
    /// </summary>
    /// <param name="message">The prompt text shown to the user (also used as the control's prompt label).</param>
    /// <param name="mask">The character used to mask typed input. Defaults to <c>*</c>.</param>
    public MaskedPromptStep(string message, char mask = '*')
    {
        _message = message;
        _mask = mask;
    }

    /// <inheritdoc/>
    public Task<string?> Completion => _tcs.Task;

    /// <inheritdoc/>
    public event Action? StateChanged;

    /// <inheritdoc/>
    public IWindowControl BuildContent(FlowChrome chrome)
    {
        if (_prompt != null)
            return _prompt;

        var prompt = Ctl.Prompt(_message)
            .WithName("masked-prompt-input")
            .WithMaskCharacter(_mask)
            .Build();

        prompt.Entered += (_, text) => _tcs.TrySetResult(text);

        // Mutate state THEN raise StateChanged, per the interface's contract, so the host can
        // re-evaluate button enable-predicates.
        prompt.InputChanged += (_, text) =>
        {
            _currentText = text;
            StateChanged?.Invoke();
        };

        _prompt = prompt;
        return _prompt;
    }
}
