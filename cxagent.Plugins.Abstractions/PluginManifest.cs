using System.Text.Json;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Plugins;

/// <summary>
/// One tool a plugin contributes, in the sidecar's own shape rather than <see cref="ToolDefinition"/>
/// — <see cref="PluginToolManifest.Gated"/> is manifest-only policy that a running plugin's dispatch needs and the
/// model's tool list does not.
/// </summary>
/// <summary>
/// When a plugin tool asks permission. Three states rather than a boolean because a tool's danger
/// often lives in its ARGUMENTS, not its identity: a query tool's <c>SELECT</c> is a read and its
/// <c>DROP</c> is not, and no boolean fixed before the call can tell them apart.
/// </summary>
public enum PluginGating
{
    /// <summary>Never asks. The default — the load gate already approved this binary.</summary>
    Never,

    /// <summary>Asks on every call, whatever the arguments say.</summary>
    Always,

    /// <summary>Asks <see cref="IPluginGateSource.Gate"/>, per call, with the arguments in hand.</summary>
    Dynamic,
}

/// <summary>
/// What a plugin returns from <see cref="IPluginGateSource.Gate"/> to ask about one call.
///
/// <para>DELIBERATELY NOT A <see cref="Permissions.PermissionRequest"/>, and this is the security
/// boundary of the whole feature. A PermissionRequest carries <c>Kind</c> and <c>AlwaysRule</c>,
/// and both are stored verbatim and matched against the store later — so a plugin that could
/// return one could return <c>Kind: Shell, AlwaysRule: "rm*"</c>, and a user clicking "Always" on
/// what looked like a plugin prompt would write a SHELL grant into permissions.json. The plugin
/// supplies the wording it needs; Core decides the scope.</para>
/// </summary>
/// <param name="Display">What the prompt says this call will do — the plugin knows the arguments,
/// so it can name the file or the statement rather than only the tool.</param>
/// <param name="AlwaysAskable">Whether this particular call may be granted standing permission.
/// ANDs with the manifest's own flag, which is a floor: a sidecar that withheld "Always" cannot
/// have it handed back at runtime.</param>
public sealed record PluginGate(string Display, bool AlwaysAskable = true)
{
    /// <summary>
    /// The undecorated thing this request is about — the command, the path — or null when the
    /// display text is already it.
    ///
    /// <para>AN INIT-ONLY PROPERTY RATHER THAN A THIRD POSITIONAL PARAMETER, because this record is
    /// PUBLISHED. A new positional parameter changes the constructor and the generated Deconstruct,
    /// so a plugin compiled against the old shape fails at LOAD with MissingMethodException — no
    /// build error, no warning, just a plugin that stops working after a host upgrade.</para>
    ///
    /// <para>WHAT IT BUYS: PermissionRequest.What is `Subject ?? Display`, and ActionClassifier
    /// reads What. Without a subject a plugin's annotation is judged on the sentence wrapped around
    /// the command rather than on the command.</para>
    /// </summary>
    public string? Subject { get; init; }
}

/// <param name="Name">The tool's name, as offered to the model.</param>
/// <param name="Description">What the model is told the tool does.</param>
/// <param name="InputSchema">
/// The tool's JSON Schema, passed through unchanged — <see cref="ToolDefinition"/> already carries a
/// <see cref="JsonElement"/> for this, so nothing here re-encodes it.
/// </param>
/// <param name="Gated">
/// Whether a call to this tool asks permission first. The plugin supplies this policy; Core enforces
/// it through the same prompt machinery every other permission uses.
/// </param>
/// <param name="AlwaysAskable">
/// Whether the prompt for this tool offers "Always" — true by default, so a gated tool the plugin
/// says nothing more about behaves like every other permission in cxagent.
///
/// <para>SET IT FALSE FOR THE TOOL THAT SHOULD NEVER GET A STANDING GRANT, and only the plugin can
/// know which one that is. A language-server plugin's <c>definition</c> is a read and a user should
/// be able to stop being asked; its <c>rename</c> rewrites files across a repository and is worth a
/// question every time. One flag for the whole plugin would force those two to share an answer.</para>
///
/// <para>THIS IS THE PLUGIN'S OWN JUDGEMENT, NOT A SECURITY BOUNDARY. A plugin that wanted a
/// standing grant simply declares itself always-askable, and the user already approved the binary
/// at load — so this is the author marking their own sharp edges, which is the only party who can.
/// Core cannot infer it from a tool name or a schema.</para>
/// </param>
public sealed record PluginToolManifest(string Name, string Description, JsonElement InputSchema,
    PluginGating Gated = PluginGating.Never, bool AlwaysAskable = true);

/// <summary>One argument a command takes — its name and what it means.</summary>
/// <remarks>
/// NAMES, NOT A SCHEMA. The palette renders these as a hint (`[when|goal]` — see
/// <c>SessionCommand.Hint</c>), and completion offers them; the argument itself crosses as the raw
/// string the user typed. A person types prose, so validating against a JSON schema would reject
/// what they meant rather than help them.
/// </remarks>
public sealed record PluginCommandArgument(string Name, string Summary);

/// <summary>One command a plugin contributes, shaped like the <c>SessionCommand</c> it becomes.</summary>
public sealed record PluginCommandManifest(
    string Name, string Summary, IReadOnlyList<PluginCommandArgument>? Arguments = null)
{
    /// <summary>Never null, so every consumer can enumerate without a guard.</summary>
    public IReadOnlyList<PluginCommandArgument> Args => Arguments ?? [];
}

/// <summary>
/// The sidecar shape and what <c>Describe</c> returns once a plugin is running — deliberately one
/// type for both, because the config-time collision check that catches two plugins declaring the
/// same tool name needs a plugin's tool names before any binary loads, and a manifest available only
/// from a running plugin would make that check impossible to perform ahead of time.
/// </summary>
/// <param name="Name">The plugin's own name — the identity a collision check compares.</param>
/// <param name="Version">The plugin's version, free-form.</param>
/// <param name="Instructions">
/// A block of system-prompt text describing how to use this plugin's tools as a set, or null when
/// the plugin has none. Per-tool descriptions cannot state facts about the plugin as a whole (a
/// shared workspace, a warm index) without repeating them.
/// </param>
/// <param name="Spawns">
/// Declares that this plugin starts child processes, so Core's reaping task knows to expect a pid
/// record from it rather than treating an absent one as a bug.
/// </param>
/// <param name="Tools">The tools this plugin contributes.</param>
/// <param name="DeclaredCommands">The commands this plugin contributes, or null when it declares none.</param>
public sealed record PluginManifest(string Name, string Version, string? Instructions, bool Spawns,
    IReadOnlyList<PluginToolManifest> Tools, IReadOnlyList<PluginCommandManifest>? DeclaredCommands = null)
{
    /// <summary>Never null, so every consumer can enumerate without a guard.</summary>
    public IReadOnlyList<PluginCommandManifest> Commands => DeclaredCommands ?? [];

    /// <summary>
    /// The contract this plugin was built against, or null when its manifest does not say.
    ///
    /// <para>CHECKED BEFORE THE PLUGIN IS CONSTRUCTED, which is the only place a check like this is
    /// worth anything: a manifest read after Load has already run the plugin's code, so refusing
    /// then discards a return value rather than preventing anything.</para>
    /// </summary>
    public int? Contract { get; init; }

    /// <summary>
    /// Whether this plugin asks for <see cref="IPluginClient"/> — the ability to start work in its
    /// session.
    ///
    /// <para>DECLARED IN THE MANIFEST RATHER THAN INFERRED FROM THE TYPE, because the load prompt must
    /// disclose it BEFORE any binary is loaded. A capability discovered by reflection after the user
    /// approved a tool count is a capability the user was never asked about.</para>
    /// </summary>
    public bool Client { get; init; }

    /// <summary>
    /// Every hook-point key this build knows how to service. Anything else in a manifest is refused
    /// by name rather than silently dropped — v1 honours <c>tools</c>, <c>permission</c> and
    /// <c>commands</c>; the rest are refused by name.
    /// </summary>
    private static readonly string[] KnownKinds = ["tools", "permission", "commands"];

    /// <summary>
    /// Every hook-point key any version of this contract has ever named, known or not — used only to
    /// tell an unrecognised property in the manifest from an unrelated typo. A key outside this list
    /// is not a plugin hook at all and is left alone rather than reported as a refused kind.
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
                    // THREE-STATE, NOT A BOOLEAN. "dynamic" routes a call through
                    // IPluginGateSource.Gate. Keeping it in the same field as the booleans is what
                    // keeps the sidecar a complete statement of gating policy: a reader who sees
                    // only `gated` has seen everything.
                    var gated = PluginGatingJson.Parse(
                        t.TryGetProperty("gated", out var tg) ? tg : default, toolName, out var gatingError);
                    if (gatingError is not null) errors.Add(gatingError);

                    // ABSENT MEANS TRUE, unlike "gated" above. The two defaults point opposite ways
                    // on purpose: a tool that says nothing about gating does not ask (the plugin did
                    // not claim it was dangerous), and a tool that asks but says nothing about
                    // "Always" offers it (the plugin did not claim it was UNGENERALISABLE). Both
                    // read as "the author did not think about this", and in each case that is the
                    // behaviour matching every other permission in cxagent.
                    var alwaysAskable = !t.TryGetProperty("alwaysAskable", out var ta)
                                        || ta.ValueKind != JsonValueKind.False;

                    tools.Add(new PluginToolManifest(toolName, description, schema, gated, alwaysAskable));
                }
            }

            var commands = new List<PluginCommandManifest>();
            if (root.TryGetProperty("commands", out var commandsEl) && commandsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in commandsEl.EnumerateArray())
                {
                    var commandName = c.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String
                        ? cn.GetString() : null;
                    if (string.IsNullOrWhiteSpace(commandName))
                    {
                        errors.Add("a command in 'commands' is missing required field 'name'.");
                        continue;
                    }
                    // SUMMARY DEFAULTS TO EMPTY, same as a tool's 'description' above — the field is
                    // what a palette row and /help show, not something a parse should fail over.
                    var summary = c.TryGetProperty("summary", out var cs) && cs.ValueKind == JsonValueKind.String
                        ? cs.GetString() ?? "" : "";

                    var arguments = new List<PluginCommandArgument>();
                    if (c.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in argsEl.EnumerateArray())
                        {
                            var argName = a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String
                                ? an.GetString() : null;
                            if (string.IsNullOrWhiteSpace(argName))
                            {
                                errors.Add($"an argument of command '{commandName}' is missing required field 'name'.");
                                continue;
                            }
                            var argSummary = a.TryGetProperty("summary", out var asum) && asum.ValueKind == JsonValueKind.String
                                ? asum.GetString() ?? "" : "";
                            arguments.Add(new PluginCommandArgument(argName, argSummary));
                        }
                    }

                    commands.Add(new PluginCommandManifest(commandName, summary, arguments));
                }
            }

            // A KIND THIS BUILD DOES NOT SERVICE IS REFUSED BY NAME. `tools`, `permission` and
            // `commands` are read above; every other known hook point is reported if present, so a
            // manifest declaring one of those is told so rather than left believing it took effect.
            foreach (var kind in AllDeclaredKinds)
            {
                if (KnownKinds.Contains(kind)) continue;
                if (root.TryGetProperty(kind, out _))
                    errors.Add($"manifest declares '{kind}', which this build does not service.");
            }

            // "abiVersion" IS READ AS A SYNONYM. The field predates managed plugins having a
            // contract number at all, and an ABI manifest in the wild spells it that way; one
            // contract covers both loaders, so the two spellings must mean the same thing rather
            // than one silently meaning nothing.
            int? contract = root.TryGetProperty("pluginContract", out var pc) && pc.TryGetInt32(out var pcv) ? pcv
                : root.TryGetProperty("abiVersion", out var av) && av.TryGetInt32(out var avv) ? avv
                : null;

            bool client = root.TryGetProperty("client", out var cl) && cl.ValueKind == JsonValueKind.True;

            var manifest = new PluginManifest(name ?? "", version ?? "", instructions, spawns, tools, commands)
            {
                Contract = contract,
                Client = client,
            };
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
