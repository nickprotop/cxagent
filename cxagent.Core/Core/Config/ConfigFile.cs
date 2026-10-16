using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Config;

/// <summary>What a write did: saved and what the file now says, or why nothing was written.</summary>
public sealed record ConfigWrite(bool Saved, ProviderSettings? Settings, IReadOnlyList<string> Errors);

/// <summary>
/// The one way cxagent writes config.json: read, edit, judge with the loader, then write atomically.
///
/// <para>JUDGED BEFORE IT IS WRITTEN, by the same code startup runs. A file this writes is therefore
/// never one the next start refuses — the edit is rejected here, with the loader's own words, while
/// the user is still looking at the field that caused it.</para>
///
/// <para>AN UNPARSEABLE FILE IS REFUSED, never "started fresh". Starting from an empty document
/// would write a valid file and silently delete everything the user had in the broken one; refusing
/// leaves it for them to fix, and says where.</para>
///
/// <para>ATOMIC AND OWNER-ONLY: a temp file, mode 0600 (it holds API keys), then one rename — a crash
/// mid-write leaves the old file whole.</para>
/// </summary>
public static class ConfigFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static ConfigWrite TryApply(string configPath, IReadOnlyDictionary<string, string> env, Action<JsonObject> edit)
    {
        JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                    ?? throw new JsonException("the top level is not an object");
            }
            catch (JsonException ex)
            {
                return new ConfigWrite(false, null, [$"config.json is not valid JSON: {ex.Message}"]);
            }
        }
        else root = new JsonObject();

        try { edit(root); }
        catch (ConfigEditException ex) { return new ConfigWrite(false, null, [ex.Message]); }

        var text = root.ToJsonString(Indented) + Environment.NewLine;
        ProviderSettings settings;
        try { settings = ProviderConfigLoader.Parse(text, env, Path.GetDirectoryName(configPath)); }
        catch (ProviderConfigException ex) { return new ConfigWrite(false, null, ex.Errors); }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var tmp = configPath + ".tmp";
        File.WriteAllText(tmp, text);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, configPath, overwrite: true);
        return new ConfigWrite(true, settings, []);
    }

    /// <summary>
    /// <c>/think --save</c>: one instance's starting level, or its removal for the model's own default.
    /// False when the session has no instance, the file names no such instance, or the write was refused.
    /// </summary>
    /// <param name="configPath">The config.json to edit.</param>
    /// <param name="env">The environment, for validation.</param>
    /// <param name="instance">The session's instance, or null.</param>
    /// <param name="level">The level, or null for the model's own default.</param>
    public static bool SaveThinkingDefault(
        string configPath, IReadOnlyDictionary<string, string> env, string? instance, string? level) =>
        instance is not null && TryApply(configPath, env, root => ProviderEdits.Update(root, instance, level is null
            ? new InstanceFields { Clear = new HashSet<string> { "thinking" } }
            : new InstanceFields { Thinking = level })).Saved;
}
