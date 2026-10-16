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
