using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace CxAgent.UI;

/// <summary>
/// Hosts one JobBlockControl per job (keyed by Job.Id) in a scrollable panel. Owns the expand/collapse
/// behavior and the single active log-tail poller (collapse-others-on-expand keeps it to one). The
/// SetJobs/UpdateJob methods run on the UI thread (the marshalling is JobPanelSink's job).
/// </summary>
public sealed class JobPanelControl : ScrollablePanelControl
{
    private readonly ConsoleWindowSystem _system;
    private readonly LogFileManager _logs;
    private readonly Dictionary<string, JobBlockControl> _blocks = new();

    // The single active expanded-block tail (collapse-others keeps it to one).
    private JobBlockControl? _expanded;
    private CancellationTokenSource? _tailCts;
    private Task? _tailTask;
    private MultilineEditControl? _tailBody;

    // Copilot mode (P9 Task 2): a standing banner control, inserted first so it always sits above
    // every job block regardless of when SetJobs last ran. Built once in the constructor (not
    // lazily) so SetDraftMode can be called before the first SetJobs without a null check.
    private readonly MarkupControl _draftBanner = new(new List<string> { "" }) { Visible = false };

    public int BlockCount => _blocks.Count;

    /// <summary>
    /// True while the goal sitting behind this panel is parked in GoalState.Draft (P9 copilot mode)
    /// awaiting F9/Esc. The whole point of Task 2: the user must be able to tell at a glance that
    /// these blocks are shown-but-not-running, so this drives a standing banner rather than relying
    /// on job state alone (Pending/Queued look identical whether copilot is on or off).
    /// </summary>
    public bool IsDraftMode { get; private set; }

    /// <summary>
    /// Raised when a job block's Diagnose/Retry/Skip button is clicked, carrying the owning
    /// Job.Id. Forwarding only — this panel does not know how to diagnose, retry, or skip a job;
    /// those handlers are wired up by whoever owns the goal/job lifecycle (a later task).
    /// </summary>
    public event EventHandler<string>? DiagnoseRequested;
    public event EventHandler<string>? RetryRequested;
    public event EventHandler<string>? SkipRequested;

    public JobPanelControl(ConsoleWindowSystem system, LogFileManager logs)
    {
        _system = system;
        _logs = logs;
        AddControl(_draftBanner);
    }

    public bool TryGetBlock(string jobId, out JobBlockControl block) => _blocks.TryGetValue(jobId, out block!);

    public void SetJobs(IReadOnlyList<Job> jobs)
    {
        StopTail();
        ClearContents();
        _blocks.Clear();
        // StopTail clears _tailBody/_tailCts but NOT _expanded, and ClearContents above just detached
        // whatever control _expanded still points at — left unset, a later expand/collapse on a
        // rebuilt block would ReferenceEquals-compare against that stale, detached control (harmless
        // today: OnBlockExpandedChanged's checks just fail and do one extra no-op StopTail, but it
        // keeps a discarded JobBlockControl alive for no reason). Task 11 review round 2, N5 — this is
        // the first call site to invoke SetJobs a second time in one session (the I2 InsertBefore
        // re-sync), so it's the first time the stale reference was actually reachable.
        _expanded = null;
        // ClearContents() just detached _draftBanner along with every job block above — re-add it
        // FIRST so a draft's plan (SetJobs runs while still in GoalState.Draft — see AgentHost) is
        // shown with the banner already sitting above the blocks, not flashing in after the fact.
        AddControl(_draftBanner);
        foreach (var job in jobs)
        {
            var block = new JobBlockControl();
            block.Update(job);
            block.ExpandedChanged += (_, expanded) => OnBlockExpandedChanged(block, job, expanded);
            block.DiagnoseRequested += (_, _) => DiagnoseRequested?.Invoke(this, job.Id);
            block.RetryRequested += (_, _) => RetryRequested?.Invoke(this, job.Id);
            block.SkipRequested += (_, _) => SkipRequested?.Invoke(this, job.Id);
            _blocks[job.Id] = block;
            AddControl(block);
        }
    }

    public void UpdateJob(Job job)
    {
        if (_blocks.TryGetValue(job.Id, out var block))
            block.Update(job);
    }

    /// <summary>
    /// Flips the standing draft banner (P9 Task 2). This is the ONLY thing that makes a drafted plan
    /// look different from a running one — the blocks themselves are otherwise identical, since jobs
    /// sit in ordinary Pending/Queued states whether copilot is on or off.
    /// </summary>
    public void SetDraftMode(bool isDraft)
    {
        IsDraftMode = isDraft;
        _draftBanner.SetContent(new List<string>
        {
            isDraft ? "[yellow]▸ DRAFT — plan shown, nothing is running. F9 approve · Esc discard.[/]" : ""
        });
        _draftBanner.Visible = isDraft;
    }

    /// <summary>
    /// Renders a resource sample on the named job's block, if it's still present. Callers (a
    /// UI-thread sink mirroring JobPanelSink) are responsible for the EnqueueOnUIThread marshal —
    /// this method assumes it is already running on the UI thread, exactly like UpdateJob above.
    /// </summary>
    public void UpdateResources(string jobId, ResourceSnapshot snapshot)
    {
        if (_blocks.TryGetValue(jobId, out var block))
            block.ShowResources(snapshot);
    }

    private void OnBlockExpandedChanged(JobBlockControl block, Job job, bool expanded)
    {
        if (expanded)
        {
            // Collapse any other expanded block first (stops its tail).
            if (_expanded is not null && !ReferenceEquals(_expanded, block))
            {
                StopTail();
                _expanded.Collapse();
            }
            _expanded = block;
            StartTail(block, job);
        }
        else if (ReferenceEquals(_expanded, block))
        {
            StopTail();
            _expanded = null;
        }
    }

    private void StartTail(JobBlockControl block, Job job)
    {
        StopTail();
        _tailBody = new MultilineEditControl(viewportHeight: 6) { ReadOnly = true };
        block.AddControl(_tailBody);

        _tailCts = new CancellationTokenSource();
        var body = _tailBody;
        var token = _tailCts.Token;
        var poller = new LogTailPoller(_logs, job.AgentId, job.Id,
            newLines => _system.EnqueueOnUIThread(() =>
            {
                // StopTail no longer waits for the poller (that self-deadlocked), so an emit already
                // queued when it was cancelled can still land here. Two guards make that harmless:
                // the token, and an identity check that this is still the ACTIVE tail body — without
                // the latter a late emit from a previous expansion would append to a control that has
                // been detached from its block, or worse, to the wrong job's tail.
                if (token.IsCancellationRequested || !ReferenceEquals(_tailBody, body)) return;
                var existing = body.Content ?? "";
                body.Content = existing.Length == 0
                    ? string.Join("\n", newLines)
                    : existing + "\n" + string.Join("\n", newLines);
            }));
        _tailTask = poller.RunAsync(token);
    }

    // Cancels the active poller (without waiting — see below) and detaches the tail body.
    // Safe to call when nothing is tailing.
    private void StopTail()
    {
        if (_tailCts is not null)
        {
            // Cancel and WALK AWAY — never block waiting for the poller.
            //
            // This runs on the UI THREAD (SetJobs/UpdateJob arrive via JobPanelSink's
            // EnqueueOnUIThread, and expand/collapse is a UI event). The poller's emit callback is
            // itself an EnqueueOnUIThread, so it needs the UI thread to make progress. Blocking here
            // with .GetAwaiter().GetResult() therefore self-deadlocks: the UI thread waits for the
            // poller, the poller waits for the UI thread. Observed live — submitting a SECOND goal
            // while a log tail was expanded wedged the app with "UI UNRESPONSIVE", and even Ctrl+Q
            // was dead because its handler runs on the same blocked thread.
            //
            // Dropping the wait is safe: the token is already cancelled, LogTailPoller checks it each
            // loop and on its Task.Delay, and its emit callback is a no-op once the body it targets is
            // detached below. A cancelled poller lingering for at most one poll interval costs nothing.
            var cts = _tailCts;
            var task = _tailTask;
            cts.Cancel();
            // Dispose the CTS only after the poller has actually observed the cancellation, off the
            // UI thread — disposing it here could race the poller's own token check.
            _ = task?.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default)
                ?? Task.Run(() => cts.Dispose());
            _tailCts = null;
            _tailTask = null;
        }
        if (_tailBody is not null && _expanded is not null)
        {
            _expanded.RemoveControl(_tailBody);
            _tailBody = null;
        }
    }
}
