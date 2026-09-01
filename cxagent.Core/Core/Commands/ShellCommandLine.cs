namespace CxAgent.Core.Commands;

/// <summary>A child process to spawn: what to run, and what to pass it.</summary>
/// <param name="Exe">The shell binary.</param>
/// <param name="Args">Its arguments — empty for an interactive shell.</param>
public readonly record struct ShellChild(string Exe, string[] Args);

/// <summary>
/// Turns a command line someone typed into the child process that runs it.
///
/// <para>THROUGH A SHELL, NOT AS A BARE PROCESS. Spawning the command directly would give an
/// unarguable exit code and a clean transcript, and would break <c>&amp;&amp;</c>, pipes, globs and
/// <c>~</c> — everything a person expects when they type a command line. The exit code survives
/// anyway: <c>-c</c> returns what the command returned.</para>
///
/// <para>THE USER'S OWN SHELL, NOT A PORTABLE SUBSET. <c>ls *.cs</c> globs under bash and does not
/// under cmd; <c>~</c> means nothing on Windows. Someone typing a command line means it for their
/// own machine, so this reads their environment rather than imposing a lowest common denominator.
/// </para>
///
/// <para>IN CORE, ALONE AMONG /shell's PIECES, because it is a pure function from a string to a
/// process description and needs no window to test. Everything else the command does needs a UI.
/// </para>
/// </summary>
public static class ShellCommandLine
{
    /// <summary>
    /// The child that runs <paramref name="command"/>, or an interactive shell when it is empty.
    ///
    /// <para>EMPTY MEANS A PROMPT, NOT <c>-c ""</c>, which would exit instantly and leave a window
    /// showing nothing. A bare /shell is a terminal the user types in.</para>
    ///
    /// <para>THE COMMAND IS ONE ARGUMENT, never split. <c>-c</c> takes a whole command line as a
    /// single argv entry; splitting on spaces would hand the shell <c>echo</c> and leave
    /// <c>&amp;&amp;</c> stranded as an argument to it.</para>
    /// </summary>
    public static ShellChild For(string? command)
    {
        var exe = Shell();
        return string.IsNullOrWhiteSpace(command)
            ? new ShellChild(exe, [])
            : new ShellChild(exe, [OperatingSystem.IsWindows() ? "/c" : "-c", command]);
    }

    /// <summary>
    /// The user's shell, with a fallback that always exists.
    ///
    /// <para>A FALLBACK ON EVERY PATH: an unset SHELL or ComSpec would otherwise produce an empty
    /// exe, which fails at spawn with a message naming nothing the user can act on.</para>
    /// </summary>
    private static string Shell()
    {
        var name = OperatingSystem.IsWindows() ? "ComSpec" : "SHELL";
        var configured = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
    }
}
