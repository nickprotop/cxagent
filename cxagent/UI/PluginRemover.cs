using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>What removing a plugin would do, decided before anything is deleted.</summary>
public abstract record RemovalPlan
{
    private RemovalPlan() { }

    /// <param name="Description">What is about to happen, for a confirmation to show.</param>
    /// <param name="Files">Every file that would be deleted, resolved and absolute.</param>
    /// <param name="LeftBehind">Files in the same folder that cannot be attributed to this plugin,
    /// so a confirmation can say what stays.</param>
    public sealed record Removable(
        string Description,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> LeftBehind) : RemovalPlan;

    /// <param name="Reason">Why nothing can be removed, in words a user can act on.</param>
    public sealed record Blocked(string Reason) : RemovalPlan;
}

/// <summary>
/// Works out what uninstalling a plugin would delete, and then deletes it.
///
/// <para>PLANNED BEFORE IT IS DONE, because deleting is irreversible and a confirmation reading
/// "uninstall csharp-lsp?" gives a user nothing to check. The plan carries every path, so the
/// question can name them.</para>
/// </summary>
public static class PluginRemover
{
    /// <summary>
    /// What removing <paramref name="name"/> would take with it.
    /// </summary>
    /// <param name="name">The config entry's name — not the file, since two entries can name one
    /// binary with different settings.</param>
    /// <param name="file">The entry point's filename, from that entry.</param>
    /// <param name="loadSetDirectory">Where the plugin was resolved from.</param>
    /// <param name="pluginsFolders">Every folder cxagent searches; a path outside all of them is
    /// refused.</param>
    /// <param name="configured">Every config entry, so a file another one still names is spared.</param>
    public static RemovalPlan Plan(
        string name,
        string file,
        string loadSetDirectory,
        IReadOnlyList<string> pluginsFolders,
        IReadOnlyDictionary<string, PluginConfig> configured)
    {
        var home = Path.GetFullPath(loadSetDirectory);

        // INSIDE A KNOWN FOLDER, OR ITS IMMEDIATE SUBDIRECTORY. A path that escapes is a bug here
        // or a malformed config; either way it is not a reason to delete anything.
        var inside = pluginsFolders.Any(folder =>
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return string.Equals(home, root, StringComparison.Ordinal)
                || string.Equals(Path.GetDirectoryName(home), root, StringComparison.Ordinal);
        });

        if (!inside)
            return new RemovalPlan.Blocked(
                $"'{name}' resolves to {home}, which is not inside a plugins folder cxagent searches.");

        // ANOTHER ENTRY MAY NAME THE SAME BINARY — config.sample.json ships exactly that, one
        // plugin configured twice with different settings. Deleting the files for one would break
        // the other, which the user did not touch.
        var alsoNaming = configured
            .Where(kv => !string.Equals(kv.Key, name, StringComparison.Ordinal))
            .Where(kv => string.Equals(kv.Value.File, file, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (alsoNaming.Count > 0)
            return new RemovalPlan.Blocked(
                $"{string.Join(", ", alsoNaming)} still name{(alsoNaming.Count == 1 ? "s" : "")} "
              + $"'{file}', so its files stay. Removing '{name}' from config is enough.");

        var nested = pluginsFolders.All(folder =>
            !string.Equals(home, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                StringComparison.Ordinal));

        if (nested)
        {
            // A NESTED PLUGIN IS ITS DIRECTORY, which is the load-set design's own definition: the
            // folder is what the hash covers and what dependency resolution reads from.
            var all = Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            return new RemovalPlan.Removable(
                $"Remove {home} and its {all.Count} file{(all.Count == 1 ? "" : "s")}.", all, []);
        }

        // A LOOSE PLUGIN HAS NO DIRECTORY OF ITS OWN, so only what can be attributed goes: the
        // entry point and its sidecar. The sidecar carries no file inventory, so anything else
        // beside it is genuinely unattributable and is reported rather than guessed at.
        var stem = Path.GetFileNameWithoutExtension(file);
        var mine = new List<string>();
        foreach (var candidate in new[] { file, stem + ".plugin.json" })
        {
            var path = Path.Combine(home, candidate);
            if (File.Exists(path)) mine.Add(Path.GetFullPath(path));
        }

        var others = Directory.EnumerateFiles(home, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(f => !mine.Contains(f, StringComparer.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return new RemovalPlan.Removable(
            $"Remove {mine.Count} file{(mine.Count == 1 ? "" : "s")} from {home}.", mine, others);
    }

    /// <summary>
    /// Deletes what the plan named, returning anything it could not.
    ///
    /// <para>ONE FILE'S FAILURE DOES NOT STOP THE REST. A partially removed plugin is the outcome
    /// either way once a delete has failed; stopping early would leave more behind and report less.
    /// On Windows a loaded assembly stays locked for the process's life, so the caller checks that
    /// before planning at all — this reports what still could not go.</para>
    /// </summary>
    public static IReadOnlyList<string> Remove(RemovalPlan.Removable plan)
    {
        var failed = new List<string>();

        foreach (var path in plan.Files)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(path);
            }
        }

        // THE DIRECTORY GOES ONLY IF IT IS NOW EMPTY. A leftover the plan listed is a file the user
        // was told would stay, and removing the folder around it would delete it anyway.
        foreach (var directory in plan.Files
                     .Select(Path.GetDirectoryName)
                     .Where(d => d is not null)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(d => d!.Length, Comparer<int>.Default))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return failed;
    }
}
