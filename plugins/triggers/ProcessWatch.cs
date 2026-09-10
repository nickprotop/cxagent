using System.Diagnostics;
using System.Text;

namespace CxAgent.Plugins.Triggers;

/// <summary>What a watched process left behind.</summary>
/// <param name="ExitCode">Its code, or null when it was killed at the timeout.</param>
/// <param name="TimedOut">True when nothing exited and the wait gave up.</param>
/// <param name="Output">The merged streams, tail-capped at <see cref="ProcessWatch.InlineCap"/>.</param>
/// <param name="SpillPath">Where the whole output went, when it did not fit.</param>
public sealed record WatchOutcome(int? ExitCode, bool TimedOut, string Output, string? SpillPath);

/// <summary>
/// Runs a command and waits for it to end.
///
/// <para>A PROCESS IS THE TRIGGER AND ITS EXIT IS THE EVENT. "Wake me when CI finishes" is not a
/// schedule: `gh run watch` BLOCKS until the run ends, so this starts it once, waits, and submits
/// when it exits. One process, one wake, no repeated unattended execution — which is why polling with
/// a shell check was refused, since it runs unattended repeatedly and burns a check on every tick
/// that a blocking wait gets for free.</para>
/// </summary>
public static class ProcessWatch
{
    /// <summary>
    /// How much output travels inline before the rest goes to a file.
    ///
    /// <para>DELIBERATELY NOT TelemetryReports.Cap's 4096. That one takes the HEAD, because it bounds
    /// a report the model re-reads EVERY TURN. A trigger's output arrives ONCE, for a process the
    /// user chose to wait on: a different budget, a different end, and a file underneath it.</para>
    ///
    /// <para>AND A CAP ALONE WOULD THROW AWAY THE ONE THING NOBODY CAN GUESS AT. Truncation always
    /// bets on which end matters. Writing the file removes the bet: the agent gets the useful tail
    /// immediately and can read the rest if the tail was not enough.</para>
    /// </summary>
    public const int InlineCap = 64_000;

    public static async Task<WatchOutcome> Run(string command, TimeSpan timeout,
        Action<int> registerPid, CancellationToken ct)
    {
        var info = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);

        using var process = new Process { StartInfo = info };

        // ONE MERGED STREAM, as a person watching the terminal would see it. A tool that writes
        // progress to one and its verdict to the other reads as nonsense when the two are
        // concatenated at exit.
        var merged = new StringBuilder();
        var sync = new object();
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (sync) merged.AppendLine(e.Data);
        }
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;

        process.Start();

        // THROUGH RegisterChildProcess, so a crashed cxagent does not strand it. A plugin that
        // crashed is a plugin that cannot clean up after itself, which is the entire scenario reaping
        // exists for.
        registerPid(process.Id);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); }
            catch (Exception) { /* Already gone; the outcome is the same. */ }
        }

        string text;
        lock (sync) text = merged.ToString();

        if (text.Length <= InlineCap)
            return new WatchOutcome(timedOut ? null : process.ExitCode, timedOut, text, null);

        // WRITTEN WHOLE AND NAMED, rather than truncated. A submitted turn cannot be taken back: an
        // uncapped build on a large solution is megabytes, unattended, and one wake could exhaust the
        // window with nobody there to stop it.
        var spill = Path.Combine(Path.GetTempPath(),
            $"cxagent-trigger-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}.log");
        try
        {
            await File.WriteAllTextAsync(spill, text, CancellationToken.None);
        }
        catch (Exception)
        {
            // A file we cannot write is not a wake we cannot send. The tail still travels.
            spill = null;
        }

        return new WatchOutcome(timedOut ? null : process.ExitCode, timedOut,
            text[^InlineCap..], spill);
    }

    /// <summary>
    /// What the agent receives: the prompt as written, then the ending, then what was printed.
    ///
    /// <para>APPENDED, ALWAYS, AND THE PROMPT IS JUST THE PROMPT. A {output} placeholder looked
    /// flexible and its failure mode is silent — a model that writes the prompt and forgets the
    /// placeholder loses the output entirely, discovered only when a useless wake arrives hours
    /// later. Appending cannot be forgotten.</para>
    /// </summary>
    public static string Compose(string prompt, WatchOutcome outcome)
    {
        var sb = new StringBuilder(prompt).AppendLine().AppendLine();

        // A TIMEOUT IS DIFFERENT NEWS FROM A FAILURE. "CI failed, here is why" and "CI is still
        // running after half an hour" are different reasons to wake someone, and a prompt cannot
        // distinguish them if both arrive as a number.
        sb.AppendLine(outcome.TimedOut ? "timed out" : $"exit {outcome.ExitCode}");

        if (outcome.SpillPath is { } path)
            sb.AppendLine($"… output too long to include in full; all of it is at {path} …");

        sb.Append(outcome.Output);
        return sb.ToString();
    }
}
