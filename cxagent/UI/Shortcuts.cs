using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// One key this app binds, and what to tell the user it does.
/// </summary>
/// <param name="Modifiers">The modifier the key is bound with.</param>
/// <param name="Key">The key itself.</param>
/// <param name="Description">
/// What it does, in the words <c>/help</c> and a button hint will show — or null for a binding that
/// is plumbing rather than a feature.
///
/// <para>NULL IS A REAL ANSWER, not an omission. The theme picker binds Up, Down, Enter and Escape
/// while it is open; those are how a dialog works, not keys anyone should be told about, and a list
/// that named them would bury the four that matter.</para>
/// </param>
public sealed record Shortcut(ConsoleModifiers Modifiers, ConsoleKey Key, string? Description)
{
    /// <summary>
    /// How the key reads to a person: <c>F3</c>, <c>Ctrl+Q</c>, <c>^S</c> in a button's hint.
    ///
    /// <para>CARET FORM FOR A BUTTON, because a hint sits inside a label a few characters wide and
    /// "Ctrl+S" spends four of them on a word the symbol already says. The long form is for prose,
    /// where a reader meets the key once and should not have to decode it.</para>
    /// </summary>
    public string Label(bool caret = false)
    {
        var name = Key switch
        {
            ConsoleKey.LeftArrow => "←",
            ConsoleKey.RightArrow => "→",
            ConsoleKey.UpArrow => "↑",
            ConsoleKey.DownArrow => "↓",
            _ => Key.ToString(),
        };

        return Modifiers switch
        {
            ConsoleModifiers.Control when caret => $"^{name}",
            ConsoleModifiers.Control => $"Ctrl+{name}",
            ConsoleModifiers.Alt => $"Alt+{name}",
            ConsoleModifiers.Shift => $"Shift+{name}",
            _ => name,
        };
    }
}

/// <summary>
/// Every key this app binds, recorded as it is bound.
///
/// <para>ONE SOURCE, BECAUSE THREE HAD ALREADY DRIFTED. The status bar, the <c>/help</c> table and
/// (soon) a button's hint each claimed to know the keymap, and none of them was where keys are
/// actually registered: help advertised F5 for a settings dialog that had been deleted, and omitted
/// F2 and F9 while the status bar showed both. The commands escaped this by reading from their
/// registry; the keys had none.</para>
///
/// <para>THE SAME REASONING <c>SessionCommands</c> CARRIES, and its comment says it outright — "FROM
/// THE TABLE, not a second copy. Every list of commands that is maintained by hand drifts from the
/// dispatcher the first time one is added."</para>
///
/// <para>REGISTERING THROUGH THIS IS THE POINT. A key bound directly on the window system is invisible
/// here and will drift again, so <see cref="Bind(ConsoleWindowSystem, ConsoleModifiers, ConsoleKey,
/// string?, Action)"/> both binds and records — there is no way to do one without the other.</para>
/// </summary>
public sealed class Shortcuts
{
    private readonly List<Shortcut> _bound = [];

    /// <summary>Everything bound, in the order it was bound.</summary>
    public IReadOnlyList<Shortcut> All => _bound;

    /// <summary>The ones worth telling a user about — those given a description.</summary>
    public IReadOnlyList<Shortcut> Documented =>
        [.. _bound.Where(s => !string.IsNullOrWhiteSpace(s.Description))];

    /// <summary>Binds a key and records it. Use this rather than the window system directly.</summary>
    public void Bind(ConsoleWindowSystem system, ConsoleModifiers modifiers, ConsoleKey key,
                     string? description, Action action)
    {
        _bound.Add(new Shortcut(modifiers, key, description));
        system.RegisterGlobalShortcut(modifiers, key, action);
    }

    /// <summary>
    /// Binds a key whose handler decides whether it is consumed, and records it.
    ///
    /// <para>The <c>Func&lt;bool&gt;</c> overload exists because the Action one is wrapped to always
    /// report the key consumed, and a handler that must yield to a dialog cannot say so.</para>
    /// </summary>
    public void Bind(ConsoleWindowSystem system, ConsoleModifiers modifiers, ConsoleKey key,
                     string? description, Func<bool> action)
    {
        _bound.Add(new Shortcut(modifiers, key, description));
        system.RegisterGlobalShortcut(modifiers, key, action);
    }

    /// <summary>
    /// The key bound to a described action, or null — for a button that wants to show its own hint.
    ///
    /// <para>MATCHED ON THE DESCRIPTION rather than an id, so a caller asks for what it means rather
    /// than repeating a key it would then have to keep in step.</para>
    /// </summary>
    public Shortcut? For(string description) =>
        _bound.FirstOrDefault(s =>
            string.Equals(s.Description, description, StringComparison.Ordinal));

    /// <summary>The <c>/help</c> table's key rows, one per documented binding.</summary>
    public IEnumerable<string> HelpRows() =>
        Documented.Select(s => $"| `{s.Label()}` | {s.Description} |");
}
