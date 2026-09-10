using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CxAgent.Plugins.Triggers;

/// <summary>
/// Where the FULL output went, when it did not fit inline, and how big that full output was.
///
/// <para>ONE RECORD, NOT TWO FIELDS ON WatchOutcome, because a path with no size is half the story a
/// spill message has to tell: Compose needs both together or not at all, so making a caller pass one
/// without the other is a bug the type system should catch rather than a convention to remember.</para>
/// </summary>
/// <param name="Path">Where the whole output was written.</param>
/// <param name="TotalBytes">The full output's size, before it was cut down to the inline tail.</param>
public sealed record Spill(string Path, long TotalBytes);

/// <summary>What a watched process left behind.</summary>
/// <param name="ExitCode">Its code, or null when it was killed at the timeout.</param>
/// <param name="TimedOut">True when nothing exited and the wait gave up.</param>
/// <param name="Output">The merged streams, tail-capped at <see cref="ProcessWatch.InlineCap"/>.</param>
/// <param name="Spill">Set when the output did not fit and had to be written out in full.</param>
public sealed record WatchOutcome(int? ExitCode, bool TimedOut, string Output, Spill? Spill);

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
        // THE CATALOG DECLARES "any" PLATFORM, AND A WATCH FAILS SILENTLY IF THAT IS WRONG: the
        // watch task runs unattended and unawaited, so a shell that does not exist on this OS throws
        // there instead of here, and the wake this trigger promised simply never arrives.
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var info = new ProcessStartInfo(isWindows ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(isWindows ? "/c" : "-c");
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
            try
            {
                process.Kill(entireProcessTree: true);
                // KILL ONLY ASKS; IT DOES NOT WAIT. Reading ExitCode before the process has actually
                // exited throws InvalidOperationException — and that is not only the timeout path:
                // when the OUTER ct fires instead, timedOut is false and the code below still reads
                // ExitCode, so the wait belongs here rather than behind the timedOut branch.
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception) { /* Already gone; the outcome is the same. */ }
        }

        string text;
        lock (sync) text = merged.ToString();

        // EXITCODE READ THROUGH A HELPER ON EVERY PATH: even after the wait above, a process that
        // could not be killed (already reaped, or permissions) leaves HasExited false, and reading
        // ExitCode then still throws. A watch that could not learn how the command ended is not a
        // watch that should throw instead of waking.
        var exitCode = timedOut ? null : TryExitCode(process);

        if (text.Length <= InlineCap)
            return new WatchOutcome(exitCode, timedOut, text, null);

        // WRITTEN WHOLE AND NAMED, rather than truncated. A submitted turn cannot be taken back: an
        // uncapped build on a large solution is megabytes, unattended, and one wake could exhaust the
        // window with nobody there to stop it.
        var spillPath = Path.Combine(Path.GetTempPath(),
            $"cxagent-trigger-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}.log");
        Spill? spill;
        try
        {
            await File.WriteAllTextAsync(spillPath, text, CancellationToken.None);
            // THE BYTE COUNT WRITTEN, NOT text.Length: those diverge once the merged output holds
            // anything outside ASCII, and the size that matters here is what landed on disk.
            spill = new Spill(spillPath, Encoding.UTF8.GetByteCount(text));
        }
        catch (Exception)
        {
            // A file we cannot write is not a wake we cannot send. The tail still travels, and with
            // no path to name there is nothing a size would tell the reader either.
            spill = null;
        }

        return new WatchOutcome(exitCode, timedOut, text[^InlineCap..], spill);
    }

    /// <summary>
    /// ExitCode without the throw: it demands HasExited, which a killed-but-not-yet-reaped or
    /// already-gone process can still fail even after an explicit wait.
    /// </summary>
    private static int? TryExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
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

        if (outcome.Spill is { } spill)
            sb.AppendLine($"… {FormatSize(spill.TotalBytes)} of output, in full at {spill.Path} …");

        sb.Append(outcome.Output);
        return sb.ToString();
    }

    /// <summary>
    /// Bytes at the scale a reader compares by, the way <c>Cap</c> already says a character count —
    /// this one just says how big the thing that got cut down actually was.
    /// </summary>
    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
