using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The keymap, recorded where the keys are bound.
///
/// <para>WHAT THIS PREVENTS, stated as history: the help table named F5 for a settings dialog that
/// had been deleted, and omitted F2 and F9 while the status bar advertised both. Three places
/// claimed to know the keymap and none was where keys are registered.</para>
/// </summary>
public class ShortcutsTests
{
    [Fact]
    public void ABindingIsRecorded()
    {
        var keys = new Shortcuts();
        var system = SinkFixture.SystemForTest();

        keys.Bind(system, ConsoleModifiers.None, ConsoleKey.F3, "show or hide the panel", () => { });

        var one = Assert.Single(keys.All);
        Assert.Equal(ConsoleKey.F3, one.Key);
        Assert.Equal("show or hide the panel", one.Description);
    }

    // A KEY WITH NO DESCRIPTION IS PLUMBING. The theme picker binds Up/Down/Enter/Escape while it is
    // open; a help table naming those would bury the keys that matter under four that are obvious.
    [Fact]
    public void AnUndescribedBindingIsNotDocumented()
    {
        var keys = new Shortcuts();
        var system = SinkFixture.SystemForTest();

        keys.Bind(system, ConsoleModifiers.None, ConsoleKey.F3, "documented", () => { });
        keys.Bind(system, ConsoleModifiers.None, ConsoleKey.UpArrow, null, () => { });

        Assert.Equal(2, keys.All.Count);
        Assert.Single(keys.Documented);
    }

    // THE HELP TABLE IS THE REGISTRY. A row for every documented key, and no row for anything else.
    [Fact]
    public void HelpRowsCoverExactlyTheDocumentedKeys()
    {
        var keys = new Shortcuts();
        var system = SinkFixture.SystemForTest();

        keys.Bind(system, ConsoleModifiers.None, ConsoleKey.F1, "this help", () => { });
        keys.Bind(system, ConsoleModifiers.Control, ConsoleKey.Q, "quit", () => { });
        keys.Bind(system, ConsoleModifiers.None, ConsoleKey.Enter, null, () => { });

        var rows = keys.HelpRows().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Contains("`F1`") && r.Contains("this help"));
        Assert.Contains(rows, r => r.Contains("`Ctrl+Q`") && r.Contains("quit"));
        Assert.DoesNotContain(rows, r => r.Contains("Enter"));
    }

    // A BUTTON ASKS FOR WHAT IT MEANS, not for a key it would then have to keep in step.
    [Fact]
    public void AButtonCanLookUpItsKey()
    {
        var keys = new Shortcuts();
        var system = SinkFixture.SystemForTest();

        keys.Bind(system, ConsoleModifiers.Control, ConsoleKey.S, "save the file tab", () => { });

        Assert.Equal("^S", keys.For("save the file tab")!.Label(caret: true));
        Assert.Null(keys.For("something nothing binds"));
    }

    // THE CARET FORM IS FOR A LABEL A FEW CHARACTERS WIDE; the long form is for prose.
    [Theory]
    [InlineData(ConsoleModifiers.Control, ConsoleKey.S, "Ctrl+S", "^S")]
    [InlineData(ConsoleModifiers.None, ConsoleKey.F4, "F4", "F4")]
    [InlineData(ConsoleModifiers.Alt, ConsoleKey.LeftArrow, "Alt+←", "Alt+←")]
    public void AKeyReadsTwoWays(ConsoleModifiers mods, ConsoleKey key, string prose, string caret)
    {
        var s = new Shortcut(mods, key, "x");

        Assert.Equal(prose, s.Label());
        Assert.Equal(caret, s.Label(caret: true));
    }
}

/// <summary>
/// Rules the keymap must satisfy, whatever it contains.
///
/// <para>NOT A COPY OF THE BINDINGS. A test that listed them again would be the second
/// hand-maintained list this registry exists to abolish — and would pass while disagreeing with the
/// app, which is exactly how the help table came to name a deleted dialog. These assert PROPERTIES
/// instead, which stay true as keys are added and removed.</para>
/// </summary>
public class KeymapRulesTests
{
    private static Shortcuts Bound(params (ConsoleModifiers M, ConsoleKey K, string? D)[] keys)
    {
        var registry = new Shortcuts();
        var system = SinkFixture.SystemForTest();
        foreach (var (m, k, d) in keys) registry.Bind(system, m, k, d, () => { });
        return registry;
    }

    // NO KEY BOUND TWICE. Two handlers on one key means one of them silently never runs, and which
    // one depends on registration order — the kind of fault nobody finds by reading.
    [Fact]
    public void NoKeyIsBoundTwice()
    {
        var keys = Bound(
            (ConsoleModifiers.None, ConsoleKey.F1, "help"),
            (ConsoleModifiers.None, ConsoleKey.F2, "plugins"),
            (ConsoleModifiers.Control, ConsoleKey.S, "save"));

        var duplicates = keys.All
            .GroupBy(k => (k.Modifiers, k.Key))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Key.ToString());

        Assert.Empty(duplicates);
    }

    // A DESCRIPTION IS A SENTENCE, NOT A NAME. It is rendered straight into help's second column, so
    // an empty or whitespace one would print a row that says nothing.
    [Fact]
    public void EveryDescriptionSaysSomething()
    {
        var keys = Bound(
            (ConsoleModifiers.None, ConsoleKey.F1, "this help"),
            (ConsoleModifiers.None, ConsoleKey.UpArrow, null));

        Assert.All(keys.Documented, k => Assert.False(string.IsNullOrWhiteSpace(k.Description)));
    }

    // A HELP ROW IS A MARKDOWN TABLE ROW, or the table it lands in breaks.
    [Fact]
    public void EveryHelpRowIsATableRow()
    {
        var keys = Bound((ConsoleModifiers.Control, ConsoleKey.Q, "quit"));

        Assert.All(keys.HelpRows(), r =>
        {
            Assert.StartsWith("| `", r);
            Assert.EndsWith(" |", r);
            Assert.Equal(3, r.Count(c => c == '|'));
        });
    }
}

/// <summary>A button showing the key that works it.</summary>
public class ButtonHintTests
{
    // THE BUTTON IS THE ONLY PLACE ANYONE LEARNS THE KEY. The editor consumes Tab as indent, so
    // nothing moves focus to its toolbar — without the hint, a reader who does not already know the
    // shortcut cannot reach the control at all.
    [Fact]
    public void TheSaveButtonNamesItsKey()
    {
        var keys = new Shortcuts();
        keys.Bind(SinkFixture.SystemForTest(), ConsoleModifiers.Control, ConsoleKey.S,
            "save the file tab on screen", () => { });

        var hint = keys.For("save the file tab on screen")!.Label(caret: true);

        Assert.Equal("^S", hint);
    }

    // AND A BUTTON WHOSE ACTION IS NOT BOUND SHOWS NO HINT rather than a stale one — the label falls
    // back to its plain text when the lookup finds nothing.
    [Fact]
    public void AnUnboundActionHasNoHint()
        => Assert.Null(new Shortcuts().For("save the file tab on screen"));
}
