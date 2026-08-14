using System.Text.Json;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Llm;

/// <summary>
/// ContextWindow (P11 Task 1) is the model's real context size in tokens — a property of the MODEL,
/// not of cxagent, so only the user (who knows what a custom endpoint is serving) can set it
/// reliably. Trailing default for the same reason the others are: nothing recomputes
/// this record after construction, so appending a defaulted field can't break any of the 26
/// existing positional construction sites.
///
/// null-means-unknown, DELIBERATELY not defaulted to some guessed number: "we don't know the
/// window" must stay distinguishable from "we know it is N", because the compression threshold
/// (Task 2) branches on exactly that difference. Substituting a made-up value here would silently
/// drive compression at the wrong point — the bug this feature exists to fix.
/// </summary>
/// <param name="MaxConcurrentAgents">
/// How many sub-agents may call THIS endpoint at once. Null or 0 means unlimited, matching how
/// <c>maxTurns</c> already reads 0 as unbounded.
///
/// <para>BESIDE <see cref="ContextWindow"/> BECAUSE IT IS THE SAME CATEGORY: a property of the
/// endpoint that cxagent cannot discover and must be told. A hosted API answers concurrent requests
/// and pushes back with 429s; a single-threaded local server queues them at the socket, so children
/// hold open connections while executing serially. Both are real deployments and neither is the
/// default case — so the default is not to interfere.</para>
/// </param>
public record ProviderInstanceConfig(
    string Kind, string Model, string? ApiKey, string? BaseUrl,
    IReadOnlyDictionary<string, string>? ExtraHeaders, int? ContextWindow = null,
    int? MaxConcurrentAgents = null);

public record RoutingTarget(string Provider, string Model);

/// <summary>
/// A TURN CAP IS SET WHERE A LOOP LIVES, NOT WHERE WORK LIVES. It exists to stop a runaway, and one
/// tight enough to bite ordinary work is worse than none: it fails silently, mid-task, with a
/// partial result the caller reports as success.
///
/// <para>Measured — the cap was once 10, and an implementer asked to edit six files spent all ten
/// turns READING them (16 read_file calls, zero writes) and reported done. Editing N files costs
/// roughly 2N turns before discovery or a retry on a failed match, so ten files is already ~25.</para>
///
/// <para><c>ContextCompressThreshold</c> is a MEASURED trigger rather than a cap: it names the live
/// context size — the provider's own count of what it just received — above which the agent
/// compresses. Null means "nobody said", which is what lets
/// <see cref="OrchestratorSettings.EffectiveCompressThreshold"/> tell an explicit choice apart from
/// an absent one and derive something better from the model's window.</para>
///
/// <para>THERE WERE FOUR, AND TWO OF THEM WERE PROMISES. <c>goalTokenBudget</c> was documented as
/// "the real bound on cost" and enforced nothing — it raised one event into a message and the turn
/// carried on spending. <c>maxTokensPerCall</c> was parsed, written, and editable in Settings while
/// being read by no code at all. A configured limit that does not limit is worse than an absent one:
/// the user believes they are covered.</para>
///
/// <para>ONE TURN CAP, FOR EVERYONE. It was <c>maxWorkerTurns</c> and applied to the session agent
/// as well, so the name described a subset of what it did. Sub-agents inherit it unless their type
/// says otherwise — see <c>agents.&lt;name&gt;.maxTurns</c>.</para>
/// </summary>
/// <param name="MaxTurns">
/// Turns one request may take before it is stopped, or null when nobody said.
///
/// <para>NULLABLE IS THE POINT. It was <c>int</c> with a default of 200, which made "the user chose
/// 200" and "the user chose nothing" the same value — and the app worked around that by reading
/// <c>config.json</c> a second time, by hand, to recover the distinction. Making the absence
/// representable deleted that reader.</para>
///
/// <para>Zero means no cap, the same explicit opt-out an agent type gets.</para>
/// </param>
public record OrchestratorSettings(
    int? MaxTurns = null, int? ContextCompressThreshold = null)
{
    /// <summary>Nothing was said about either. What that MEANS is decided by the readers:
    /// <see cref="AgentHost.TurnCeiling"/> for turns, <see cref="EffectiveCompressThreshold"/> for
    /// compaction.</summary>
    public static readonly OrchestratorSettings Unbounded = new();

    /// <summary>
    /// The fixed fallback trigger for a provider whose context window nobody has told us: sized
    /// against a small LOCAL model's window (many run at/under 32K-64K context), not a frontier
    /// model's much larger one, so the out-of-the-box behaviour protects the constrained case. Applied
    /// by <see cref="AgentHost"/> — the only caller of <see cref="EffectiveCompressThreshold"/> — as
    /// its own last resort when that method has nothing to derive from (see its doc).
    /// </summary>
    public const int DefaultCompressThreshold = 40_000;

    /// <summary>
    /// P11 Task 2: resolves what is KNOWN about the compression trigger from config alone, given what
    /// is known about the ACTIVE provider's context window (ProviderInstanceConfig.ContextWindow —
    /// Task 1; null when the user hasn't told us). Precedence:
    ///
    ///   1. ContextCompressThreshold, if the user set one — they may know something about their setup
    ///      (e.g. a shared/rate-limited endpoint) that a raw window size does not capture;
    ///   2. else a fraction of a KNOWN context window — the honest trigger, scaled to the real budget
    ///      instead of a guess;
    ///   3. else null — NEITHER an explicit number NOR a window is known, so this method has nothing
    ///      to derive from. It does not invent the 40,000 constant itself; that last-resort fallback
    ///      lives one level up, in AgentHost (the only caller), which is where "we truly know
    ///      nothing" must still yield a protective, non-null number.
    ///
    /// The fraction is 80%, not "just under 100%": compression fires so the NEXT goal's decomposition
    /// call, its worker turns, and their tool outputs all still fit in the window — it has to leave
    /// headroom for what comes AFTER the trigger, not merely make the existing history fit exactly at
    /// the ceiling. A trigger at 95-99% would compress essentially in step with the provider's own
    /// rejection, defeating the point of measuring at all.
    /// </summary>
    public int? EffectiveCompressThreshold(int? contextWindow) =>
        ContextCompressThreshold
        ?? (contextWindow is { } window ? (int)(window * 0.8) : (int?)null);
}

/// <summary>
/// One MCP server to run as a child process.
///
/// <para><paramref name="Enabled"/> defaults to TRUE: a server someone bothered to configure is one
/// they want. Requiring <c>"enabled": true</c> on every entry is a footgun — the server silently
/// never appears and the config reads as though it should.</para>
///
/// <para><paramref name="TimeoutMs"/> is null-means-use-the-default rather than a number invented
/// here, so <see cref="Mcp.McpClient"/> keeps ownership of what that default is.</para>
/// </summary>
/// <param name="Command">
/// argv, NOT a shell line. <c>["npx", "-y", "pkg"]</c> — first element the executable, the rest
/// arguments passed through untouched. A single string would have to be split by us, and every
/// splitter either mishandles quoting or invites a shell, which turns a config file into a
/// code-execution seam.
/// </param>
/// <param name="Environment">
/// Variables for the child, merged OVER what it inherits from us.
///
/// <para>THE SPEC'S PRESCRIBED CREDENTIAL CHANNEL for stdio: <i>"Implementations using an STDIO
/// transport SHOULD NOT follow this specification, and instead retrieve credentials from the
/// environment."</i> A child already inherits our environment, so an exported variable reaches it —
/// but that is process-wide, and two servers needing different values for the same name cannot both
/// be served by it. This is the per-server override.</para>
/// </param>
/// <param name="WorkingDirectory">
/// Where to start the server, or null to inherit ours.
///
/// <para>Servers that take a path argument resolve it relative to their cwd, so one launched from
/// wherever cxagent happened to start reads a different tree than the user meant. Relative values
/// resolve against the project directory, matching opencode (<c>mcp/index.ts:346</c>).</para>
/// </param>
/// <param name="Url">
/// The endpoint of a REMOTE server, or null for a local one.
///
/// <para>THE TRANSPORT IS INFERRED FROM WHICH KEY IS PRESENT, not declared in a third field. A
/// command is a process and a url is an endpoint; they are never both meaningful, and asking the
/// user to also state which is a way to end up with a config that contradicts itself. An entry
/// carrying both — or neither — is skipped rather than guessed at, because picking one silently
/// could spawn a process for someone who meant to reach an endpoint.</para>
/// </param>
/// <param name="Headers">
/// Headers sent on every request to a remote server — the HTTP credential channel.
///
/// <para>A static <c>Authorization: Bearer …</c> or vendor API-key header covers most real servers
/// with no OAuth at all. The spec's OAuth flow only begins when a server answers 401, which is why
/// this alone makes the HTTP transport useful.</para>
/// </param>
public record McpServerConfig(
    IReadOnlyList<string> Command,
    bool Enabled = true,
    int? TimeoutMs = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? WorkingDirectory = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>True when this server is reached over HTTP rather than spawned.</summary>
    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// One configured sub-agent type: what it is for, and optionally which model runs it and how long
/// it may run.
///
/// <para>A BRIEFING IS A REQUEST, NOT A PERMISSION. It becomes the highest-authority text in the
/// child's system prompt — above the general prompt and above anything the parent says — but it does
/// not remove a tool. "Never edit files" asks; enforcement is a separate mechanism that does not
/// exist yet, and treating a briefing as a sandbox would be trusting prose to do a gate's job.</para>
/// </summary>
/// <param name="Briefing">HOW this agent works. Required — a type with nothing to say is the default
/// child under another name, and naming it invites the model to believe it picked something.</param>
/// <param name="Provider">
/// An instance name from <c>providers</c>, or null for the parent's. NOT a free-text model id: the
/// catalog already exists and a name that is not in it is caught at load rather than at spawn.
/// </param>
/// <param name="MaxTurns">
/// Turns before this type stops and summarises what it has. Null inherits the session ceiling, which
/// is what every child got before this existed. Zero means unbounded, matching
/// <c>AgentHost.TurnCeiling</c> and <c>Agent</c>'s own <c>maxTurns &lt;= 0</c> translation.
///
/// <para>A CAP THAT FIRES MID-WORK DOES NOT FAIL LOUDLY — it returns a salvage summary of unfinished
/// work. The envelope marks it (<c>state="capped"</c>), but a number set too low turns every run of
/// that type into a half-answer. Set it only where the job has a knowable shape.</para>
/// </param>
public record AgentTypeConfig(string Briefing, string? Provider = null, int? MaxTurns = null);

public record ProviderSettings(
    IReadOnlyDictionary<string, ProviderInstanceConfig> Providers,
    string? DefaultProvider,
    IReadOnlyList<string> AllowedProviders,
    IReadOnlyDictionary<string, RoutingTarget> Routing,
    OrchestratorSettings? Orchestrator = null)
{
    public OrchestratorSettings Orchestrator { get; init; } = Orchestrator ?? OrchestratorSettings.Unbounded;

    /// <summary>Configured MCP servers, empty when the block is absent — which is the common case.</summary>
    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; } =
        new Dictionary<string, McpServerConfig>();

    /// <summary>
    /// Configured sub-agent types, empty when the block is absent — the common case.
    ///
    /// <para>Empty does NOT mean no types: the implicit <c>general</c> is supplied downstream, so a
    /// user with no <c>agents</c> block still has a catalog of one. Holding it here rather than
    /// seeding it in would make config the place that decides what <c>general</c> is, and it is not —
    /// config only gets to OVERRIDE it.</para>
    /// </summary>
    public IReadOnlyDictionary<string, AgentTypeConfig> AgentTypes { get; init; } =
        new Dictionary<string, AgentTypeConfig>();

    /// <summary>
    /// Non-fatal complaints from the load, for the UI to show.
    ///
    /// <para>Everything else this loader dislikes goes into <see cref="ProviderConfigException"/> and
    /// stops the app. That is right for a provider — there is no session without one — and wrong for
    /// an optional tool server, where it would mean a typo'd command line takes down providers,
    /// session and all. Warnings are the middle ground the MCP block needs: drop the bad entry, keep
    /// going, and still SAY so, because a server that silently never appears is its own bug.</para>
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ProviderConfigException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public ProviderConfigException(IReadOnlyList<string> errors)
        : base("Provider configuration invalid:\n  - " + string.Join("\n  - ", errors))
        => Errors = errors;
}

/// <summary>Reads &lt;ConfigDir&gt;/config.json, applies env-var key overrides, and validates it whole.</summary>
public static class ProviderConfigLoader
{
    public static readonly IReadOnlySet<string> KnownKinds =
        new HashSet<string> { "anthropic", "openai-compatible", "ollama" };

    private static readonly IReadOnlySet<string> KeylessKinds =
        new HashSet<string> { "ollama" };

    public static ProviderSettings LoadAndValidate(AppPaths paths, IReadOnlyDictionary<string, string> env)
    {
        var errors = new List<string>();
        var path = Path.Combine(paths.ConfigDir, "config.json");
        if (!File.Exists(path))
            throw new ProviderConfigException(new[] { $"config.json not found at '{path}'." });

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new ProviderConfigException(new[] { $"config.json is not valid JSON: {ex.Message}" }); }

        using (doc)
        {
            var root = doc.RootElement;
            var providers = new Dictionary<string, ProviderInstanceConfig>();

            if (root.TryGetProperty("providers", out var provs) && provs.ValueKind == JsonValueKind.Object)
                foreach (var entry in provs.EnumerateObject())
                {
                    var name = entry.Name;
                    var o = entry.Value;
                    string kind = o.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    string model = o.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    string? apiKey = o.TryGetProperty("apiKey", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    string? baseUrl = o.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                    int? contextWindow = o.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number
                        ? cw.GetInt32() : null;
                    // ABSENT OR 0 IS UNLIMITED — the same convention maxTurns uses, so a reader who
                    // knows one knows the other. Negative is treated as absent rather than rejected:
                    // a config typo should not stop a session starting over a performance hint.
                    int? maxConcurrentAgents = o.TryGetProperty("maxConcurrentAgents", out var mca)
                        && mca.ValueKind == JsonValueKind.Number && mca.GetInt32() > 0
                        ? mca.GetInt32() : null;

                    // env override: CXAGENT_PROVIDER_<INSTANCE>_APIKEY
                    var envKey = $"CXAGENT_PROVIDER_{name.ToUpperInvariant()}_APIKEY";
                    if (env.TryGetValue(envKey, out var envVal) && !string.IsNullOrEmpty(envVal))
                        apiKey = envVal;

                    Dictionary<string, string>? extra = null;
                    if (o.TryGetProperty("extraHeaders", out var eh) && eh.ValueKind == JsonValueKind.Object)
                    {
                        extra = new();
                        foreach (var h in eh.EnumerateObject()) extra[h.Name] = h.Value.GetString() ?? "";
                    }

                    if (!KnownKinds.Contains(kind))
                        errors.Add($"provider '{name}': unknown kind '{kind}' (known: {string.Join(", ", KnownKinds)}).");
                    if (string.IsNullOrWhiteSpace(model))
                        errors.Add($"provider '{name}': 'model' is required.");
                    if (kind == "openai-compatible" && string.IsNullOrWhiteSpace(baseUrl))
                        errors.Add($"provider '{name}': 'baseUrl' is required for kind 'openai-compatible'.");
                    if (KnownKinds.Contains(kind) && !KeylessKinds.Contains(kind) && string.IsNullOrWhiteSpace(apiKey))
                        errors.Add($"provider '{name}': 'apiKey' is required for kind '{kind}' (or set {envKey}).");

                    providers[name] = new ProviderInstanceConfig(kind, model, apiKey, baseUrl, extra, contextWindow,
                        maxConcurrentAgents);
                }
            else
                errors.Add("config.json has no 'providers' object.");

            string? defaultProvider = root.TryGetProperty("defaultProvider", out var dp) && dp.ValueKind == JsonValueKind.String
                ? dp.GetString() : null;
            if (defaultProvider is not null && !providers.ContainsKey(defaultProvider))
                errors.Add($"defaultProvider '{defaultProvider}' is not a configured provider instance.");

            var allowed = new List<string>();
            var routing = new Dictionary<string, RoutingTarget>();
            // Seeded from RoleRegistry.Builtins so Roles is NEVER empty for a valid config — a config
            // with no 'llmAgent' block still yields the four built-ins, all with Target = null. Config
            // only overlays; it can neither invent a built-in nor demote one (see ROLES INVARIANT).

            if (root.TryGetProperty("llmAgent", out var la) && la.ValueKind == JsonValueKind.Object)
            {
                if (la.TryGetProperty("allowedProviders", out var ap) && ap.ValueKind == JsonValueKind.Array)
                    foreach (var e in ap.EnumerateArray())
                    {
                        var n = e.GetString();
                        if (n is null) continue;
                        allowed.Add(n);
                        if (!providers.ContainsKey(n))
                            errors.Add($"llmAgent.allowedProviders references unknown provider '{n}'.");
                    }
                if (la.TryGetProperty("routing", out var rt) && rt.ValueKind == JsonValueKind.Object)
                    foreach (var e in rt.EnumerateObject())
                    {
                        var prov = e.Value.TryGetProperty("provider", out var pv) ? pv.GetString() ?? "" : "";
                        var mdl = e.Value.TryGetProperty("model", out var mv) ? mv.GetString() ?? "" : "";
                        routing[e.Name] = new RoutingTarget(prov, mdl);
                        if (!providers.ContainsKey(prov))
                            errors.Add($"llmAgent.routing.{e.Name} references unknown provider '{prov}'.");
                        if (string.IsNullOrWhiteSpace(mdl))
                            errors.Add($"llmAgent.routing.{e.Name}: 'model' is required.");
                    }

            }

            // HOISTED ABOVE THE ORCHESTRATOR BLOCK, which now warns about removed keys and about a
            // negative cap. One list for the whole load; the MCP block below appends to the same one.
            var warnings = new List<string>();

            var orchestrator = OrchestratorSettings.Unbounded;
            if (root.TryGetProperty("orchestrator", out var orch) && orch.ValueKind == JsonValueKind.Object)
            {
                // ABSENT STAYS NULL, for both. "Nobody said" and "somebody said the default" are
                // different states, and collapsing them is what forced the old raw-JSON re-read.
                int? maxTurns = null;
                if (orch.TryGetProperty("maxTurns", out var mt) && mt.ValueKind == JsonValueKind.Number)
                {
                    var value = mt.GetInt32();
                    // NEGATIVE IS IGNORED, ZERO IS KEPT — zero is the explicit "no cap", the same
                    // opt-out an agent type gets, so it cannot be clamped away with the invalid ones.
                    if (value < 0) warnings.Add("orchestrator.maxTurns is negative; ignored.");
                    else maxTurns = value;
                }

                int? contextCompressThreshold = orch.TryGetProperty("contextCompressThreshold", out var cct) && cct.ValueKind == JsonValueKind.Number
                    ? cct.GetInt32() : null;

                orchestrator = new OrchestratorSettings(maxTurns, contextCompressThreshold);
            }

            // MCP SERVERS ARE NEVER FATAL. A bad entry is skipped with a warning naming it; the rest
            // of the config — every provider, the whole session — loads regardless. See
            // ProviderSettings.Warnings for why this one block is not on the errors list.
            var mcpServers = new Dictionary<string, McpServerConfig>();
            if (root.TryGetProperty("mcp", out var mcp) && mcp.ValueKind == JsonValueKind.Object)
                foreach (var entry in mcp.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add($"mcp.{entry.Name} is not an object; skipped.");
                        continue;
                    }

                    var command = new List<string>();
                    if (entry.Value.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.Array)
                        foreach (var part in cmd.EnumerateArray())
                            if (part.ValueKind == JsonValueKind.String && part.GetString() is { } s
                                && !string.IsNullOrWhiteSpace(s))
                                command.Add(s);

                    var url = entry.Value.TryGetProperty("url", out var u)
                           && u.ValueKind == JsonValueKind.String
                           && !string.IsNullOrWhiteSpace(u.GetString())
                        ? u.GetString()!.Trim() : null;

                    // EXACTLY ONE OF command / url. Both is ambiguous and neither is nothing to
                    // start; either way the entry is skipped with the reason named, rather than
                    // silently resolved into whichever transport we happened to prefer.
                    if (command.Count > 0 && url is not null)
                    {
                        warnings.Add($"mcp.{entry.Name} has both 'command' and 'url'; skipped — "
                                   + "a server is either a local command or a remote url.");
                        continue;
                    }

                    if (command.Count == 0 && url is null)
                    {
                        warnings.Add($"mcp.{entry.Name} has no 'command' or 'url'; skipped.");
                        continue;
                    }

                    var enabled = !entry.Value.TryGetProperty("enabled", out var en)
                               || en.ValueKind != JsonValueKind.False;
                    int? timeoutMs = entry.Value.TryGetProperty("timeoutMs", out var tm)
                                  && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : null;

                    // Null, not an empty dictionary, when absent: "inherit ours" must stay
                    // distinguishable from "start with nothing".
                    Dictionary<string, string>? environment = null;
                    if (entry.Value.TryGetProperty("env", out var envBlock)
                        && envBlock.ValueKind == JsonValueKind.Object)
                    {
                        environment = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var v in envBlock.EnumerateObject())
                            if (v.Value.ValueKind == JsonValueKind.String)
                                environment[v.Name] = v.Value.GetString() ?? "";
                    }

                    var cwd = entry.Value.TryGetProperty("cwd", out var wd)
                           && wd.ValueKind == JsonValueKind.String
                           && !string.IsNullOrWhiteSpace(wd.GetString())
                        ? wd.GetString() : null;

                    Dictionary<string, string>? headers = null;
                    if (entry.Value.TryGetProperty("headers", out var headerBlock)
                        && headerBlock.ValueKind == JsonValueKind.Object)
                    {
                        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in headerBlock.EnumerateObject())
                            if (h.Value.ValueKind == JsonValueKind.String)
                                headers[h.Name] = h.Value.GetString() ?? "";
                    }

                    // {env:…} and {file:…} expand in header and environment VALUES only — never in
                    // the command, where interpolating into an argv that spawns a process would turn
                    // a config file into a code-execution seam.
                    headers = (Dictionary<string, string>?)ConfigVariable.SubstituteValues(
                        headers, warnings, $"mcp.{entry.Name}.headers", StringComparer.OrdinalIgnoreCase);
                    environment = (Dictionary<string, string>?)ConfigVariable.SubstituteValues(
                        environment, warnings, $"mcp.{entry.Name}.env", StringComparer.Ordinal);

                    mcpServers[entry.Name] =
                        new McpServerConfig(command, enabled, timeoutMs, environment, cwd, url, headers);
                }

            // SUB-AGENT TYPES. Warnings rather than errors, for the reason the MCP block gives:
            // everything that stops the app is something there is no session without, and a type is
            // an optional convenience. A typo'd briefing must not take providers down with it.
            //
            // The one difference from MCP: a bad type is DROPPED, and dropping it is visible — the
            // model is offered a catalog, so a type that silently never appears is a type the user
            // watches the model fail to use.
            var agentTypes = new Dictionary<string, AgentTypeConfig>(StringComparer.Ordinal);
            if (root.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Object)
                foreach (var entry in agents.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add($"agents.{entry.Name} is not an object; skipped.");
                        continue;
                    }

                    // REQUIRED. A type with nothing to say is the default child under another name,
                    // and naming it invites the model to believe it picked something.
                    var briefing = entry.Value.TryGetProperty("briefing", out var b)
                                && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                    if (string.IsNullOrWhiteSpace(briefing))
                    {
                        warnings.Add($"agents.{entry.Name} has no 'briefing'; skipped.");
                        continue;
                    }

                    // NAMES AN INSTANCE FROM `providers`, checked HERE rather than at spawn time. A
                    // typo found three turns into a child's run reads as a sub-agent bug; found at
                    // load it reads as what it is.
                    string? typeProvider = null;
                    if (entry.Value.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String)
                    {
                        var name = pv.GetString();
                        if (string.IsNullOrWhiteSpace(name) || !providers.ContainsKey(name))
                        {
                            warnings.Add($"agents.{entry.Name}.provider '{name}' is not a configured "
                                       + $"provider (known: {string.Join(", ", providers.Keys)}); "
                                       + "using the parent's.");
                        }
                        else typeProvider = name;
                    }

                    // NULL INHERITS, 0 IS UNBOUNDED, NEGATIVE IS A TYPO. Zero carries meaning here —
                    // it is the same explicit opt-out AgentHost.TurnCeiling gives the session — so it
                    // cannot be lumped in with the invalid values and clamped away.
                    int? maxTurns = null;
                    if (entry.Value.TryGetProperty("maxTurns", out var mt) && mt.ValueKind == JsonValueKind.Number)
                    {
                        var value = mt.GetInt32();
                        if (value < 0) warnings.Add($"agents.{entry.Name}.maxTurns is negative; ignored.");
                        else maxTurns = value;
                    }

                    agentTypes[entry.Name] = new AgentTypeConfig(briefing.Trim(), typeProvider, maxTurns);
                }

            if (errors.Count > 0)
                throw new ProviderConfigException(errors);

            return new ProviderSettings(providers, defaultProvider, allowed, routing, orchestrator)
            {
                McpServers = mcpServers,
                AgentTypes = agentTypes,
                Warnings = warnings,
            };
        }
    }
}
