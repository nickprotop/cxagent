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
    private IReadOnlyList<SessionCommand> _shown = [];
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

    /// <summary>Whether the menu is currently on screen.</summary>
    public bool IsOpen => _portal is not null;

    /// <summary>The command the user settled on, when they chose one.</summary>
    public event EventHandler<SessionCommand>? Chosen;

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

        if (!text.StartsWith('/') || text.Contains('\n') || text.Contains(' '))
        {
            Close();
            return;
        }

        var matches = SessionCommands.Matching(text);
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
                Close();

                // SUPPRESS THE REOPEN. Chosen fills the composer with the command's name, which fires
                // InputChanged, which calls Sync — and "/help" still matches "/help", so the menu came
                // straight back and captured the Enter meant to RUN it. The composer read "/help/"
                // after two Enters and a slash: every key landing somewhere other than intended.
                //
                // A completed choice is the one moment the menu must stay shut: the user has said
                // which command they want, so the next Enter belongs to the submit path.
                _suppressUntilEdit = true;
                _chosenText = picked.Name;
                Chosen?.Invoke(this, picked);
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
            var name = MarkupParser.Escape(c.Name).PadRight(NameColumn);
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
