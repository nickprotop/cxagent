using CxAgent.Core.Models;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Themes;
using Ctl = SharpConsoleUI.Builders.Controls;
using CxAgent.Core.Helpers;

namespace CxAgent.UI;

/// <summary>
/// One job's block: a CollapsiblePanel whose border ColorRole reflects the job state, with a header
/// line (status icon · name · elapsed · one-liner), a progress bar, a Failed-state actions row
/// (Diagnose/Retry/Skip), and a Running-state live resource readout. A composite — it ARRANGES
/// hardened children (no paint code), mirroring PanelControl : CollapsiblePanel.
/// </summary>
public sealed class JobBlockControl : CollapsiblePanel
{
    private const string DiagnoseLabel = "Diagnose";
    private const string RetryLabel = "Retry";
    private const string SkipLabel = "Skip";

    private readonly MarkupControl _header;
    private readonly ProgressBarControl _progress;
    private readonly MarkupControl _resources;

    private ButtonControl? _diagnoseBtn;
    private ButtonControl? _retryBtn;
    private ButtonControl? _skipBtn;

    public string JobId { get; private set; } = "";

    /// <summary>True while the Failed-state actions row (Diagnose/Retry/Skip) is showing.</summary>
    public bool ActionsVisible { get; private set; }

    /// <summary>Labels of the actions currently offered. Empty unless ActionsVisible.</summary>
    public IReadOnlyList<string> ActionLabels { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// The latest resource readout as human-readable text (e.g. "cpu 42% · mem 128 MB"). Empty
    /// whenever the job isn't Running — a finished job must never keep showing a frozen live figure.
    /// </summary>
    public string ResourceText { get; private set; } = "";

    /// <summary>Raised when the Diagnose button is clicked. The handler lands in a later task.</summary>
    public event EventHandler? DiagnoseRequested;

    /// <summary>Raised when the Retry button is clicked. The handler lands in a later task.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>Raised when the Skip button is clicked. The handler lands in a later task.</summary>
    public event EventHandler? SkipRequested;

    public JobBlockControl()
    {
        _header = new MarkupControl(new List<string> { "" });
        _progress = new ProgressBarControl { MaxValue = 1.0, Value = 0 };
        _resources = new MarkupControl(new List<string> { "" }) { Visible = false };
        AddControl(_header);
        AddControl(_progress);
        AddControl(_resources);
    }

    public static ColorRole RoleFor(JobState state) => state switch
    {
        JobState.Running => ColorRole.Info,
        JobState.Succeeded => ColorRole.Success,
        JobState.Failed => ColorRole.Danger,
        JobState.Paused => ColorRole.Warning,
        _ => ColorRole.Default,   // Pending / Queued / Cancelled / Skipped
    };

    public void Update(Job job)
    {
        JobId = job.Id;
        Title = $"{Icon(job.State)} {job.DisplayName}{Elapsed(job)}";
        ColorRole = RoleFor(job.State);

        var oneLiner = job.ProgressMessage ?? StateWord(job.State);
        // THROUGH THE ROLE, like ColorRole above it: a literal grey is the same value in every
        // theme, and the one-liner sits under a header whose colour the theme does choose.
        _header.SetContent(new List<string> { $"[{ColorScheme.MutedMarkup}]{oneLiner}[/]" });

        if (job.Progress is { } p)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = System.Math.Clamp(p, 0, 1);
            _progress.Visible = true;
        }
        else if (job.State == JobState.Running)
        {
            _progress.IsIndeterminate = true;
            _progress.Visible = true;
        }
        else
        {
            _progress.Visible = false;
        }

        UpdateActions(job.State);

        // A finished job must never keep showing a frozen live CPU/mem figure — clear the moment
        // it leaves Running, not just on the next ShowResources call (which may never come).
        if (job.State != JobState.Running)
            ClearResources();
    }

    /// <summary>Renders the latest CPU/memory sample as human-readable text (never raw bytes).</summary>
    public void ShowResources(ResourceSnapshot snapshot)
    {
        // Truncate (not round) the CPU percent: a live gauge that rounds 42.5 up to "43%" reads as
        // more precise than it is, and a round-half-to-even vs. away-from-zero mismatch is a
        // needless source of off-by-one test flakiness for a number nobody needs to the decimal.
        int cpu = (int)snapshot.CpuPercent;
        ResourceText = $"cpu {cpu}% · mem {FormatBytes(snapshot.MemoryBytes)}";
        _resources.SetContent(new List<string> { $"[grey]{ResourceText}[/]" });
        _resources.Visible = true;
    }

    private void ClearResources()
    {
        if (ResourceText.Length == 0) return;
        ResourceText = "";
        _resources.SetContent(new List<string> { "" });
        _resources.Visible = false;
    }

    private static string FormatBytes(long bytes)
    {
        const double Mb = 1024.0 * 1024.0;
        const double Gb = Mb * 1024.0;
        if (bytes >= Gb) return $"{DisplayNumber.Fixed(bytes / Gb, 1)} GB";
        return $"{bytes / Mb:0} MB";
    }

    private void UpdateActions(JobState state)
    {
        bool failed = state == JobState.Failed;
        if (failed == ActionsVisible) return;   // no transition — buttons already match

        if (failed)
        {
            _diagnoseBtn = Ctl.Button(DiagnoseLabel).Build();
            _retryBtn = Ctl.Button(RetryLabel).Build();
            _skipBtn = Ctl.Button(SkipLabel).Build();
            _diagnoseBtn.Click += (_, _) => DiagnoseRequested?.Invoke(this, EventArgs.Empty);
            _retryBtn.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
            _skipBtn.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
            AddControl(_diagnoseBtn);
            AddControl(_retryBtn);
            AddControl(_skipBtn);
            ActionLabels = new[] { DiagnoseLabel, RetryLabel, SkipLabel };
        }
        else
        {
            if (_diagnoseBtn is not null) RemoveControl(_diagnoseBtn);
            if (_retryBtn is not null) RemoveControl(_retryBtn);
            if (_skipBtn is not null) RemoveControl(_skipBtn);
            _diagnoseBtn = _retryBtn = _skipBtn = null;
            ActionLabels = Array.Empty<string>();
        }

        ActionsVisible = failed;
    }

    private static string Icon(JobState s) => s switch
    {
        JobState.Running => "◐",     // ◐
        JobState.Succeeded => "✓",   // ✓
        JobState.Failed => "✗",      // ✗
        JobState.Paused => "‖",      // ‖
        _ => "○",                     // ○
    };

    private static string StateWord(JobState s) => s.ToString().ToLowerInvariant();

    private static string Elapsed(Job job)
    {
        if (job.StartedAt is not { } start) return "";
        var end = job.CompletedAt ?? DateTimeOffset.UtcNow;
        var secs = (end - start).TotalSeconds;
        return secs >= 0 ? $" — {DisplayNumber.Fixed(secs, 1)}s" : "";
    }
}
