using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>In btw mode the palette stays shut: a slash there is text for the aside, not a command.</summary>
public class CommandMenuBtwTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private CommandMenu Menu(bool commandsOff) =>
        new(_fixture.Host.System, _fixture.Host.Main.Window!, _fixture.Host.Main.Input)
        {
            Registry = _fixture.Host.Session.Manager!.Commands,
            CommandsOff = () => commandsOff,
        };

    [Fact]
    public void WithCommandsOff_ASlashOpensNothing()
    {
        var menu = Menu(commandsOff: true);

        menu.Sync("/c");

        Assert.False(menu.IsOpen);
    }

    /// <summary>The control: the same text opens the palette when commands are on.</summary>
    [Fact]
    public void WithCommandsOn_ASlashOpensThePalette()
    {
        var menu = Menu(commandsOff: false);

        menu.Sync("/c");

        Assert.True(menu.IsOpen);
        menu.Sync("");
    }
}

/// <summary>
/// The palette's geometry: wide enough for what it lists, and opaque across its whole width.
/// </summary>
public class CommandMenuLayoutTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    /// <summary>Descriptions were clipped with "…" at a fixed 72 columns on a terminal with room.</summary>
    [Theory]
    [InlineData(140, 12, 80, 96)]    // room: grows to caret + name + summary + a space
    [InlineData(140, 12, 40, 56)]
    [InlineData(80, 12, 120, 78)]    // too long for the terminal: capped at it less a column each side
    [InlineData(40, 12, 3, 32)]      // short rows still get a usable minimum
    public void TheWidthFollowsTheLongestRowAndIsCappedByTheTerminal(
        int terminal, int nameColumn, int longestSummary, int expected)
    {
        Assert.Equal(expected, CommandMenu.WidthFor(terminal, nameColumn, longestSummary));
    }

    [Fact]
    public void TheOpenMenuIsStretchedAndWithinTheTerminal()
    {
        var host = _fixture.Host;
        var menu = new CommandMenu(host.System, host.Main.Window!, host.Main.Input)
        {
            Registry = host.Session.Manager!.Commands,
        };

        menu.Sync("/");

        Assert.True(menu.IsOpen);
        var portal = host.System.DesktopPortalService.TopPortal;
        Assert.NotNull(portal);
        Assert.InRange(portal!.Bounds.Width, 32, host.System.DesktopDimensions.Width - 2);
        Assert.Equal(SharpConsoleUI.Layout.HorizontalAlignment.Stretch,
            Assert.IsAssignableFrom<SharpConsoleUI.Controls.IWindowControl>(portal.Content).HorizontalAlignment);
        menu.Sync("");
    }
}

/// <summary>
/// ENTER RUNS A LINE THAT IS ALREADY A COMPLETE COMMAND, rather than choosing a longer row that happens to
/// match: "/model local" with "local" and "local2" configured switched to local2.
/// </summary>
public class CommandMenuEnterTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private (CommandMenu Menu, Func<bool> Submitted, Func<string?> Chosen) Menu()
    {
        var host = _fixture.Host;
        var submitted = false;
        string? chosen = null;
        var menu = new CommandMenu(host.System, host.Main.Window!, host.Main.Input)
        {
            Registry = host.Session.Manager!.Commands,
            Values = _ => [new CxAgent.Core.Commands.CommandArgument("local", "the first"), new CxAgent.Core.Commands.CommandArgument("local2", "the second")],
            Submit = () => submitted = true,
        };
        menu.Chosen += (_, text) => chosen = text;
        return (menu, () => submitted, () => chosen);
    }

    private void Press(ConsoleKey key) =>
        Assert.IsAssignableFrom<SharpConsoleUI.Controls.IInteractiveControl>(
                _fixture.Host.System.DesktopPortalService.TopPortal!.Content)
            .ProcessKey(new ConsoleKeyInfo(key == ConsoleKey.Enter ? '\r' : '\0', key, false, false, false));

    [Fact]
    public void Enter_OnACompleteLine_RunsIt()
    {
        var (menu, submitted, chosen) = Menu();
        menu.Sync("/model local");
        Assert.True(menu.IsOpen);   // still offering local2

        Press(ConsoleKey.Enter);

        Assert.True(submitted());
        Assert.Null(chosen());
    }

    /// <summary>A row the user moved to is still theirs to choose.</summary>
    [Fact]
    public void Enter_AfterMovingToARow_ChoosesIt()
    {
        var (menu, submitted, chosen) = Menu();
        menu.Sync("/model local");
        Assert.True(menu.IsOpen);

        Press(ConsoleKey.DownArrow);
        Press(ConsoleKey.UpArrow);
        Press(ConsoleKey.Enter);

        Assert.False(submitted());
        Assert.Equal("/model local2", chosen());
    }
}
