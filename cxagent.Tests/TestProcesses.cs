namespace CxAgent.Tests;

/// <summary>
/// Where a test's child process runs when the test does not care.
///
/// <para>NAMED, NEVER INHERITED. A process started without a working directory runs in the PROCESS's
/// current one, and the classes in <see cref="WorkingDirectoryCollection"/> switch that to temp folders
/// they then delete. The collection keeps them from colliding with each other; it does nothing for a
/// test in another class that starts <c>/bin/sh</c> in that moment — the start fails with "No such file
/// or directory", naming a folder the failing test never heard of.</para>
///
/// <para>THE TEMP ROOT, because nothing deletes it: every test's own folder lives under it, and
/// removing one of those leaves this standing.</para>
/// </summary>
internal static class TestProcesses
{
    public static string WorkingDir { get; } = Path.GetTempPath();
}
