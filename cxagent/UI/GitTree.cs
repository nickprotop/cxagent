using System.Diagnostics;

namespace CxAgent.UI;

/// <summary>
/// A folder's working tree, as the session panel shows it — for the terminal's panel and a browser's alike.
/// </summary>
public static class GitTree
{
    /// <summary>The working tree's shape — "3 changed · 1 untracked" — or null when it is clean or not a repository.
    /// Run in <paramref name="dir"/> (the process's folder when null), with <paramref name="limit"/> to answer.</summary>
    public static string? Status(string? dir = null, TimeSpan? limit = null, string program = "git")
    {
        try
        {
            // HARDENED: this runs on its own, outside any sandbox, and must not be what runs a repository's fsmonitor.
            using var p = Process.Start(Core.Sandbox.GitSafe.Harden(new ProcessStartInfo(program, "status --porcelain")
            {
                WorkingDirectory = dir ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }));
            if (p is null) return null;

            // READ BEFORE WAITING. Past a pipe's worth of status — about 1,500 files — git blocks until someone
            // drains it, so waiting first timed out and the tree showed clean.
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(limit ?? TimeSpan.FromMilliseconds(500))) { try { p.Kill(true); } catch (Exception) { } return "too slow to count"; }
            if (p.ExitCode != 0) return "status unknown";

            int changed = 0, untracked = 0;
            foreach (var line in output.GetAwaiter().GetResult().Split('\n'))
            {
                if (line.Length < 2) continue;
                if (line.StartsWith("??", StringComparison.Ordinal)) untracked++;
                else changed++;
            }

            if (changed == 0 && untracked == 0) return null;

            var parts = new List<string>(2);
            if (changed > 0) parts.Add($"{changed} changed");
            if (untracked > 0) parts.Add($"{untracked} untracked");
            return string.Join(" · ", parts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record Reading(DateTimeOffset At, string? Status, bool Running);

    private static readonly Dictionary<string, Reading> Readings = new(StringComparer.Ordinal);

    /// <summary>How long a reading stays good — the terminal panel's own five seconds.</summary>
    private static readonly TimeSpan GoodFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The last reading for <paramref name="folder"/>, starting a new one when it is older than five seconds — so a
    /// panel rebuilt every second runs git at most once per five, off the caller's thread. Null until the first
    /// reading is in, and for a clean tree.
    /// </summary>
    public static string? Cached(string folder)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Readings)
        {
            var had = Readings.GetValueOrDefault(folder) ?? new Reading(DateTimeOffset.MinValue, null, false);
            if (!had.Running && now - had.At >= GoodFor)
            {
                Readings[folder] = had with { At = now, Running = true };
                _ = Task.Run(() =>
                {
                    var status = Status(folder);
                    lock (Readings) Readings[folder] = new Reading(now, status, false);
                });
            }
            return Readings[folder].Status;
        }
    }
}
