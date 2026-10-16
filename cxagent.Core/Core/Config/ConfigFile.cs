using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Config;

/// <summary>What a write did: saved and what the file now says, or why nothing was written.</summary>
public sealed record ConfigWrite(bool Saved, ProviderSettings? Settings, IReadOnlyList<string> Errors);

/// <summary>Which config.json, and the environment it is judged with.</summary>
/// <param name="Path">The file.</param>
/// <param name="Env">The environment, for the per-instance API-key overrides the loader reads.</param>
public sealed record ConfigTarget(string Path, IReadOnlyDictionary<string, string> Env);

/// <summary>
/// How cxagent changes provider and MCP config: read, edit, judge with the loader, then write atomically.
/// (The theme and plugin entries are written by <c>PluginConfigWriter</c>; this is the writer for
/// everything a running session is built from.)
///
/// <para>JUDGED BEFORE IT IS WRITTEN, by the same code startup runs. A file this writes is therefore
/// never one the next start refuses — the edit is rejected here, with the loader's own words, while
/// the user is still looking at the field that caused it.</para>
///
/// <para>AN UNPARSEABLE FILE IS REFUSED, never "started fresh". Starting from an empty document
/// would write a valid file and silently delete everything the user had in the broken one; refusing
/// leaves it for them to fix, and says where.</para>
///
/// <para>EVERY FAILURE IS AN ANSWER, NOT AN EXCEPTION: a file that cannot be read, a hand-edited value
/// of the wrong type, a disk that will not take the write. The caller is a dialog or a command, and
/// both say what went wrong rather than unwinding.</para>
///
/// <para>ATOMIC AND OWNER-ONLY FROM THE FIRST BYTE: the temp file is created at 0600 (it holds API
/// keys), named for this write so two writers cannot share one, then renamed over the file — a crash
/// mid-write leaves the old file whole, and a failed write leaves no temp file behind.</para>
/// </summary>
public static class ConfigFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static ConfigWrite TryApply(ConfigTarget target, Action<JsonObject> edit)
    {
        var configPath = target.Path;
        JsonObject root;
        try
        {
            root = File.Exists(configPath)
                ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                    ?? throw new JsonException("the top level is not an object")
                : new JsonObject();
        }
        catch (JsonException ex) { return Refused($"config.json is not valid JSON: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused($"config.json could not be read: {ex.Message}");
        }

        try { edit(root); }
        catch (ConfigEditException ex) { return Refused(ex.Message); }

        var text = root.ToJsonString(Indented) + Environment.NewLine;
        ProviderSettings settings;
        try { settings = ProviderConfigLoader.Parse(text, target.Env, Path.GetDirectoryName(configPath)); }
        catch (ProviderConfigException ex) { return new ConfigWrite(false, null, ex.Errors); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            // A VALUE OF THE WRONG TYPE, read by the loader as the type it expects.
            return Refused($"config.json has a value of the wrong type: {ex.Message}");
        }

        var tmp = $"{configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(text);
            File.Move(tmp, configPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            return Refused($"config.json could not be written: {ex.Message}");
        }
        return new ConfigWrite(true, settings, []);
    }

    private static ConfigWrite Refused(string why) => new(false, null, [why]);

    /// <summary>
    /// <c>/think --save</c>: one instance's starting level, or its removal for the model's own default.
    /// False when the session has no instance, the file names no such instance, or the write was refused.
    /// </summary>
    /// <param name="target">The config.json to edit.</param>
    /// <param name="instance">The session's instance, or null.</param>
    /// <param name="level">The level, or null for the model's own default.</param>
    public static bool SaveThinkingDefault(ConfigTarget target, string? instance, string? level) =>
        instance is not null && TryApply(target, root => ProviderEdits.Update(root, instance, level is null
            ? new InstanceFields { Clear = new HashSet<string> { "thinking" } }
            : new InstanceFields { Thinking = level })).Saved;
}
