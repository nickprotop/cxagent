using System.Text;
using System.Diagnostics;
using CxAgent.Core.Jobs;

namespace CxAgent.Core.Execution;

/// <param name="FileName">The executable to run.</param>
/// <param name="Arguments">Its arguments, already split — passed through ArgumentList, so no quoting.</param>
/// <param name="WorkingDir">Where it runs, or null for the process's own directory.</param>
/// <param name="Env">Variables added to the child's environment, or null to inherit unchanged.</param>
/// <param name="TimeoutSeconds">How long before the tree is killed, or null for no deadline.</param>
/// <param name="SpillDir">
/// Where an over-long stream's full text is written, or null to write none and truncate outright.
///
/// <para>ON THE SPEC RATHER THAN ASKED OF THE CONTEXT, because <see cref="Jobs.IJobContext"/> is the
/// PLUGIN-FACING interface: every plugin that implements it would have to grow a member to answer a
/// question only the built-in shell path asks. The caller that knows where a session keeps its files
/// is the one that already builds the spec.</para>
///
/// <para>NULL IS A REAL CASE, not a degenerate one. A headless run, a test, or an embedder that
/// wired no log directory has nowhere to put a file — and a runner that invented one (temp, say)
/// would be writing files nobody sweeps on behalf of a caller that never asked for any.</para>
/// </param>
public record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDir = null,
    IReadOnlyDictionary<string, string>? Env = null,
    int? TimeoutSeconds = null,
    string? SpillDir = null);

/// <summary>
/// Where a stream's FULL text went, when it did not fit inline, and how big that full text was.
///
/// <para>ONE RECORD, NOT TWO FIELDS ON <see cref="ProcessResult"/>, because a path with no size is
/// half the story: a reader deciding whether to open the file needs to know what it is opening, so
/// making a caller set one without the other is a bug the type system should catch rather than a
/// convention to remember.</para>
/// </summary>
/// <param name="Path">Where the whole stream was written.</param>
/// <param name="TotalBytes">The full text's size, before it was cut down to the inline tail.</param>
public sealed record Spill(string Path, long TotalBytes);

/// <param name="Stdout">
/// The LAST <see cref="ProcessRunner.MaxCapturedChars"/> characters of what the command printed.
///
/// <para>Captured as well as logged because the log file is for the USER and this is for the next
/// JOB. Without it <c>{{some_shell_job.stdout}}</c> could never resolve: ShellJobExecutor had nothing
/// but an exit code to put in its output bag, so any goal that shelled out and fed the result
/// onward failed. A live drive of "list ~/bin. what it does?" reported the directory EMPTY, because
/// from the model's side the listing genuinely produced nothing.</para>
/// </param>
/// <param name="Stderr">Diagnostics, same cap and same end. Separate from Stdout so a job can
/// reference either.</param>
/// <param name="ExitCode">The process's exit code.</param>
/// <param name="TimedOut">Whether it was killed for exceeding its deadline rather than exiting.</param>
public record ProcessResult(int ExitCode, bool TimedOut, string Stdout = "", string Stderr = "")
{
    /// <summary>Set when stdout did not fit inline and its full text was written out; null when the
    /// inline extract IS the whole of it, or when no <c>SpillDir</c> was given.</summary>
    public Spill? StdoutSpill { get; init; }

    /// <summary>The same for stderr, separately: a build that fails writes its diagnostics to one
    /// stream and its progress to the other, and either can be the one that overflowed.</summary>
    public Spill? StderrSpill { get; init; }

    /// <summary>
    /// Whichever stream had to be written out — stdout's when both did.
    ///
    /// <para>For the CALLER THAT WANTS ONE PATH TO NAME, which is the common one: a tool result says
    /// "the rest is here" once, and stdout is the stream a command's substance is on. A caller that
    /// genuinely needs to distinguish reads the two above.</para>
    /// </summary>
    public Spill? Spill => StdoutSpill ?? StderrSpill;
}

/// <summary>
/// Runs a child process, streaming stdout/stderr lines to the job context. Reads output
/// ASYNCHRONOUSLY (event-based) so a full pipe buffer never deadlocks the child. A per-run
/// timeout or the external cancellation token kills the whole process tree.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// How much of each stream travels inline before the rest goes to a file.
    ///
    /// <para>Matches ToolBindings.MaxToolResultChars — both bound the same thing, text on its way
    /// into a model's context, and a command's output feeding a downstream job is subject to exactly
    /// that pressure. It is not 64,000 (what a trigger's watch allows) for the reason
    /// TelemetryReports.OutputCap is 4,096: a file already holds the rest, so the inline budget only
    /// has to carry the part worth reading without being asked.</para>
    ///
    /// <para>AND IT IS THE TAIL THAT SURVIVES. Truncation always bets on which end matters, and for a
    /// command the end is where the answer is: a failing build's error and summary are last, while a
    /// head shows the compiler banner. The file removes the bet for anything the tail did not
    /// carry.</para>
    /// </summary>
    public const int MaxCapturedChars = 8192;

    /// <summary>
    /// One stream's inline tail, plus the file holding all of it once it outgrew that tail.
    ///
    /// <para>NEITHER "KEEP EVERYTHING AND SLICE AT THE END" NOR "ALWAYS WRITE A FILE". Keeping
    /// everything is unbounded — <c>find /</c> would accumulate gigabytes in memory to throw most of
    /// it away, which is the runaway the old cap-as-you-go existed to prevent. Always writing is a
    /// file per <c>git status</c>, most of them empty of anything the inline text did not already
    /// say. So: a bounded buffer, and the file opened only by the line that first overflows it —
    /// which is also the line that proves the file is worth having.</para>
    ///
    /// <para>THE FILE GETS THE WHOLE STREAM, head included. The overflowing line flushes the buffer
    /// into it before appending itself, so the file a reader is pointed at can be read on its own
    /// rather than stitched back onto the inline extract.</para>
    /// </summary>
    private sealed class StreamCapture(string? spillDir, string name) : IDisposable
    {
        private readonly StringBuilder _tail = new();
        private StreamWriter? _spillWriter;
        private string? _spillPath;
        private long _totalBytes;
        private bool _overflowed;

        /// <summary>Every line, in order — appended under the caller's lock.</summary>
        public void Add(string line)
        {
            _totalBytes += Encoding.UTF8.GetByteCount(line) + 1;   // +1 for the '\n' appended below

            // ONCE THE FILE IS OPEN, EVERY LINE GOES STRAIGHT TO IT, and the buffer below keeps only
            // its tail. One entry point rather than a "first line" and a "subsequent line" method:
            // a caller that has to know which phase it is in will eventually get it wrong.
            if (_spillWriter is not null) _spillWriter.Write(line + '\n');

            _tail.Append(line).Append('\n');
            if (_tail.Length <= MaxCapturedChars) return;

            _overflowed = true;

            // THE FIRST OVERFLOW OPENS THE FILE AND HANDS IT THE BUFFER, which at this instant still
            // holds every line from the start — so nothing is lost across the transition from "fits
            // inline" to "does not", which a file opened any later would drop on the floor.
            if (_spillWriter is null && TryOpenSpill())
                _spillWriter!.Write(_tail.ToString());

            // Trimmed back to the CAP rather than emptied, so memory stays bounded however long the
            // command runs while the buffer is still a tail at exit — emptying it on each overflow
            // would leave only whatever happened to arrive after the last one.
            var excess = _tail.Length - MaxCapturedChars;
            if (excess > 0) _tail.Remove(0, excess);
        }

        private bool TryOpenSpill()
        {
            if (_spillWriter is not null) return true;
            if (spillDir is null) return false;
            try
            {
                Directory.CreateDirectory(spillDir);
                _spillPath = Path.Combine(spillDir, name);
                _spillWriter = new StreamWriter(_spillPath, append: false, Encoding.UTF8);
                return true;
            }
            catch (Exception)
            {
                // A FILE WE CANNOT WRITE IS NOT A COMMAND WE CANNOT REPORT. The tail still travels;
                // with no path to name, a size would tell the reader nothing either, so the result
                // simply carries no Spill and the marker says only that text was cut.
                _spillPath = null;
                _spillWriter = null;
                spillDir = null;   // Do not retry per line: a full or read-only disk fails every time.
                return false;
            }
        }

        /// <summary>
        /// The inline text, marked at the FRONT when text was cut.
        ///
        /// <para>AT THE FRONT BECAUSE THE ELISION HAPPENED THERE. A trailing "truncated" note on a
        /// tail tells a model the output ENDS mid-stream, so it reasons about a command that never
        /// finished rather than one whose beginning it did not see.</para>
        /// </summary>
        public string Text()
        {
            var text = _tail.ToString();
            if (!_overflowed) return text;

            var where = _spillPath is null
                ? "no file could be written"
                : $"full output in {_spillPath}";
            return $"[... earlier output elided; showing the last {MaxCapturedChars:N0} characters, "
                 + $"{where} ...]\n" + text;
        }

        /// <summary>The file and the true size, or null when nothing was written.</summary>
        public Spill? Result() => _spillPath is null ? null : new Spill(_spillPath, _totalBytes);

        /// <summary>Closed before the result is read, so the file on disk is complete when the
        /// caller is handed its path — a reader pointed at a half-flushed file sees a short one.</summary>
        public void Dispose()
        {
            try { _spillWriter?.Dispose(); }
            catch (Exception) { /* Diagnostic file; a failed close does not fail the command. */ }
            _spillWriter = null;
        }
    }

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

        // Accumulated as well as logged — see ProcessResult.Stdout for why.
        //
        // Locked because both handlers fire on thread-pool threads and neither the StringBuilder
        // inside a capture nor its StreamWriter is thread-safe: the same class of race that was
        // silently corrupting the LOG file until it was serialised. The lock also keeps a runaway
        // command (`find /`) bounded in memory, since the capture trims inside it.
        //
        // A NAME PER STREAM, NOT A TIMESTAMP OR A PID: this directory is one job's, so the stream is
        // the only thing that distinguishes the two files, and a stable name means a re-run of the
        // same job overwrites its own spill instead of accumulating one per attempt.
        using var stdout = new StreamCapture(spec.SpillDir, "stdout.spill");
        using var stderr = new StreamCapture(spec.SpillDir, "stderr.spill");
        var outputLock = new object();

        void Capture(StreamCapture capture, string line)
        {
            lock (outputLock) capture.Add(line);
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

        // CLOSED BEFORE THE PATHS ARE HANDED OUT, so a reader opening the file named in the result
        // finds all of it rather than whatever had happened to flush. The `using` above would close
        // them on the way out of the method — too late, since the result carries the paths.
        //
        // Under the lock, because a handler for a line still in flight would otherwise write to a
        // writer being disposed. Both handlers are quiet by now (WaitForExit above flushes them), but
        // "by now" is a timing argument and the lock is not.
        string outText, errText;
        Spill? outSpill, errSpill;
        lock (outputLock)
        {
            stdout.Dispose();
            stderr.Dispose();
            outText = stdout.Text();
            errText = stderr.Text();
            outSpill = stdout.Result();
            errSpill = stderr.Result();
        }

        return new ProcessResult(exitCode, timedOut, outText, errText)
        {
            StdoutSpill = outSpill,
            StderrSpill = errSpill,
        };
    }

    private static void TryKillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* race: already gone */ }
    }
}
