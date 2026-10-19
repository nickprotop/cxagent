using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace CxAgent.Cli;

/// <summary>
/// <c>pack --info &lt;file&gt;</c>: the catalog entry's text in a file kept beside the plugin — JSON, or
/// YAML for someone who would rather write that — so the fields need not be retyped as options on
/// every pack.
///
/// <para>STRICT ABOUT KEYS: an unknown key is an error rather than ignored, so "licence" is caught
/// the first time instead of shipping an entry whose license is a placeholder.</para>
/// </summary>
internal static class PackInfoFile
{
    public static readonly string[] Fields = ["description", "publisher", "license", "repository"];

    /// <summary>The file's fields, or null with <paramref name="error"/> saying what is wrong with it.</summary>
    public static IReadOnlyDictionary<string, string>? Read(string path, out string? error)
    {
        if (!File.Exists(path))
        {
            error = $"no info file '{path}'.";
            return null;
        }

        var raw = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => FromJson(File.ReadAllText(path), out error),
            ".yaml" or ".yml" => FromYaml(File.ReadAllText(path), out error),
            _ => Fail(out error, $"'{path}' is neither .json nor .yaml/.yml."),
        };
        if (raw is null) return null;

        if (raw.Keys.FirstOrDefault(k => !Fields.Contains(k)) is { } unknown)
        {
            error = $"'{path}' has '{unknown}', which is not one of {string.Join(", ", Fields)}.";
            return null;
        }

        error = null;
        return raw;
    }

    private static Dictionary<string, string>? FromJson(string text, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Fail(out error, "the info file must be a JSON object.");
            var fields = new Dictionary<string, string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) return Fail(out error, $"'{p.Name}' in the info file must be text.");
                fields[p.Name] = p.Value.GetString()!;
            }

            error = null;
            return fields;
        }
        catch (JsonException ex)
        {
            return Fail(out error, $"the info file is not JSON: {ex.Message}");
        }
    }

    // NODE BY NODE, NOT DESERIALISED: four flat text fields need no object mapping, and reading the
    // tree says exactly which value is not text.
    private static Dictionary<string, string>? FromYaml(string text, out string? error)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count > 1) return Fail(out error, "the info file holds more than one YAML document; keep one.");
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode map)
                return Fail(out error, "the info file must be a YAML mapping of key: value lines.");

            var fields = new Dictionary<string, string>();
            foreach (var (key, value) in map.Children)
            {
                var name = (key as YamlScalarNode)?.Value ?? "";
                if (value is not YamlScalarNode node) return Fail(out error, $"'{name}' in the info file must be text.");

                // YAML'S NULL — ~, null, or nothing — UNQUOTED is "not given", not the word: "license: ~"
                // must leave the field to the csproj or a placeholder, never ship "~" as a license.
                if (node.Style == YamlDotNet.Core.ScalarStyle.Plain && node.Value is null or "" or "~" or "null" or "Null" or "NULL") continue;
                fields[name] = node.Value ?? "";
            }

            error = null;
            return fields;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return Fail(out error, $"the info file is not YAML: {ex.Message}");
        }
    }

    private static Dictionary<string, string>? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }
}
