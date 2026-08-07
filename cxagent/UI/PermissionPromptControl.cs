using CxAgent.Core.Permissions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>The user's answer to a <see cref="PermissionPromptControl"/>. <see cref="TrustFolder"/>
/// (Task 2.5) backs a fourth button offered only when the control is constructed with
/// <c>offerTrust: true</c> — the anti-nag way back from a persisted "Don't trust".</summary>
public enum PermissionChoice { Once, Always, Deny, TrustFolder }

/// <summary>
/// A permission prompt presented as a kind-specific heading, the request's <see cref="PermissionRequest.Display"/>
/// verbatim (markup-escaped), and a row of self-resolving buttons whose labels disclose their
/// scope. Follows <see cref="ChoiceStepContent"/>'s proven shape (markup header + buttons +
/// <see cref="TaskCompletionSource{T}"/>), but is a plain <see cref="IWindowControl"/> factory
/// rather than an <c>IFlowStepContent</c> — this is not a wizard step, it rides the
/// <see cref="MainWindow.ShowPermissionPrompt"/> composer-swap seam instead.
///
/// <para>Only the control and the swap are built here (Task 3). Nothing wires this to the gate or
/// makes it appear at runtime — that is Task 4.</para>
/// </summary>
public sealed class PermissionPromptControl
{
    // Past this many characters the Display is elided rather than shown in full — a shell command
    // or path can be arbitrarily long, and painting thousands of characters into the heading is
    // both unreadable and a layout risk. The RULE and the EXECUTED command always use the full,
    // untruncated string (AlwaysRule / Display on PermissionRequest); only the on-screen heading
    // is shortened.
    private const int MaxDisplayChars = 2000;

    private readonly TaskCompletionSource<PermissionChoice> _tcs = new();
    private readonly PermissionRequest _request;
    private readonly bool _offerTrust;

    public PermissionPromptControl(PermissionRequest request, bool offerTrust = false)
    {
        _request = request;
        _offerTrust = offerTrust;
    }

    /// <summary>Resolves once, on the first button click. A second click (double-click) is a
    /// no-op — <see cref="TaskCompletionSource{T}.TrySetResult"/> silently ignores it.</summary>
    public Task<PermissionChoice> Completion => _tcs.Task;

    /// <summary>
    /// Resolves <see cref="Completion"/> to <see cref="PermissionChoice.Deny"/> if nothing has
    /// answered it yet (Task 4): the caller that cancelled owns going away, not this control, so a
    /// cancelled goal must make the control itself finish rather than leave it awaiting a click
    /// that will never come. Safe to call from a background thread (only touches the TCS) and safe
    /// to race against a real click — whichever resolves first wins, the loser is a silent no-op,
    /// same as a double-click.
    /// </summary>
    public void TryCancel() => _tcs.TrySetResult(PermissionChoice.Deny);

    /// <summary>
    /// The heading must state the REAL reason we are asking, because a file request reaches this
    /// prompt for two different reasons and telling the user the wrong one teaches them to stop
    /// reading the text. A path is asked about either because it is outside the working folder,
    /// or because it is INSIDE an untrusted folder (untrusted removes the implicit silent class —
    /// Task 2.5). <paramref name="inBoundary"/> distinguishes them: it is exactly the condition
    /// the gate already computed to decide whether to offer the Trust button, so no new plumbing.
    ///
    /// Found by the live drive: an in-tree `notes.txt` in an untrusted folder was announced as
    /// "Write a file outside the working folder?", which is simply false.
    /// </summary>
    private static string HeadingFor(PermissionKind kind, bool inBoundary) => kind switch
    {
        PermissionKind.Shell => "Run shell command?",
        PermissionKind.FileRead => inBoundary
            ? "Read a file in this (untrusted) folder?"
            : "Read a file outside the working folder?",
        PermissionKind.FileWrite => inBoundary
            ? "Write a file in this (untrusted) folder?"
            : "Write a file outside the working folder?",
        PermissionKind.Http => "Allow an HTTP request?",
        _ => "Allow this action?",
    };

    /// <summary>
    /// Builds the panel: bolded heading, the escaped (and possibly elided) Display, then one
    /// button per offered choice. No Esc handling — a verified global-shortcut conflict
    /// (AppBootstrap.cs:420) rules it out; Deny is the explicit escape hatch instead.
    /// </summary>
    public IWindowControl BuildContent()
    {
        var panel = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        var displayed = _request.Display.Length > MaxDisplayChars
            ? _request.Display[..MaxDisplayChars] + " …(truncated)"
            : _request.Display;

        var markup = Ctl.Markup();
        markup.AddLine($"[bold]{SharpConsoleUI.Parsing.MarkupParser.Escape(HeadingFor(_request.Kind, _offerTrust))}[/]");
        // One AddLine per source line, same reasoning as ChoiceStepContent: a multi-line Display
        // (e.g. a shell command plus its working_dir) would otherwise paint its "\n" literally.
        foreach (var line in displayed.Replace("\r\n", "\n").Split('\n'))
            markup.AddLine(SharpConsoleUI.Parsing.MarkupParser.Escape(line));
        panel.AddControl(markup.WithMargin(1, 1, 1, 1).Build());

        AddChoiceButton(panel, "Allow once", PermissionChoice.Once);

        if (_request.AlwaysRule is not null)
            AddChoiceButton(panel, $"Always allow: {_request.AlwaysRule}", PermissionChoice.Always);

        if (_offerTrust)
            AddChoiceButton(panel, "Trust this folder", PermissionChoice.TrustFolder);

        AddChoiceButton(panel, "Deny", PermissionChoice.Deny);

        return panel;
    }

    private void AddChoiceButton(ScrollablePanelControl panel, string label, PermissionChoice choice)
    {
        // Escape the LABEL, not just the heading above. An unescaped '[' — inevitable once
        // AlwaysRule is a shell command or path — renders the button as an EMPTY ROW rather than
        // erroring (ChoiceStepContent.cs:54-64, found live on the F8 provider list). The full
        // label including the raw AlwaysRule is still what gets compared/stored; only the
        // button's rendered Text is escaped.
        var btn = Ctl.Button(SharpConsoleUI.Parsing.MarkupParser.Escape($"[ {label} ]"))
            .WithMargin(1, 0, 1, 0).Build();
        btn.Click += (_, _) => _tcs.TrySetResult(choice);
        panel.AddControl(btn);
    }
}
