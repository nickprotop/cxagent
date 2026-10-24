using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CxAgent.Tests;

/// <summary>
/// Gives each test run a temp folder of its own and deletes it when the run ends.
///
/// <para>THE TESTS LEAVE WHAT THEY MAKE. Some twenty test files create a folder under the system temp directory
/// per test — a project, a config dir, a rules store — and none delete it, so every run left about six hundred
/// folders behind. They built up to more than four hundred thousand, until /tmp hit its inode quota and an
/// unrelated install failed with "Disk quota exceeded". Cleaning up test by test would be twenty fixes and a
/// twenty-first test that forgets; redirecting the temp directory catches all of them, and any written later.</para>
///
/// <para>THE ENVIRONMENT, NOT A HELPER. <see cref="Path.GetTempPath"/> reads TMPDIR (TMP and TEMP on Windows)
/// on every call, and a process the tests start inherits the variable, so a plugin host or a shell a test runs
/// writes into the same folder without any test knowing.</para>
///
/// <para>A RUN THAT IS KILLED DOES NOT REACH ProcessExit — a hang stopped by the 60-second timeout is exactly
/// that — so each run also removes the folders of runs whose process is gone. A folder whose process still
/// lives belongs to a run happening now, in another worktree perhaps, and is left alone.</para>
/// </summary>
internal static class TestTempRoot
{
    private const string Prefix = "cxagent-tests-";

    [ModuleInitializer]
    internal static void Redirect()
    {
        var system = Path.GetTempPath();
        SweepDeadRuns(system);

        var root = Path.Combine(system, Prefix + Environment.ProcessId);
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "TMPDIR", "TMP", "TEMP" })
            Environment.SetEnvironmentVariable(name, root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
    }

    private static void SweepDeadRuns(string system)
    {
        foreach (var dir in Directory.EnumerateDirectories(system, Prefix + "*"))
        {
            var suffix = Path.GetFileName(dir)[Prefix.Length..];
            if (int.TryParse(suffix, out var pid) && !IsRunning(pid)) TryDelete(dir);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // A file still held open — a child process outliving its test — keeps the folder until a later run
            // sweeps it; failing a test run over temp files would be worse than leaving them one run longer.
        }
    }
}
