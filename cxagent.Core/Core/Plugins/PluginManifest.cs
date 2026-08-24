using System.Text.Json;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Plugins;

/// <summary>
/// One tool a plugin contributes, in the sidecar's own shape rather than <see cref="ToolDefinition"/>
/// — <see cref="Gated"/> is manifest-only policy that a running plugin's dispatch needs and the
/// model's tool list does not.
/// </summary>
/// <param name="Name">The tool's name, as offered to the model.</param>
/// <param name="Description">What the model is told the tool does.</param>
/// <param name="InputSchema">
/// The tool's JSON Schema, passed through unchanged — <see cref="ToolDefinition"/> already carries a
/// <see cref="JsonElement"/> for this, so nothing here re-encodes it.
/// </param>
/// <param name="Gated">
/// Whether a call to this tool asks permission first. The plugin supplies this policy; Core enforces
/// it through the same prompt machinery every other permission uses — see PLUGINS.md, "The plugin
/// provides its own policy; Core enforces it".
/// </param>
public sealed record PluginToolManifest(string Name, string Description, JsonElement InputSchema, bool Gated = false);

/// <summary>
/// The sidecar shape and what <c>Describe</c> returns once a plugin is running — deliberately one
/// type for both, because a config-time collision check (PLUGINS.md, matrix row 2) needs a plugin's
/// tool names before any binary loads, and a manifest available only from a running plugin would
/// make that check impossible to perform ahead of time.
/// </summary>
/// <param name="Name">The plugin's own name — the identity a collision check compares.</param>
/// <param name="Version">The plugin's version, free-form.</param>
/// <param name="Instructions">
/// A block of system-prompt text describing how to use this plugin's tools as a set, or null when
/// the plugin has none. See PLUGINS.md, "The system prompt" — per-tool descriptions cannot state
/// facts about the plugin as a whole (a shared workspace, a warm index) without repeating them.
/// </param>
/// <param name="Spawns">
/// Declares that this plugin starts child processes, so Core's reaping task knows to expect a pid
/// record from it rather than treating an absent one as a bug.
/// </param>
/// <param name="Tools">The tools this plugin contributes.</param>
public sealed record PluginManifest(string Name, string Version, string? Instructions, bool Spawns,
    IReadOnlyList<PluginToolManifest> Tools)
{
    /// <summary>
    /// Every hook-point key this build knows how to service. Anything else in a manifest is refused
    /// by name rather than silently dropped — see PLUGINS.md, "Hook points": "v1 honours `tools` and
    /// `permission`; the rest are refused by name."
    /// </summary>
    private static readonly string[] KnownKinds = ["tools", "permission"];

    /// <summary>
    /// Every hook-point key PLUGINS.md names, known or not — used only to tell an unrecognised
    /// property in the manifest from an unrelated typo. A key outside this list is not a plugin hook
    /// at all and is left alone rather than reported as a refused kind.
    /// </summary>
    private static readonly string[] AllDeclaredKinds =
        ["tools", "permission", "commands", "completions", "providers", "observers"];

    /// <summary>
    /// Parses a manifest from its sidecar JSON text.
    ///
    /// <para>A KIND THIS BUILD DOES NOT SERVICE FAILS THE PARSE, BUT NOT SILENTLY AND NOT WHOLESALE.
    /// <see cref="PluginManifestParseResult.IsSuccess"/> is false and <see
    /// cref="PluginManifestParseResult.Errors"/> names the unserviced key — refusing by name rather
    /// than ignoring it, so a plugin author is told the declaration did not take effect. But <see
    /// cref="PluginManifestParseResult.Manifest"/> is still populated with whatever this build DOES
    /// know how to read, `tools` included: a forward-compatible manifest carrying a future kind must
    /// not fail wholesale over the part this build cannot yet act on.</para>
    /// </summary>
    public static PluginManifestParseResult Parse(string json)
    {
        var errors = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new PluginManifestParseResult(null, [$"manifest is not valid JSON: {ex.Message}"]);
        }

        using (doc)
        {
            var root = doc.RootElement;

            string? name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            string? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
            string? instructions = root.TryGetProperty("instructions", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString() : null;
            bool spawns = root.TryGetProperty("spawns", out var s) && s.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(name))
                errors.Add("manifest is missing required field 'name'.");
            if (string.IsNullOrWhiteSpace(version))
                errors.Add("manifest is missing required field 'version'.");

            var tools = new List<PluginToolManifest>();
            if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in toolsEl.EnumerateArray())
                {
                    var toolName = t.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String
                        ? tn.GetString() : null;
                    if (string.IsNullOrWhiteSpace(toolName))
                    {
                        errors.Add("a tool in 'tools' is missing required field 'name'.");
                        continue;
                    }
                    var description = t.TryGetProperty("description", out var td) && td.ValueKind == JsonValueKind.String
                        ? td.GetString() ?? "" : "";
                    // ABSENT SCHEMA BECOMES AN EMPTY OBJECT, not a default JsonElement — a default
                    // JsonElement's ValueKind is Undefined, and code downstream that inspects the
                    // schema (or re-serialises it) should see "no constraints" rather than crash on
                    // a value that was never actually parsed from anything.
                    var schema = t.TryGetProperty("inputSchema", out var ts) && ts.ValueKind == JsonValueKind.Object
                        ? ts.Clone() : JsonDocument.Parse("{}").RootElement;
                    var gated = t.TryGetProperty("gated", out var tg) && tg.ValueKind == JsonValueKind.True;

                    tools.Add(new PluginToolManifest(toolName, description, schema, gated));
                }
            }

            // A KIND THIS BUILD DOES NOT SERVICE IS REFUSED BY NAME. `tools` and `permission` are
            // read above; every other hook point PLUGINS.md names is reported if present, so a
            // manifest declaring `commands` against a build that services only tools is told so
            // rather than left believing the declaration took effect.
            foreach (var kind in AllDeclaredKinds)
            {
                if (KnownKinds.Contains(kind)) continue;
                if (root.TryGetProperty(kind, out _))
                    errors.Add($"manifest declares '{kind}', which this build does not service.");
            }

            var manifest = new PluginManifest(name ?? "", version ?? "", instructions, spawns, tools);
            return new PluginManifestParseResult(manifest, errors);
        }
    }
}

/// <summary>
/// The outcome of parsing a manifest — a manifest AND errors can both be present at once (see
/// <see cref="PluginManifest.Parse"/>), so this is not a simple either/or result.
/// </summary>
/// <param name="Manifest">
/// What this build could read, or null only when the JSON itself was unparsable or malformed enough
/// that no manifest shape could be recovered.
/// </param>
/// <param name="Errors">Every reason this manifest did not fully succeed. Empty on a clean parse.</param>
public sealed record PluginManifestParseResult(PluginManifest? Manifest, IReadOnlyList<string> Errors)
{
    /// <summary>True when nothing in the manifest was refused or malformed.</summary>
    public bool IsSuccess => Errors.Count == 0;
}
