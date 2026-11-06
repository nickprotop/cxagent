using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Dialogs;

namespace CxAgent.UI;

/// <summary>
/// `/sessions new [folder]` — a second conversation, in its own tab.
///
/// <para>A VERB ON `/sessions` RATHER THAN A `/session` COMMAND. A singular sibling beside the
/// existing plural is a mistype waiting to happen, and `new` belongs beside `resume` and `all` as one
/// more thing to do with the sessions this folder has. Registered by the app rather than declared in
/// Core's table for <c>/exit</c>'s reason: opening a tab needs a window, and a library cannot make
/// one in its host.</para>
///
/// <para>WITH NO ARGUMENT IT ASKS, through the folder picker SharpConsoleUI already provides — the
/// same seam <c>/open</c> uses for files, opened at the current session's directory because that is
/// where a sibling project usually lives.</para>
/// </summary>
public sealed class NewSessionCommand
{
    /// <summary>What opening a session needs that only the composition root can supply.</summary>
    /// <param name="System">The window system, for the picker and the sinks.</param>
    /// <param name="Main">The window that will hold the tab.</param>
    /// <param name="Manager">Opens the session and holds it beside the others.</param>
    /// <param name="Composer">Wires the session the way every other door does.</param>
    /// <param name="Resolution">The configuration a new session resolves against.</param>
    /// <param name="Mode">The working mode a new session starts in.</param>
    /// <param name="ConfigDir">Where plugins are discovered and their child processes recorded.</param>
    public readonly record struct Host(
        ConsoleWindowSystem System,
        MainWindow Main,
        SessionManager Manager,
        SessionComposer Composer,
        ResolvedConfig Resolution,
        WorkingMode Mode,
        string ConfigDir);

    private readonly Host _host;

    public NewSessionCommand(Host host) => _host = host;

    /// <summary>
    /// The folder from the verb's argument string, or empty when none was typed.
    ///
    /// <para>THE VERB IS STILL IN THERE. <c>RegisterVerb</c> matches on the first word and hands the
    /// WHOLE string to the handler — "new /some/path", not "/some/path" — so taking it as given
    /// would look for a folder called "new /some/path".</para>
    /// </summary>
    public static string FolderFrom(string? arguments) =>
        arguments?.Split(' ', 2) is [_, var rest] ? rest.Trim() : "";

    /// <summary>Runs the verb. Returns true, always: a refusal is still a handled command.</summary>
    public bool Run(Session current, string? arguments)
    {
        var folder = Folder(arguments);

        if (folder is null)
        {
            AskForFolder(current);
            return true;
        }

        Open(folder);
        return true;
    }

    /// <summary>
    /// The folder named on the command line, or null to ask.
    ///
    /// <para>RELATIVE PATHS RESOLVE AGAINST THE CURRENT SESSION, not the process — two sessions in
    /// one process do not share a working directory, and the one the user is typing in is the only
    /// sensible base for what they typed.</para>
    /// </summary>
    private static string? Folder(string? arguments)
    {
        var trimmed = arguments?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Opens the picker and, if a folder comes back, opens a session in it.
    ///
    /// <para>ASYNC VOID AT THE EDGE, deliberately, and the same shape <c>/open</c>'s picker uses: the
    /// command handler is synchronous and a dialog resolves whenever the user is done with it.
    /// Everything inside is in a try, because an async void that throws reaches no caller and takes
    /// the app with it.</para>
    /// </summary>
    private async void AskForFolder(Session current)
    {
        try
        {
            var chosen = await FileDialogs.ShowFolderPickerAsync(_host.System,
                startPath: current.WorkingDirectory);

            // NULL IS A CANCEL, not a failure: the dialog closes itself and nothing is opened.
            if (!string.IsNullOrWhiteSpace(chosen)) Open(chosen);
        }
        catch (Exception ex)
        {
            _host.Main.Chat.AddMessage(ChatRole.System, $"could not open a session: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a session on a folder and gives it a tab.
    ///
    /// <para>THE POLICY IS THIS SESSION'S OWN, scoped to the folder it works in and carrying its id.
    /// One gate serves the process; passing a policy per session is what stops a second one being
    /// judged against the first one's directory, and what lets a decision say which session made
    /// it.</para>
    /// </summary>
    private void Open(string folder)
    {
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex)
        {
            _host.Main.Chat.AddMessage(ChatRole.System, $"not a usable path: {ex.Message}");
            return;
        }

        if (!Directory.Exists(full))
        {
            // NAMED, NOT JUST REFUSED. A typo in a path is the likely cause, and the path as this
            // resolved it is the thing that shows the typo.
            _host.Main.Chat.AddMessage(ChatRole.System, $"no such folder: {full}");
            return;
        }

        var session = new Session(full);
        var tab = _host.Main.AddSessionTab(session);

        // THE SAME ROUTINE THE STARTUP PATH USES. Written separately, this path once subscribed two of eleven events —
        // no tool rows, no turn recording, no compression notice, no child tracking — because nothing on this side
        // listed what the other side wires.
        //
        // ITS POLICY TAKES THE CURRENT EDIT MODE, while the session is opened in the agent mode alone: that is how
        // this door has always opened a tab, and the composer keeps it rather than deciding it.
        _host.Composer.Wire(new WireRequest(session, _host.Resolution, _host.Mode.Agent)
        {
            Policy = new PermissionPolicy(full, _host.Composer.Services.Rules, _host.Mode.Edits)
            {
                SessionId = session.Id,
                LogDir = _host.Composer.Services.Paths.LogsDir,
            },
        }, new TuiFrontEnd(new TuiTab(_host.System, _host.Main, tab, _host.Manager, _host.Resolution, _host.Mode, _host.ConfigDir)));

        tab.Chat.AddMessage(ChatRole.System, $"session opened in {full}");

        // ITS OWN PLUGINS, LOADED FOR ITS OWN FOLDER. The startup path loads them for the first
        // session and closes over it, so a second session had none at all — no tools, and no system
        // rows saying so, which is indistinguishable from a session that simply has no plugins
        // configured. The search folders include the session's own directory, so a project-local
        // plugin belongs to the project that declares it.
        _ = TrustThenLoadPluginsAsync(session, tab);
    }

    /// <summary>
    /// Asks about the folder's trust when nobody has decided yet, then loads the session's plugins — in that order,
    /// because a plugin the folder declares runs code, which is what the trust answer is about.
    /// </summary>
    private async Task TrustThenLoadPluginsAsync(Session session, SessionTab tab)
    {
        var result = await _host.Composer.AskTrustIfUnknownAsync(session, CancellationToken.None);
        if (result.SaveError is { } error)
            tab.Chat.AddMessage(ChatRole.System, $"could not save folder trust: {error}");
        if (result.Outcome == TrustOutcome.Trusted)
            tab.Chat.AddMessage(ChatRole.System, "trusted this folder");
        else if (result.Outcome == TrustOutcome.NotTrusted)
            tab.Chat.AddMessage(ChatRole.System, "not trusted — file operations in this folder will ask every time");
        await LoadPluginsAsync(session, tab);
    }

    /// <summary>
    /// Loads this session's plugins, reporting into its own transcript.
    ///
    /// <para>FIRE AND FORGET, LIKE THE STARTUP PATH: loading spawns processes and may ask permission,
    /// and a command handler is synchronous. The task is discarded rather than awaited, so a failure
    /// is reported into the tab rather than thrown at a caller that has already returned.</para>
    /// </summary>
    private Task LoadPluginsAsync(Session session, SessionTab tab) =>
        _host.Composer.LoadPluginsAsync(session, _host.Resolution,
            message => tab.Chat.AddMessage(ChatRole.System, message), CancellationToken.None);
}
