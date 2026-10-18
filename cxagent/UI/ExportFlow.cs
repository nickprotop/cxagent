using System.Globalization;
using CxAgent.Core.Commands;
using CxAgent.Core.Export;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Core;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// From an export request — F12, /export, /sessions export — to a file or the clipboard: what the dialog is opened on,
/// and what happens once it is answered.
///
/// <para>EVERY ROUTE ENDS IN ONE DIALOG with one renderer behind it. A live session, an agent tab's agent and a past
/// session differ only in where their history is read from and what the top line says.</para>
/// </summary>
/// <param name="system">The window system the dialog opens in.</param>
/// <param name="paths">Where cxagent keeps its sessions.</param>
/// <param name="configPath">config.json, where the dialog's choices are remembered.</param>
public sealed class ExportFlow(ConsoleWindowSystem system, AppPaths paths, string configPath)
{
    /// <summary>The longest text a toast shows whole.</summary>
    private const int ToastWidth = 60;

    /// <summary>The conversation in a session tab.</summary>
    public Task ForSessionAsync(Session session, ExportPrefill prefill)
    {
        // SAVED FIRST: the export reads its title and spend from the session's folder, and the newest of those is in
        // memory until a turn ends.
        session.SaveNow();
        if (session.SessionId is not { } agentId)
        {
            Toast("Nothing to export yet.", NotificationSeverity.Info);
            return Task.CompletedTask;
        }

        var header = SessionHeader.Read(new SessionFolder(paths, agentId));
        var source = new Lazy<ExportSource?>(() => ExportSource.Load(paths, agentId, session.SpendLabel));
        return AskAsync(new ExportDialogRequest(
            SessionLine(header?.Title, header?.UpdatedAt, session.WorkingDirectory),
            IsAgent: false,
            Renderer(source),
            ExportTarget.DefaultFileName(header?.Title, DateTimeOffset.Now),
            session.WorkingDirectory,
            ExportPreference.Read(configPath),
            prefill,
            session.IsBusy ? "A turn is running; its unfinished answer is not included." : null));
    }

    /// <summary>The agent in an agent tab, alone.</summary>
    public Task ForAgentAsync(AgentTab tab)
    {
        var kept = tab.Kept;
        if (kept.Agent.Agent.History?.Path is not { } historyPath || Path.GetDirectoryName(historyPath) is not { } dir)
        {
            Toast("This agent keeps no history to export.", NotificationSeverity.Info);
            return Task.CompletedTask;
        }

        var parent = tab.Session.SessionId is { } parentId ? SessionHeader.Read(new SessionFolder(paths, parentId)) : null;
        var header = new ExportHeader(kept.Agent.Agent.Id, parent?.Title, tab.Session.WorkingDirectory, null, null, 0, 0,
            tab.Session.SpendLabel, kept.Name, kept.TypeName);
        var source = new Lazy<ExportSource?>(() => ExportSource.LoadDir(dir, header));
        return AskAsync(new ExportDialogRequest(
            $"[bold]{MarkupParser.Escape(kept.Name)}[/] [{ColorScheme.MutedMarkup}]({MarkupParser.Escape(kept.TypeName)}) · "
                + $"sub-agent of {MarkupParser.Escape(ClipTitle(parent?.Title ?? "this session", 66 - kept.Name.Length - kept.TypeName.Length))}[/]",
            IsAgent: true,
            Renderer(source),
            ExportTarget.DefaultFileName(kept.Name, DateTimeOffset.Now),
            tab.Session.WorkingDirectory,
            ExportPreference.Read(configPath),
            ExportPrefill.None,
            tab.CanStop ? "This agent is still working; its unfinished stretch is not included." : null));
    }

    /// <summary>A session from /sessions, without resuming it.</summary>
    /// <param name="current">The session the command was typed in — where a file goes when the past one's folder is gone.</param>
    /// <param name="agentId">The past session's agent id.</param>
    public Task ForPastAsync(Session current, string agentId)
    {
        var header = SessionHeader.Read(new SessionFolder(paths, agentId));
        var folder = header?.WorkingDir is { } dir && Directory.Exists(dir) ? dir : current.WorkingDirectory;
        var source = new Lazy<ExportSource?>(() => ExportSource.Load(paths, agentId));
        return AskAsync(new ExportDialogRequest(
            SessionLine(header?.Title, header?.UpdatedAt, header?.WorkingDir),
            IsAgent: false,
            Renderer(source),
            ExportTarget.DefaultFileName(header?.Title, header?.UpdatedAt ?? DateTimeOffset.Now),
            folder,
            ExportPreference.Read(configPath),
            ExportPrefill.None));
    }

    // LOADED ONCE, ON THE FIRST RENDER — off the UI thread, where the dialog renders — and reused for every change of
    // options after it: the export is of the conversation as it stood when the dialog opened.
    private static Func<ExportOptions, ExportResult> Renderer(Lazy<ExportSource?> source) =>
        options => source.Value is { } loaded
            ? ExportMarkdown.Render(loaded, options)
            : new ExportResult("", new ExportStats(0, 0, 0));

    private async Task AskAsync(ExportDialogRequest request)
    {
        var decision = await ExportDialog.AskAsync(new ExportDialogHost(system, Deliver, system.EnqueueOnUIThread), request);
        if (decision is null) return;

        var size = ExportTarget.Size(decision.Result.Stats.Bytes);
        Toast(decision.ToClipboard
            ? $"Copied {size} as markdown"
            : Clip($"Exported {size} to {Path.GetFileName(decision.Path)}"), NotificationSeverity.Success);

        try
        {
            ExportPreference.Save(configPath, new ExportChoices(decision.Options.Parts, decision.ToClipboard, decision.Open));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // THE EXPORT HAPPENED; only remembering how did not, and the next dialog opening on the defaults says so.
        }
    }

    /// <summary>Copies or writes the export, and opens it when asked. Null when done; otherwise why not.</summary>
    private static string? Deliver(ExportDecision decision)
    {
        if (decision.ToClipboard)
        {
            ClipboardHelper.SetText(decision.Result.Markdown);
            return null;
        }

        try
        {
            ExportTarget.Write(decision.Path!, decision.Result.Markdown);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Could not write {decision.Path}: {e.Message}";
        }

        if (decision.Open) UrlLauncher.Open(new Uri(Path.GetFullPath(decision.Path!)).AbsoluteUri);
        return null;
    }

    /// <summary>
    /// One line: the title, when, and the folder by its name. ONE LINE because it names the conversation, and a title
    /// is the first prompt — often a paragraph — beside a folder path that can be longer still.
    /// </summary>
    private static string SessionLine(string? title, DateTimeOffset? at, string? folder)
    {
        var facts = new List<string>();
        if (at is { } when) facts.Add(when.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(folder)) facts.Add(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));
        var tail = facts.Count > 0 ? " · " + string.Join(" · ", facts) : "";
        var name = ClipTitle(title ?? "This conversation", 70 - tail.Length);
        return $"[bold]{MarkupParser.Escape(name)}[/]" + (tail.Length > 0 ? $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(tail)}[/]" : "");
    }

    private static string ClipTitle(string title, int room)
    {
        room = Math.Max(16, room);
        return title.Length <= room ? title : title[..(room - 1)].TrimEnd() + "…";
    }

    private void Toast(string text, NotificationSeverity severity) => system.ToastService.Show(text, severity);

    // A TOAST CUTS ITS LINE AT ABOUT SIXTY CHARACTERS; a file name cut there loses its end, which is the part that
    // says which file.
    private static string Clip(string text) => text.Length <= ToastWidth ? text : "…" + text[^(ToastWidth - 1)..];
}
