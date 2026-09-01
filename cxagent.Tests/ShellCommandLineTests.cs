using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A typed command line becomes a child process. The user's OWN shell, not a portable subset:
/// <c>ls *.cs</c> globs under bash and does not under cmd, and someone typing a command line means
/// it for their own machine.
/// </summary>
public class ShellCommandLineTests
{
    [Fact]
    public void ACommand_RunsThroughTheShellWithMinusC()
    {
        var child = ShellCommandLine.For("echo hi && echo bye");

        // THE WHOLE COMMAND AS ONE ARGUMENT. Splitting it on spaces would hand the shell "echo"
        // and leave "&&" as a separate argv entry, which is not what -c means.
        if (OperatingSystem.IsWindows())
            Assert.Equal(["/c", "echo hi && echo bye"], child.Args);
        else
            Assert.Equal(["-c", "echo hi && echo bye"], child.Args);
    }

    [Fact]
    public void NoCommand_IsABareInteractiveShell()
    {
        // -c "" would exit immediately; a bare shell is a prompt the user can type at.
        Assert.Empty(ShellCommandLine.For("").Args);
        Assert.Empty(ShellCommandLine.For("   ").Args);
    }

    [Fact]
    public void TheExe_IsAlwaysSomethingRunnable()
    {
        // A fallback exists on every path: an unset $SHELL or %ComSpec% must not produce an empty
        // exe, which fails at spawn with no useful message.
        Assert.False(string.IsNullOrWhiteSpace(ShellCommandLine.For("x").Exe));
    }

    [Fact]
    public void ABareShell_AndACommand_UseTheSameExe()
    {
        // The mode changes the ARGUMENTS, never which shell the user gets.
        Assert.Equal(ShellCommandLine.For("").Exe, ShellCommandLine.For("ls").Exe);
    }
}
