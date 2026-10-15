using System.Diagnostics;
using System.Text;

namespace CxAgent.Core.Skills;

/// <summary>What one shell line printed, and how it ended.</summary>
/// <param name="ExitCode">The shell's exit code; -1 when it was killed at the timeout.</param>
/// <param name="TimedOut">Whether it was still running at the timeout.</param>
/// <param name="Output">Stdout then stderr, capped at <see cref="ShellLine.MaxOutputChars"/>.</param>
public sealed record ShellLineResult(int ExitCode, bool TimedOut, string Output);

/// <summary>
/// Runs a command file's <c>!`…`</c> line before the prompt is sent.
///
/// <para>/bin/sh -c, THE SHELL run_shell USES, so a permission rule written against the model's shell
/// calls matches these lines the same way, and an argument is quoted for one known shell.</para>
///
/// <para>ITS OWN SMALL RUNNER, not ProcessRunner. That one is built for a job inside a turn — an
/// 8 KB tail, a spill file, a job context, and a timeout that DETACHES the process to the background
/// registry. A line feeding a prompt wants the opposite on each: the head of the output up to a cap,
/// no job, and a timeout that kills.</para>
/// </summary>
public static class ShellLine
{
    public const int MaxOutputChars = 20 * 1024;

    public static async Task<ShellLineResult> RunAsync(string command, string workingDirectory,
        TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // STDIN CLOSED, NOT INHERITED. A line that reads input would otherwise wait on a terminal
            // the TUI owns, and hang the turn until the timeout.
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("/bin/sh could not be started.");
        process.StandardInput.Close();

        // BOTH STREAMS INTO ONE BUDGET, read as they arrive. Waiting for each to end is waiting for
        // every process holding the pipe to exit — and a line that starts something in the background
        // (`server &`) hands that pipe to a child the shell does not wait for, so the line would hold
        // the turn until that child ends, whenever that is, with Esc unable to stop it.
        var capture = new Capture();
        using var stopReading = new CancellationTokenSource();
        var stdout = capture.PumpAsync(process.StandardOutput, capture.Stdout, stopReading.Token);
        var stderr = capture.PumpAsync(process.StandardError, capture.Stderr, stopReading.Token);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, capture.Full);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);

            // THE SHELL HAS EXITED; whatever its children still write is no longer this line's.
            // A moment to collect what was already in flight, then the pipes are let go.
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(PipeGrace, CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            if (ct.IsCancellationRequested) throw;

            // FULL IS NOT A TIMEOUT. Output past the cap was cut and the line stopped on purpose, so
            // what it printed up to there is the answer, marked truncated below.
            timedOut = !capture.Full.IsCancellationRequested;
        }
        finally
        {
            stopReading.Cancel();
        }

        var text = capture.Text();
        if (capture.Full.IsCancellationRequested)
            text += $"\n[output truncated at {MaxOutputChars / 1024} KB]";

        // A LINE STOPPED AT THE CAP REPORTS THE CODE IT WAS KILLED WITH. That is true, and the
        // truncation note above already says why it ended.
        return new ShellLineResult(timedOut ? -1 : SafeExitCode(process), timedOut, text);
    }

    /// <summary>How long, after the shell exits, output still in the pipe is waited for.</summary>
    private static readonly TimeSpan PipeGrace = TimeSpan.FromMilliseconds(500);

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>
    /// The two streams, read into one shared budget of <see cref="MaxOutputChars"/>.
    ///
    /// <para>CUT AT THE CAP, NOT AFTER. Collecting everything and trimming at the end lets
    /// <c>yes</c> or <c>cat huge.log</c> fill memory first; here reading stops at the cap and
    /// <see cref="Full"/> fires, which stops the line itself.</para>
    /// </summary>
    private sealed class Capture
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _full = new();
        private int _total;

        public StringBuilder Stdout { get; } = new();
        public StringBuilder Stderr { get; } = new();
        public CancellationToken Full => _full.Token;

        public async Task PumpAsync(StreamReader reader, StringBuilder into, CancellationToken stop)
        {
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer, stop);
                    if (read == 0) return;
                    lock (_gate)
                    {
                        var take = Math.Min(read, MaxOutputChars - _total);
                        into.Append(buffer, 0, take);
                        _total += take;
                        if (take < read) { _full.Cancel(); return; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public string Text()
        {
            lock (_gate)
            {
                var output = new StringBuilder(Stdout.ToString().TrimEnd());
                if (Stderr.ToString().TrimEnd() is { Length: > 0 } err)
                {
                    if (output.Length > 0) output.Append('\n');
                    output.Append(err);
                }
                return output.ToString();
            }
        }
    }
}
