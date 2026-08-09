using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>
/// Polls a job's log file and emits only the NEW tail lines each cycle. Runs on a background Task;
/// reads the file there and hands new lines to a caller-supplied callback (which marshals them onto
/// the UI thread). A read failure is swallowed (log I/O is diagnostic, never fatal — P2's stance).
/// Stops promptly on cancellation. This is an isolated unit — it touches no UI control.
/// </summary>
public sealed class LogTailPoller
{
    private readonly LogFileManager _logs;
    private readonly string _agentId;
    private readonly string _jobId;
    private readonly Action<IReadOnlyList<string>> _emit;
    private readonly int _tailLines;
    private readonly int _pollIntervalMs;
    // Total lines emitted so far, counted against the log's FULL length (not the tail window).
    // Counting against the window breaks once the log outgrows tailLines: the window's Count pins at
    // the cap while it slides forward, so "new lines" can never be detected again and the tail freezes.
    private int _emittedTotal;
    private bool _primed;        // first read emits only the tail window, not the whole backlog

    public LogTailPoller(LogFileManager logs, string agentId, string jobId,
        Action<IReadOnlyList<string>> emitNewLines, int tailLines = 20, int pollIntervalMs = 500)
    {
        _logs = logs;
        _agentId = agentId;
        _jobId = jobId;
        _emit = emitNewLines;
        _tailLines = tailLines;
        _pollIntervalMs = pollIntervalMs;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var all = await ReadAllSafe(ct);
            if (all is not null)
            {
                if (!_primed)
                {
                    // First successful read: emit only the last _tailLines (don't dump the backlog),
                    // but account for every line so subsequent appends are detected.
                    _primed = true;
                    var start = Math.Max(0, all.Count - _tailLines);
                    _emittedTotal = all.Count;
                    if (all.Count > start) _emit(all.Skip(start).ToList());
                }
                else if (all.Count > _emittedTotal)
                {
                    var fresh = all.Skip(_emittedTotal).ToList();
                    _emittedTotal = all.Count;
                    _emit(fresh);
                }
                else if (all.Count < _emittedTotal)
                {
                    // Log truncated/rotated under us — resync rather than stall forever.
                    _emittedTotal = all.Count;
                }
            }
            try { await Task.Delay(_pollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Returns ALL lines currently in the log, or null if unreadable. Never throws.
    // The tail window is applied by the caller on the FIRST read only; after that every appended
    // line is emitted, so the window is a backlog cap rather than a cap on live output.
    private async Task<IReadOnlyList<string>?> ReadAllSafe(CancellationToken ct)
    {
        try
        {
            var text = await _logs.ReadAsync(_agentId, _jobId, "log");
            if (string.IsNullOrEmpty(text)) return System.Array.Empty<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            // The trailing '\n' yields a final empty element — drop it.
            return lines.Length > 0 && lines[^1].Length == 0
                ? lines[..^1].ToList() : lines.ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }   // diagnostic read failure — swallow, loop survives
    }
}
