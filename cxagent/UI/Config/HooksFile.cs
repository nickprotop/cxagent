using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using CxAgent.Core.Hooks;
using CxAgent.Core.Storage;

namespace CxAgent.UI.Config;

/// <summary>
/// Reads the three hooks files into a <see cref="HooksDraft"/>, and writes back the ones that changed.
///
/// <para>config.json THROUGH ConfigFile, like every other F8 write: the raw text is edited and judged by the
/// loader before it lands, so an <c>{env:…}</c> elsewhere in the file stays a placeholder and a write that
/// would break the config is refused. The project files are cxagent's alone and are written whole, keeping
/// any other top-level key they carry.</para>
/// </summary>
public static class HooksFile
{
    public static HooksDraft Load(AppPaths paths, IReadOnlyDictionary<string, string> env, string workingDirectory)
    {
        // THE USER'S BLOCK AS WRITTEN, from the file — not the session's copy, which is what it read at start.
        var user = ProvidersFile.Read(paths, env).Settings?.Hooks ?? HooksSettings.None;
        var snapshot = HookSources.Read(user, workingDirectory);
        var unreadable = snapshot.Problems
            .Where(p => p.Message.StartsWith("could not be read", StringComparison.Ordinal)
                        || p.Message.StartsWith("needs a top-level", StringComparison.Ordinal))
            .Select(p => p.Source).ToHashSet();
        return new HooksDraft(snapshot.Hooks.Where(h => h.Source != HookSource.Library).Select(HookEntry.From),
            snapshot.RepoRoot, unreadable);
    }

    /// <summary>Writes every source whose file changed. Returns what went wrong, empty when all were written.</summary>
    public static IReadOnlyList<string> Save(HooksDraft draft, AppPaths paths, IReadOnlyDictionary<string, string> env)
    {
        var errors = new List<string>();
        foreach (var source in draft.Changed)
        {
            if (!draft.CanSaveTo(source)) { errors.Add($"{Label(source)}: there is no repository here to save it in."); continue; }
            if (draft.Unreadable.Contains(source))
            {
                errors.Add($"{Label(source)} could not be read, so it was not overwritten. Fix it in an editor first.");
                continue;
            }
            var block = draft.Block(source);
            if (source == HookSource.User) SaveUser(block, paths, env, errors);
            else SaveProject(HookSources.ProjectFile(draft.RepoRoot, source)!, block, errors);
        }
        return errors;
    }

    public static string Label(HookSource source) => source switch
    {
        HookSource.User => "config.json",
        HookSource.Project => ".cxagent/hooks.json",
        HookSource.Local => ".cxagent/hooks.local.json",
        _ => "the app",
    };

    private static void SaveUser(JsonObject block, AppPaths paths, IReadOnlyDictionary<string, string> env, List<string> errors)
    {
        var result = ConfigFile.TryApply(new ConfigTarget(Path.Combine(paths.ConfigDir, "config.json"), env), root =>
        {
            // THE BLOCK'S SETTINGS STAY: defaultModel is not an event and the editor does not show it.
            var merged = new JsonObject();
            if (root["hooks"] is JsonObject old)
                foreach (var kv in old.Where(kv => !HookEvents.Known(kv.Key) && kv.Value is not JsonArray))
                    merged[kv.Key] = kv.Value?.DeepClone();
            foreach (var kv in block) merged[kv.Key] = kv.Value?.DeepClone();
            if (merged.Count == 0) root.Remove("hooks"); else root["hooks"] = merged;
        });
        if (!result.Saved) errors.AddRange(result.Errors.Select(e => $"config.json: {e}"));
    }

    private static void SaveProject(string file, JsonObject block, List<string> errors)
    {
        try
        {
            var root = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                is JsonObject existing ? existing : new JsonObject();
            root["hooks"] = block;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
        }
    }
}
