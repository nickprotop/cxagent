using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// The list that appears above the composer when a goal begins with <c>/</c>.
///
/// <para>DISCOVERY, not completion. Tab completion assumes you already know the command exists and
/// only need help typing it; with four commands and no surface but <c>/help</c>, the actual problem
/// is knowing what there IS. A menu that opens on the slash answers that without being asked.</para>
///
/// <para>A DESKTOP PORTAL rather than a control in the grid, because the list has to float above the
/// transcript: laid out in the composer's cell it would be clipped to three rows, and giving the
/// composer room for it would move the prompt every time you typed a slash.</para>
///
/// <para>THE CONTENT HANDLES ITS OWN KEYS, and it has to. An open desktop portal CAPTURES keyboard
/// input (InputCoordinator: the top portal's content gets the key, then global shortcuts, then the
/// key is dropped) — so PreviewKeyPressed, where cxagent claims Enter, is never reached. A portal
/// whose content is not an IInteractiveControl therefore swallows every keystroke silently, which is
/// exactly what a MarkupControl did here: the menu appeared and the next character vanished.</para>
///
/// <para>Typing is FORWARDED BACK to the composer rather than handled here, so the filter narrows as
/// the user keeps typing — the portal is capturing the keys that were meant for the box below it.</para>
/// </summary>
public sealed class CommandMenu
{
    private readonly ConsoleWindowSystem _system;
    private readonly Window _window;
    private readonly IWindowControl _owner;

    private DesktopPortal? _portal;
    private CommandMenuContent? _list;
    /// <summary>
    /// One row, whichever mode the menu is in.
    /// </summary>
    /// <param name="Label">What is shown in the name column — <c>/mcp</c> or <c>reload</c>.</param>
    /// <param name="Summary">The line beside it.</param>
    /// <param name="Completion">
    /// What the composer becomes when this row is chosen, or null when it cannot be completed. Null
    /// is a PLACEHOLDER row (<c>&lt;server&gt;</c>): shown so the argument is discoverable, but the
    /// value is the user's to type and filling the composer with angle brackets would be nonsense.
    /// </param>
    /// <summary>
    /// What choosing an argument row should put in the composer, or null when it should not.
    ///
    /// <para>A ROW THAT CARRIES ITS PLACEHOLDER COMPLETES TO THE VERB IN FRONT OF IT. `show &lt;name&gt;`
    /// and `login &lt;name&gt;` were unselectable: marked non-completing, so the one way to reach the
    /// server list behind them was to type the verb by hand — the list worked, but only for someone
    /// who already knew the word the palette was showing them.</para>
    ///
    /// <para>Completing the WHOLE name would put "&lt;name&gt;" in the composer literally, which is the
    /// failure the non-completing flag exists to prevent. Completing the part before the placeholder
    /// leaves a trailing space, which is exactly what makes the palette open its next level and offer
    /// the live values.</para>
    ///
    /// <remarks>Public so it is testable without a live ConsoleWindowSystem — the same seam
    /// <c>ChatTranscriptSink.Escape</c> uses.</remarks>
    /// </summary>
    public static string? CompletionFor(CommandArgument argument, string prefix)
    {
        var bracket = argument.Name.IndexOf('<');
        if (bracket > 0) return $"{prefix} {argument.Name[..bracket]}";

        return argument.Completes ? $"{prefix} {argument.Name}" : null;
    }

    private readonly record struct Row(string Label, string Summary, string? Completion);

    private IReadOnlyList<Row> _shown = [];
    private int _selected;
    /// <summary>Most rows the menu will show. Past this the list wants scrolling, which this is not.</summary>
    private const int MaxRows = 10;

    private bool _suppressUntilEdit;
    private int _rowsWhenOpened;
    private string _chosenText = string.Empty;

    public CommandMenu(ConsoleWindowSystem system, Window window, IWindowControl owner)
    {
        _system = system;
        _window = window;
        _owner = owner;
    }

    /// <summary>
    /// Where live argument rows come from — the instances for <c>/model</c>, the sessions for
    /// <c>/sessions resume</c>.
    ///
    /// <para>PER MENU, NOT PER PROCESS. This was a mutable static on <see cref="SessionCommands"/>,
    /// which is fine while exactly one session exists and a latent bug the moment a second one does:
    /// two menus would overwrite each other's source and each offer the other's rows.</para>
    /// </summary>
    public Func<string, IReadOnlyList<CommandArgument>>? Values { get; set; }

    /// <summary>Whether the menu is currently on screen.</summary>
    public bool IsOpen => _portal is not null;

    /// <summary>
    /// The text the composer should become. A STRING RATHER THAN A COMMAND, because a row is now
    /// either a command or one of its arguments, and the only thing the consumer ever used was the
    /// text to insert.
    /// </summary>
    public event EventHandler<string>? Chosen;

    /// <summary>
    /// Reconsiders the menu for what is now in the composer — opens it, refilters it, or closes it.
    /// </summary>
    /// <remarks>
    /// <para>ONLY A LEADING SLASH ON A SINGLE LINE. A slash mid-text is a path, not a command, and a
    /// slash on line three of a continued goal is prose the user is still writing. This is the same
    /// rule <see cref="SessionCommands.Match"/> applies to dispatch, so the menu cannot offer
    /// something the dispatcher would refuse.</para>
    /// </remarks>
    public void Sync(string? composerText)
    {
        var text = composerText ?? string.Empty;

        // A just-chosen command does not reopen its own menu — see the Enter case. Cleared as soon as
        // the text changes to something else, so editing after a choice offers the list again.
        if (_suppressUntilEdit)
        {
            if (text == _chosenText) return;
            _suppressUntilEdit = false;
        }

        if (!text.StartsWith('/') || text.Contains('\n'))
        {
            Close();
            return;
        }

        // A SPACE NO LONGER CLOSES THE MENU — it descends into the command's arguments. That was the
        // discovery gap: the palette vanished at exactly the moment a user had committed to a command
        // and needed to know what may follow it, leaving `/mcp reload` and `/stats clear` reachable
        // only by having read the docs.
        List<Row> matches;
        if (text.Contains(' '))
        {
            var args = SessionCommands.ArgumentsFor(text, Values);

        // A ROW THAT CARRIES ITS PLACEHOLDER COMPLETES TO THE VERB IN FRONT OF IT. `show <name>` and
        // `login <name>` were unselectable: Completes:false made them display-only, so the one way to
        // reach the server list behind them was to type the verb by hand — the list worked, but only
        // for someone who already knew the word the palette was showing them.
        //
        // Completing the WHOLE name would put "<name>" in the composer literally, which is the
        // failure Completes:false existed to prevent. Completing the part before the placeholder
        // gives "…/mcp show " — a prefix with a trailing space, which is exactly what makes the
        // palette open its next level and offer the servers.
            // THE PREFIX IS EVERYTHING ALREADY COMMITTED TO, not just the command name. At one level
            // down those are the same string; at two — "/sessions resume 3" — they are not, and
            // completing to "/sessions 3" would produce a command the dispatcher rejects.
            var lastSpace = text.LastIndexOf(' ');
            var prefix = text[..lastSpace];

            matches = [.. args.Select(a => new Row(
                a.Name, a.Summary, CompletionFor(a, prefix)))];
        }
        else
        {
            // THE HINT RIDES WITH THE SUMMARY, so a command that takes arguments does not look
            // identical to one that does not. Without it `/stats` and `/clear` were the same row to
            // a reader, and nothing on screen suggested one of them had more to offer.
            matches = [.. SessionCommands.Matching(text).Select(c => new Row(
                c.Name,
                c.TakesArguments ? $"{c.Summary}  {c.Hint}" : c.Summary,
                c.Name))];
        }

        if (matches.Count == 0)
        {
            // Nothing matches: close rather than show an empty box. The unknown-command reply on
            // submit is what tells the user they mistyped.
            Close();
            return;
        }

        _shown = matches;
        _selected = Math.Clamp(_selected, 0, matches.Count - 1);

        if (_portal is null)
        {
            Open();
            return;
        }

        // REOPEN WHEN THE ROW COUNT CHANGES, rather than resizing in place.
        //
        // A portal's BufferSize is fixed in its constructor from the bounds it was created with, and
        // layout runs against that buffer — so assigning Bounds afterwards moves the window without
        // giving it room, and the extra rows are clipped. A menu opened on ONE match (recalled
        // "/help" from history) then backspaced to "/" drew four commands into a one-row buffer: the
        // list looked wrong when only its frame was.
        //
        // Recreating is cheap — a markup control and a rectangle — and it is the only way to get a
        // buffer that matches, short of a framework change to make BufferSize settable.
        if (_rowsWhenOpened != matches.Count)
        {
            var keep = _selected;
            Close();
            _selected = keep;   // a refilter must not snap the caret back to the top
            Open();
            return;
        }

        Render();
    }

    /// <summary>
    /// Offers a key to the menu; true when the menu consumed it.
    /// </summary>
    /// <remarks>
    /// Called from the window's PreviewKeyPressed BEFORE the submit path, so ↑/↓ move the selection
    /// rather than walking history and Enter picks rather than submits. Escape closes without
    /// choosing — the one way out that does not also send something.
    /// </remarks>
    internal bool HandleKey(ConsoleKeyInfo key)
    {
        if (!IsOpen) return false;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _selected = _selected == 0 ? _shown.Count - 1 : _selected - 1;
                Render();
                return true;

            case ConsoleKey.DownArrow:
                _selected = (_selected + 1) % _shown.Count;
                Render();
                return true;

            case ConsoleKey.Escape:
                Close();
                return true;

            case ConsoleKey.Enter:
                var picked = _shown[_selected];

                // A PLACEHOLDER CANNOT BE CHOSEN. `<server>` and `<days>` name a shape, not a value;
                // completing them literally would put text in the composer that is not a command.
                // The row exists to say the argument is there — the typing stays the user's.
                if (picked.Completion is null) return true;

                Close();

                // SUPPRESS THE REOPEN. Chosen fills the composer with the command's name, which fires
                // InputChanged, which calls Sync — and "/help" still matches "/help", so the menu came
                // straight back and captured the Enter meant to RUN it. The composer read "/help/"
                // after two Enters and a slash: every key landing somewhere other than intended.
                //
                // A completed choice is the one moment the menu must stay shut: the user has said
                // which command they want, so the next Enter belongs to the submit path.
                // NOT SUPPRESSED WHEN THE COMPLETION ENDS IN A SPACE. A row like `show <name>`
                // completes to "/mcp show " precisely so the palette opens its next level and offers
                // the servers; suppressing there left the user looking at a committed verb and no
                // list, having to type a character and delete it to summon one. Suppression exists
                // for a COMPLETE command — "/help" — where the next Enter belongs to the submit path
                // and reopening would capture it.
                _suppressUntilEdit = !picked.Completion.EndsWith(' ');
                _chosenText = picked.Completion;
                Chosen?.Invoke(this, picked.Completion);
                return true;

            default:
                // ANYTHING ELSE GOES BACK TO THE COMPOSER. The portal has captured a key the user
                // aimed at the prompt — a letter narrowing the filter, or a backspace widening it —
                // and the composer is where it belongs. Handled here rather than declined, because
                // declining hands it to global shortcuts and then drops it.
                return ForwardToComposer(key);
        }
    }

    /// <summary>The composer this menu filters for; typing captured by the portal is replayed here.</summary>
    public PromptControl? Composer { get; set; }

    private bool ForwardToComposer(ConsoleKeyInfo key)
    {
        if (Composer is null) return false;

        var text = Composer.Input ?? string.Empty;

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length == 0) return false;
            Composer.Input = text[..^1];
            return true;
        }

        if (char.IsControl(key.KeyChar)) return false;

        Composer.Input = text + key.KeyChar;
        return true;
    }

    /// <summary>Closes the menu if it is open. Safe to call when it is not.</summary>
    public void Close()
    {
        if (_portal is null) return;


        _system.DesktopPortalService.RemovePortal(_portal);
        _portal = null;
        _list = null;
    }

    private void Open()
    {
        _list = new CommandMenuContent(this) { BackgroundColor = ColorScheme.PromptSurface };
        Render();

        _rowsWhenOpened = _shown.Count;
        _portal = _system.DesktopPortalService.CreatePortal(new DesktopPortalOptions(
            Content: _list,
            Bounds: Bounds(),
            // The click that dismisses must not also land in the transcript behind it.
            DismissOnClickOutside: true,
            ConsumeClickOnDismiss: true,
            Owner: _owner,
            OnDismiss: () => { _portal = null; _list = null; }));
    }

    /// <summary>
    /// Where the list sits: directly above the composer, left-aligned with it.
    ///
    /// <para>ABOVE, not below — the composer is already at the foot of the window, so there is
    /// nothing below it to grow into. Anchored to the window's own rect rather than measured from
    /// the owner, because the composer's height is fixed and known (PromptRows plus its mode line),
    /// which makes this arithmetic rather than a layout query.</para>
    /// </summary>
    private System.Drawing.Rectangle Bounds()
    {
        // WIDE ENOUGH THAT NOTHING WRAPS. At a third of the screen the longest summary folded onto a
        // second line, which breaks the one-row-per-command reading the list depends on — a wrapped
        // row looks like an entry with no name. Two thirds, capped, and Render truncates anything
        // still too long: a clipped summary is legible, a wrapped one is not.
        var width = Math.Min(72, Math.Max(32, _system.DesktopDimensions.Width * 2 / 3));

        // A HEIGHT CAP, because the list is unbounded in principle. Four commands fit anywhere; a
        // future twenty would cover the transcript it is meant to sit over, and on a short terminal
        // even a modest list can leave nothing above it. Capped at a third of the screen and never
        // more than MaxRows — past that the list needs scrolling, which is a different control.
        var height = Math.Min(_shown.Count, Math.Min(MaxRows, Math.Max(1, _system.DesktopDimensions.Height / 3)));

        // SITS DIRECTLY ON THE SEPARATOR, no gap. ComposerRows already counts the prompt, its mode
        // line, the status bar AND the separator row above them, so the first free row is exactly
        // that subtraction — the extra -1 that was here left a blank line between the two.
        var bottom = _system.DesktopDimensions.Height - MainWindow.ComposerRows;

        return new System.Drawing.Rectangle(1, Math.Max(0, bottom - height), width, height);
    }

    private void Render()
    {
        if (_list is null) return;

        // ONE ROW PER COMMAND. The summary is truncated to whatever is left after the caret and the
        // name column, so a long one is clipped rather than wrapped onto a row of its own.
        var available = Bounds().Width;
        const int NameColumn = 12;
        var room = Math.Max(0, available - NameColumn - 4);

        // SHOW THE WINDOW AROUND THE SELECTION, not always the first N. With a height cap the list
        // can be taller than the box, and drawing from index 0 would make everything past the cap
        // unreachable — the caret would move onto rows nobody can see.
        var visible = Bounds().Height;
        var first = Math.Clamp(_selected - visible / 2, 0, Math.Max(0, _shown.Count - visible));
        var last = Math.Min(_shown.Count, first + visible);

        var lines = new List<string>(visible);
        for (var i = first; i < last; i++)
        {
            var c = _shown[i];
            var name = MarkupParser.Escape(c.Label).PadRight(NameColumn);
            var summary = MarkupParser.Escape(
                c.Summary.Length > room ? c.Summary[..Math.Max(0, room - 1)] + "…" : c.Summary);

            // The selected row is the accent; the rest recede. A caret rather than a background
            // highlight, because the portal is already a raised surface and two levels of emphasis
            // on one small list reads as noise.
            lines.Add(i == _selected
                ? $"[{ColorScheme.AccentMarkup}]▸ {name}[/] {summary}"
                : $"[{ColorScheme.MutedMarkup}]  {name} {summary}[/]");
        }

        _list.SetContent(lines);
        if (_portal is not null) _portal.IsDirty = true;
    }
}

/// <summary>
/// The menu's portal content: a markup list that also takes keys.
///
/// <para>IInteractiveControl is not decoration here — an open portal routes keyboard input to its
/// content and drops what the content declines, so a non-interactive content silently eats every
/// keystroke. This forwards to <see cref="CommandMenu.HandleKey"/>, which selects, chooses, or
/// replays the key into the composer.</para>
/// </summary>
internal sealed class CommandMenuContent : MarkupControl, IInteractiveControl
{
    private readonly CommandMenu _menu;

    public CommandMenuContent(CommandMenu menu) : base([]) => _menu = menu;

    public bool IsEnabled { get; set; } = true;

    public bool ProcessKey(ConsoleKeyInfo key) => _menu.HandleKey(key);
}
