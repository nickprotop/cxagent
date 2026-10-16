using CxAgent.Core.Config;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// Asked once a change is saved that would break some open tabs' prompt cache — their model changed:
/// apply it now, compress first, wait for each tab's next clear or compaction, or leave running tabs
/// alone.
///
/// <para>THE COST IS NAMED, because it is the reason to ask: each tab and how much context it would
/// re-send. Tokens, not money — cxagent keeps no price table to turn one into the other.</para>
///
/// <para>ESC IS "ONLY NEW TABS". The file is already saved, so the question is only about running
/// tabs, and leaving them alone is the answer that spends nothing.</para>
/// </summary>
public static class ApplyToTabsDialog
{
    private static Window? _window;
    private static TaskCompletionSource<CacheChoice>? _answer;

    /// <summary>Whether the question is up — Escape answers it before anything under it.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>Answers "only new tabs" and closes. Returns whether it was open.</summary>
    public static bool CloseIfOpen()
    {
        if (_window is not { } window) return false;
        _answer?.TrySetResult(CacheChoice.NewTabsOnly);
        window.Close();
        return true;
    }

    private static readonly (CacheChoice Choice, string Label, string What)[] Options =
    [
        (CacheChoice.ApplyNow, "Apply now", "each tab re-sends its whole conversation once"),
        (CacheChoice.CompressThenApply, "Compress, then apply", "summarises each tab first; only the summary is re-sent"),
        (CacheChoice.AtNextClearOrCompression, "At next clear or compression", "applied when the tab is cleared or compacts — the cache is rebuilt then anyway"),
        (CacheChoice.NewTabsOnly, "Only new tabs", "open tabs keep the old setting until closed; new tabs and the next start get it"),
    ];

    /// <summary>Asks, and returns the choice; Esc is <see cref="CacheChoice.NewTabsOnly"/>.</summary>
    /// <param name="system">The window system.</param>
    /// <param name="what">One line saying what changed.</param>
    /// <param name="tabs">The tabs whose cache would break.</param>
    public static Task<CacheChoice> AskAsync(ConsoleWindowSystem system, string what, IReadOnlyList<Session> tabs)
    {
        if (_window is not null) return Task.FromResult(CacheChoice.NewTabsOnly);
        var answer = new TaskCompletionSource<CacheChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;

        var total = tabs.Sum(t => t.ContextUsed ?? 0);
        var window = new WindowBuilder(system)
            .WithTitle("Apply to open tabs?")
            .WithSize(Math.Min(86, Console.WindowWidth - 4), Math.Min(14 + tabs.Count + Options.Length * 2, Console.WindowHeight - 2))
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Movable(true)
            .Minimizable(false)
            .Build();

        var lines = new List<string>
        {
            MarkupParser.Escape(what) + " These open tabs would rebuild their prompt cache:",
            "",
        };
        foreach (var tab in tabs)
            lines.Add($"  [bold]{MarkupParser.Escape(TabName(tab))}[/]  [{ColorScheme.MutedMarkup}]· {MarkupParser.Escape(tab.InstanceName ?? "")} · {Tokens(tab.ContextUsed)} in context[/]");
        lines.Add("");
        window.AddControl(new MarkupControl(lines) { Wrap = true });

        var list = new ListControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (choice, label, whatItDoes) in Options)
        {
            // THE COST ONLY WHEN IT IS KNOWN: a tab that has not taken a turn has no measured context, and
            // "about 0 tokens" would read as free.
            var cost = choice == CacheChoice.ApplyNow && total > 0 ? $" — about {Tokens(total)} re-sent" : "";
            list.AddItem(new ListItem($"[bold]{label}[/]\n  [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(whatItDoes + cost)}[/]") { Tag = choice });
        }
        list.ItemActivated += (_, item) =>
        {
            if (item?.Tag is not CacheChoice choice) return;
            answer.TrySetResult(choice);
            window.Close();
        };
        window.AddControl(list);
        window.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]The file is saved either way. New tabs always get the change. Enter chooses · Esc = only new tabs[/]"])
        {
            StickyPosition = StickyPosition.Bottom,
            Wrap = true,
        });

        window.OnClosed += (_, _) =>
        {
            _answer?.TrySetResult(CacheChoice.NewTabsOnly);
            _answer = null;
            _window = null;
        };

        _window = window;
        system.AddWindow(window);
        system.SetActiveWindow(window);
        window.FocusManager.SetFocus(list, FocusReason.Programmatic);
        list.SelectedIndex = 0;
        return answer.Task;
    }

    private static string TabName(Session session) =>
        Path.GetFileName(session.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } folder ? folder : session.WorkingDirectory;

    private static string Tokens(int? tokens) => tokens switch
    {
        null => "unknown",
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M tokens",
        >= 1_000 => $"{tokens / 1000}K tokens",
        _ => $"{tokens} tokens",
    };
}
