using System.Globalization;
using CxAgent.Core.Models;

namespace CxAgent.Core.Execution;

/// <summary>
/// Turns what a running call reports — progress, streamed text — into the fields its row draws:
/// <see cref="Job.Progress"/> and <see cref="Job.ProgressMessage"/> on the header,
/// <see cref="Job.ProgressBody"/> behind the expand.
///
/// <para>FIELDS, NOT EVENTS TO THE UI. The row is repainted from the transcript's one-second tick
/// while a call is in flight, so a report only has to leave the job holding the latest state; a
/// plugin streaming a thousand chunks a second costs a thousand field writes, not a thousand
/// enqueued repaints. It also keeps the reporting thread — for a native plugin, the one reader
/// every call's reply waits on — from ever blocking on the UI.</para>
///
/// <para>LOCKED, because a managed plugin may report from several threads of its own at once, and
/// the line window below is a read-modify-write. The fields are written whole, so the UI thread
/// reading them on the tick sees one report or the next, never half of one.</para>
/// </summary>
public sealed class JobReportFeed(Job job)
{
    /// <summary>How many streamed lines a running row keeps. Older ones are dropped from the top and
    /// counted: the call's result is what the model reads, and this is only a window onto the work
    /// while it happens.</summary>
    public const int KeptLines = 200;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private string _partial = string.Empty;
    private long _dropped;
    private bool _wroteProgress;
    private bool _wroteText;
    private bool _finished;

    /// <summary>"45% · counted 9 of 20", or "45%" with no message.</summary>
    public void Progress(double percent, string? message)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var text = clamped.ToString("0", CultureInfo.InvariantCulture) + "%"
                   + (string.IsNullOrWhiteSpace(message) ? "" : " · " + message);
        lock (_gate)
        {
            if (_finished) return;
            job.Progress = clamped;
            job.ProgressMessage = text;
            _wroteProgress = true;
        }
    }

    /// <summary>
    /// Appends a chunk of streamed text. A chunk is not a line: text arrives however the plugin
    /// produced it, so a line in three pieces counts once, when its newline arrives.
    /// </summary>
    public void Text(string delta)
    {
        lock (_gate)
        {
            if (_finished) return;
            _wroteText = true;
            var pieces = delta.Split('\n');
            _partial += pieces[0];
            for (var i = 1; i < pieces.Length; i++)
            {
                _lines.Enqueue(_partial);
                _partial = pieces[i];
                if (_lines.Count > KeptLines)
                {
                    _lines.Dequeue();
                    _dropped++;
                }
            }

            job.ProgressBody = Render();
        }
    }

    /// <summary>
    /// The call has ended: clears what this feed wrote, so a settled row says how the call ended
    /// rather than how far along it was, and ignores any report after it. ONLY WHAT IT WROTE — the
    /// same fields carry a sub-agent's own progress, which this must not erase. Safe to call twice.
    /// </summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            if (_wroteProgress)
            {
                job.Progress = null;
                job.ProgressMessage = null;
            }

            if (_wroteText) job.ProgressBody = null;
        }
    }

    private string Render()
    {
        var kept = _lines.Count == 0 ? string.Empty : string.Join("\n", _lines) + "\n";
        if (_dropped == 0) return kept + _partial;

        var noun = _dropped == 1 ? "line" : "lines";
        return $"… {_dropped.ToString("N0", CultureInfo.InvariantCulture)} earlier {noun} not shown\n"
               + kept + _partial;
    }
}
