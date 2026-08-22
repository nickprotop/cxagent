using CxAgent.Core.Permissions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Themes;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>The user's answer to a <see cref="PermissionPromptControl"/>. <see cref="TrustFolder"/>
/// (Task 2.5) backs a fourth button offered only when the control is constructed with
/// <c>offerTrust: true</c> — the anti-nag way back from a persisted "Don't trust".</summary>
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
    private static string HeadingTextFor(PermissionKind kind, bool inBoundary, bool refusedByClassifier) => kind switch
    {
        PermissionKind.Shell => "Run shell command?",

        // THE CLASSIFIER'S REFUSAL IS ITS OWN REASON, and naming it matters most here: in auto mode
        // the folder is usually TRUSTED — that is a precondition for the classifier running at all —
        // so the "(untrusted)" wording below was telling a user the opposite of what happened. The
        // heading is the one line meant to explain why they are being asked.
        PermissionKind.FileRead when refusedByClassifier => "Read a file — auto review said ask?",
        PermissionKind.FileWrite when refusedByClassifier => "Write a file — auto review said ask?",

        PermissionKind.FileRead => inBoundary
            ? "Read a file in this (untrusted) folder?"
            : "Read a file outside the working folder?",
        PermissionKind.FileWrite => inBoundary
            ? "Write a file in this (untrusted) folder?"
            : "Write a file outside the working folder?",
        PermissionKind.Http => "Allow an HTTP request?",

        // NAMES WHAT IT ACTUALLY IS. Falling through to the generic "Allow this action?" would ask
        // someone to approve a call into code we cannot inspect without telling them that is what
        // they are doing — the one fact that should change how carefully they read the rest.
        PermissionKind.Mcp => "Run a tool on an external MCP server?",

        _ => "Allow this action?",
    };

    // Past this many characters the classifier's reason is elided in the heading. The reason is
    // MODEL-AUTHORED TEXT about attacker-influenced content (see PermissionRequest.ClassifierReason)
    // — nothing stops a compromised or merely verbose model from writing paragraphs. Unbounded, a
    // long reason would push the actual subject (the command/path/origin the user is being asked
    // about) off the visible prompt, which is a security regression: the reason is a hint, the
    // subject is what they are approving. 160 chars is a long clause, not a paragraph — enough for
    // "sends 4.2 KB to an origin this project has never used" with room to spare, short enough that
    // the heading stays one wrapped line rather than competing with the subject below it.
    private const int MaxReasonChars = 160;

    /// <summary>
    /// The full heading text for a request, INCLUDING the classifier's reason when it gave one —
    /// this is the public, request-shaped seam a test (or any other reader) uses instead of the
    /// kind/bool triple above. The decision stays the user's; this only tells them what the model
    /// that flagged the request said about it.
    ///
    /// <para>NOT MARKUP-ESCAPED HERE. Callers that render this into a markup pane (BuildContent
    /// below) must escape the whole returned string themselves, the same as they already do for
    /// the kind-only heading — escaping twice would turn a literal "[red]" in a reason into
    /// visible "\[red\]" rather than plain text. A caller that only wants the plain text (tests)
    /// gets it unescaped, which is what "Contains" assertions need.</para>
    ///
    /// <para>FAILS TO THE PLAIN HEADING. No verdict, RefusedByClassifier false, or a null/blank
    /// reason — all read as "nothing to add", and the return is byte-identical to
    /// <see cref="HeadingTextFor"/> alone. A classifier that is down must cost a plainer prompt,
    /// never a wrong one.</para>
    /// </summary>
    public static string HeadingFor(PermissionRequest request, bool offerTrust = false)
    {
        var heading = HeadingTextFor(request.Kind, offerTrust, request.RefusedByClassifier);

        if (!request.RefusedByClassifier || request.ClassifierReason is not { Length: > 0 } reason)
            return heading;

        var clipped = reason.Length > MaxReasonChars
            ? reason[..MaxReasonChars] + "…"
            : reason;

        return $"{heading}  — {clipped}";
    }

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

        // ELEVATED, a step above the composer this replaces. A permission prompt is the one moment
        // the app stops and asks, and it has to read as a different plane rather than as more
        // content in the column. The previous answer dimmed the whole transcript behind it, which
        // changed a screenful of state to say one thing and drew its edge from the prompt's own
        // laid-out height. Raising this surface says it locally, with nothing else moving.
        panel.BackgroundColor = ColorScheme.PromptSurface;

        var displayed = _request.Display.Length > MaxDisplayChars
            ? _request.Display[..MaxDisplayChars] + " …(truncated)"
            : _request.Display;

        var markup = Ctl.Markup();
        markup.AddLine($"[bold]{SharpConsoleUI.Parsing.MarkupParser.Escape(HeadingFor(_request, _offerTrust))}[/]");

        // WHO IS ASKING, when it is not the session's own agent.
        //
        // OBSERVED ON A LIVE DRIVE: a child spawned to analyse a test failure asked to run shell
        // commands repeatedly, and the prompt was INDISTINGUISHABLE from the parent asking — same
        // heading, same command, nothing saying a delegated agent wanted it. The user is being asked
        // to take responsibility for a command, and "which agent decided to run this" changes the
        // answer: a command the user's own request implies is different from one a sub-agent
        // invented three delegation steps away.
        //
        // ON ITS OWN LINE, under the heading rather than folded into it. The heading names the KIND
        // of thing being allowed and is what someone reads first; requester is a qualifier, and
        // rewriting the heading per requester would give the same action two different names.
        //
        // NOTHING IS ADDED for the parent — see PermissionRequest.Requester for why an unattributed
        // prompt is correct there rather than merely convenient.
        if (!string.IsNullOrWhiteSpace(_request.Requester))
            markup.AddLine($"[{ColorScheme.ThinkingMarkup}]asked for by: "
                         + $"{SharpConsoleUI.Parsing.MarkupParser.Escape(_request.Requester!)}[/]");
        // One AddLine per source line, same reasoning as ChoiceStepContent: a multi-line Display
        // (e.g. a shell command plus its working_dir) would otherwise paint its "\n" literally.
        foreach (var line in displayed.Replace("\r\n", "\n").Split('\n'))
            markup.AddLine(SharpConsoleUI.Parsing.MarkupParser.Escape(line));

        // WHAT "ALWAYS" WOULD ACTUALLY COVER, in the BODY rather than the button. The label says
        // "Always allow" and reads as "stop asking about this sort of thing" — while a shell rule is
        // the EXACT command string, so the next call, one flag different, prompts again. A user who
        // grants Always on `find . -type f` and is asked about `find . -name '*.cs'` a second later
        // concludes the button does not work.
        //
        // NOT IN THE BUTTON. That was tried: "Always allow: <the whole command>" made the control
        // as wide as the dialog and pushed the row off screen, so the answer to a question you could
        // read was a control you could not. The body already wraps and already holds the command.
        if (_request.AlwaysRule is { Length: > 0 } rule)
        {
            markup.AddLine(string.Empty);
            markup.AddLine($"[{ColorScheme.MutedMarkup}]Always allow covers: "
                         + $"{SharpConsoleUI.Parsing.MarkupParser.Escape(Clip(rule))}[/]");
        }

        panel.AddControl(markup.WithMargin(1, 1, 1, 1).Build());

        // A BLANK LINE between the question and the controls that answer it, so the eye reads
        // "what is being asked" and "what I can do" as two things rather than one paragraph.
        panel.AddControl(Ctl.Markup().AddLine(string.Empty).Build());

        // The rule the family draws above a button row (cxpost puts one in every dialog it has).
        panel.AddControl(Ctl.RuleBuilder()
            .WithColorRole(ColorScheme.Structure)
            .WithMargin(1, 0, 1, 0)
            .Build());

        var toolbar = Ctl.Toolbar()
            .WithSpacing(2)
            .WithAlignment(HorizontalAlignment.Center)
            .WithMargin(1, 0, 1, 0);

        // ROLES, NOT LITERAL COLOURS: Success/Warning/Danger resolve through the active theme, so
        // the buttons stay legible if the theme changes and the meanings stay tied to the framework's
        // vocabulary rather than to three hex values chosen here.
        toolbar.AddButton(ChoiceButton("Allow once", ColorScheme.Affirmative, PermissionChoice.Once));

        // A FIXED LABEL, NOT "Always allow: <the whole command>". The command is already displayed,
        // in full, three lines above — repeating it makes the button as wide as the dialog and
        // pushes the row off screen, so the answer to a question you can read becomes a control you
        // cannot.
        if (_request.AlwaysRule is not null)
            toolbar.AddButton(ChoiceButton("Always allow", ColorScheme.Caution, PermissionChoice.Always));

        if (_offerTrust)
            toolbar.AddButton(ChoiceButton("Trust this folder", ColorScheme.Caution,
                PermissionChoice.TrustFolder));

        toolbar.AddButton(ChoiceButton("Deny", ColorScheme.Destructive, PermissionChoice.Deny));

        panel.AddControl(toolbar.Build());
        return panel;
    }

    /// <summary>
    /// One choice as a borderless button carrying its semantic role.
    ///
    /// <para>NO BORDER, so the whole row is ONE LINE. A rounded border is three rows tall — top
    /// edge, label, bottom edge — and four of them turned the answer to a one-line question into a
    /// block deeper than the question itself, on a prompt that already sits under a heading, the
    /// command, a blank line and a rule. The role still carries the meaning; the box added height,
    /// not information.</para>
    ///
    /// <para>Labels are FIXED STRINGS, so the escaping here is currently unreachable. It stays
    /// anyway: the cost is one call, and the failure it guards against is invisible rather than loud
    /// — an unescaped '[' from a shell command interpolated into a label renders the button as an
    /// empty row.</para>
    /// </summary>
    /// <summary>
    /// The rule text, bounded. A shell rule is the whole command and a command can be a paragraph;
    /// this line exists to correct an expectation, not to reproduce what is already displayed above.
    /// </summary>
    private static string Clip(string text) =>
        text.Length > 120 ? text[..120].TrimEnd() + "…" : text;

    private ButtonControl ChoiceButton(string label, ColorRole role, PermissionChoice choice)
    {
        var btn = Ctl.Button(SharpConsoleUI.Parsing.MarkupParser.Escape(label))
            .WithColorRole(role)
            .WithBorder(ButtonBorderStyle.None)
            .Build();
        btn.Click += (_, _) => _tcs.TrySetResult(choice);
        return btn;
    }
}
