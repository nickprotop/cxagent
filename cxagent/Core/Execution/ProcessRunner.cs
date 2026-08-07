using System.Text;
using System.Diagnostics;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;

namespace CxAgent.Core.Execution;

public record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDir = null,
    IReadOnlyDictionary<string, string>? Env = null,
    int? TimeoutSeconds = null);

/// <param name="Stdout">
/// What the command printed, capped at <see cref="ProcessRunner.MaxCapturedChars"/>.
///
/// <para>Captured as well as logged because the log file is for the USER and this is for the next
/// JOB. Without it <c>{{some_shell_job.stdout}}</c> could never resolve: ShellJobPlugin had nothing
/// but an exit code to put in its output bag, so any goal that shelled out and fed the result
/// onward failed. A live drive of "list ~/bin. what it does?" reported the directory EMPTY, because
/// from the model's side the listing genuinely produced nothing.</para>
/// </param>
/// <param name="Stderr">Diagnostics, same cap. Separate from Stdout so a job can reference either.</param>
public record ProcessResult(int ExitCode, bool TimedOut, string Stdout = "", string Stderr = "");

/// <summary>
/// Runs a child process, streaming stdout/stderr lines to the job context. Reads output
/// ASYNCHRONOUSLY (event-based) so a full pipe buffer never deadlocks the child. A per-run
/// timeout or the external cancellation token kills the whole process tree.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Cap on captured stdout/stderr, per stream. Matches WorkerToolset.MaxToolResultChars — both
    /// bound the same thing, text on its way into a model's context, and a command's output feeding
    /// a downstream job is subject to exactly that pressure. The full text is always in the log file,
    /// which is why capping here loses nothing the user cannot still read.
    /// </summary>
    public const int MaxCapturedChars = 8192;


    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, IJobContext ctx, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // REDIRECT STDIN AND CLOSE IT (below), so an interactive command gets EOF instead of the
            // terminal. Unredirected, the child INHERITS the TUI's stdin: `git commit` opens $EDITOR,
            // `apt install` waits on y/n, and each one both steals the user's keystrokes from the
            // interface and blocks until the timeout with no output to explain why. EOF turns all of
            // that into a fast, legible failure the model can act on.
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = spec.WorkingDir ?? Environment.CurrentDirectory,
        };
        foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);
        if (spec.Env is not null)
            foreach (var kv in spec.Env) psi.Environment[kv.Key] = kv.Value;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Accumulated as well as logged — see ProcessResult.Stdout for why. StringBuilder rather
        // than string concat: these fire once per line, and a chatty command would otherwise
        // reallocate the whole buffer per line.
        //
        // Locked because both handlers fire on thread-pool threads, and StringBuilder is not
        // thread-safe: the same class of race that was silently corrupting the LOG file until it was
        // serialised. Capping inside the lock keeps a runaway command (`find /`) bounded in memory
        // rather than accumulating gigabytes to throw most of it away later.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputLock = new object();

        void Capture(StringBuilder sb, string line)
        {
            lock (outputLock)
            {
                if (sb.Length >= MaxCapturedChars) return;
                sb.Append(line).Append('\n');
            }
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { ctx.Log(e.Data); Capture(stdout, e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { ctx.Log(JobLogLevel.Warning, e.Data); Capture(stderr, e.Data); } };

        process.Start();

        // Close stdin IMMEDIATELY. Redirecting it without closing it is worse than not redirecting:
        // the child then blocks on a pipe that no one will ever write to. Closed, a read returns EOF
        // and the command fails in milliseconds with a message the model can act on.
        try { process.StandardInput.Close(); }
        catch (Exception) { /* already gone: the process exited before we got here */ }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The monitor's Updated event fires on its own Timer thread; ctx.ReportResources just
        // re-raises for whoever is subscribed (the UI marshals onto its own thread from there —
        // this call site does not know or care about the UI thread). Disposed in the same scope
        // as `process` below, fire-and-forget (Dispose only stops the Timer, no wait involved).
        using var resourceMonitor = new ProcessResourceMonitor(process);
        resourceMonitor.Updated += (_, snapshot) => ctx.ReportResources(snapshot);

        // Link the caller's ct with a timeout token so either kills the tree.
        using var timeoutCts = spec.TimeoutSeconds is int secs
            ? new CancellationTokenSource(TimeSpan.FromSeconds(secs))
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested; // distinguish timeout from external cancel
            TryKillTree(process);
            // Give WaitForExit a brief unconditional window so ExitCode is available.
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* best effort */ }
        }

        // WaitForExit(void) flushes the async output handlers so trailing lines are delivered.
        try { process.WaitForExit(); } catch { /* already exited */ }

        int exitCode;
        try { exitCode = process.ExitCode; } catch { exitCode = -1; }
        // Marked when cut, matching JobDigest's rule: a model must KNOW text was elided, or it
        // reasons confidently about output it never saw.
        string Finish(StringBuilder sb)
        {
            lock (outputLock)
                return sb.Length >= MaxCapturedChars
                    ? sb.ToString() + $"\n[... output truncated at {MaxCapturedChars:N0} characters; full text in the job log ...]"
                    : sb.ToString();
        }

        return new ProcessResult(exitCode, timedOut, Finish(stdout), Finish(stderr));
    }

    private static void TryKillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* race: already gone */ }
    }
}
