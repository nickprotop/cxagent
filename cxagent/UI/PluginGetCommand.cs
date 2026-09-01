using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// <c>/plugin get &lt;name&gt;</c> — fetches a plugin from the published catalog into the global
/// plugins folder.
///
/// <para>IT EXISTS FOR THE CATALOG PAGE. A plugin's listing on the site can print one line the
/// reader copies, and a copy-paste install is a better story than "open the manager, find the row,
/// choose a folder, confirm" for someone who already knows which plugin they want. The manager
/// stays: it is how you BROWSE, and this is how you install something you have already chosen.</para>
///
/// <para>IT DOWNLOADS AND STOPS, which is <see cref="PluginInstaller"/>'s own rule — installing is
/// not approving. Nothing is loaded and nothing is written to config.json, because promoting "the
/// files arrived" into "run this at every start" is the step the load gate exists to guard. The
/// reply names the two commands that take it further.</para>
///
/// <para>GLOBAL, WITHOUT ASKING. The manager offers a folder because a user browsing might want a
/// project-local copy; someone typing a name off a web page means "install it for me", and the
/// global folder is what that means. A project install is still available through the manager.</para>
///
/// <para>A CLASS RATHER THAN A CLOSURE IN THE COMPOSITION ROOT, following <see cref="McpCommand"/>:
/// the dependencies were already explicit enough to be constructor parameters, and a command's
/// behaviour is not the composition root's job.</para>
/// </summary>
public sealed class PluginGetCommand(
    ConsoleWindowSystem system,
    MainWindow window,
    AppPaths paths)
{
    /// <summary>
    /// Posts a system row, coloured by severity — the same channel <see cref="McpCommand"/> uses,
    /// because a command's answer belongs in the transcript the user is reading.
    /// </summary>
    private void Say(string text, Severity severity = Severity.Info) =>
        ChatTranscriptSink.Post(window.Chat, ChatTranscriptSink.Row(new Message(text, severity)));

    /// <summary>Runs <c>/plugin get &lt;name&gt;</c>.</summary>
    /// <param name="session">The session whose permission gate decides the download.</param>
    /// <param name="arguments">Everything after the command name — <c>get clone-finder</c>.</param>
    public async Task HandleAsync(Session session, string arguments)
    {
        var name = NameFrom(arguments);

        if (name.Length == 0)
        {
            Say("usage: /plugin get <name> — the name as the catalog lists it, e.g. clone-finder.",
                Severity.Warning);
            return;
        }

        var url = Environment.GetEnvironmentVariable("CXAGENT_PLUGIN_CATALOG") is { Length: > 0 } custom
            ? custom
            : CatalogReader.PublishedUrl;

        // THE SAME CACHE THE MANAGER READS, so a catalog fetched by either is available to both and
        // this works offline in a later run for the same reason the dialog does.
        var reader = new CatalogReader(null, Path.Combine(paths.ConfigDir, "catalog-cache.json"), url);

        Catalog catalog;
        try
        {
            catalog = await reader.ReadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Say($"the catalog could not be read: {ex.Message}", Severity.Warning);
            return;
        }

        if (Find(catalog, name) is not { } entry)
        {
            // THE NEAR MISSES, NOT A BARE REFUSAL. "not found" against a catalog the user cannot see
            // leaves them guessing at spelling; the candidates are what turn it into a correction.
            var near = catalog.Plugins
                .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                         || name.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .Take(3)
                .ToList();

            Say(near.Count > 0
                    ? $"no plugin named '{name}' in the catalog. Did you mean {string.Join(", ", near)}?"
                    : $"no plugin named '{name}' in the catalog. /plugin browse lists what is there.",
                Severity.Warning);
            return;
        }

        if (session.Services?.Gate is not { } gate)
        {
            Say("no permission gate is wired, so nothing can be downloaded.", Severity.Warning);
            return;
        }

        // STAMPED WITH THE SESSION'S POLICY, as the manager's own download prompt is: the gate holds
        // no session, so a request arriving without a policy has no root to check against and is
        // refused before the user ever sees it.
        var outcome = await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Http,
                $"download '{entry.Name}' {entry.Version} from {entry.DownloadUrl}",
                AlwaysRule: null)
            { Policy = session.Policy },
            CancellationToken.None);

        if (!outcome.Allowed)
        {
            Say($"'{entry.Name}' was not downloaded.");
            return;
        }

        // THE LAST FOLDER IS THE GLOBAL ONE — PluginDiscovery.SearchFolders orders them nearest
        // first, so global is last. Read from the same function the loader uses rather than composed
        // here, or an install lands somewhere the loader does not look.
        var folder = PluginDiscovery.SearchFolders([], session.WorkingDirectory, paths.ConfigDir)[^1];

        var result = await new PluginInstaller().InstallAsync(entry, folder, CancellationToken.None);

        system.EnqueueOnUIThread(() => Report(entry, result));
    }

    /// <summary>
    /// Says what happened, and — when it worked — what the two next steps are.
    ///
    /// <para>NAMING THEM IS THE POINT. This command deliberately stops after the files land, so a
    /// reply that said only "installed" would leave the user with a plugin that does nothing and no
    /// idea which command makes it run.</para>
    /// </summary>
    private void Report(CatalogEntry entry, InstallResult result)
    {
        switch (result)
        {
            case InstallResult.Installed(var directory, var files):
                Say($"'{entry.Name}' {entry.Version} downloaded — {files.Count} file(s) in {directory}. "
                  + $"Nothing is running yet: `/plugin load {entry.File}` tries it for this session, "
                  + "and /plugin browse can add it to config.json so it loads at every start.");
                break;

            case InstallResult.HashMismatch(var expected, var actual):
                // WHAT ARRIVED IS NOT WHAT WAS PUBLISHED, and that is worth more than "failed".
                Say($"'{entry.Name}' was not installed: the download does not match the catalog's "
                  + $"checksum (expected {expected[..12]}…, got {actual[..12]}…).", Severity.Warning);
                break;

            case InstallResult.Refused(var reason):
                Say($"'{entry.Name}' was not installed: {reason}", Severity.Warning);
                break;
        }
    }

    /// <summary>
    /// The plugin name from the verb's argument string, or empty when none was typed.
    ///
    /// <para>THE VERB IS STILL IN THERE. RegisterVerb matches on the first word and hands the WHOLE
    /// argument string to the handler — "get clone-finder", not "clone-finder" — exactly as /mcp's
    /// verbs receive theirs. Taking the string as the name would try to install a plugin called
    /// "get clone-finder".</para>
    /// </summary>
    public static string NameFrom(string arguments) =>
        arguments.Split(' ', 2) is [_, var rest] ? rest.Trim() : "";

    /// <summary>
    /// The entry for this name, matched case-insensitively against the catalog's own name.
    ///
    /// <para>DISPLAY NAME TOO, because the site shows one and the catalog keys on the other — a
    /// reader copying what they can see should not be told it does not exist.</para>
    /// </summary>
    private static CatalogEntry? Find(Catalog catalog, string name) =>
        catalog.Plugins.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
         || string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
}
