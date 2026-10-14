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
