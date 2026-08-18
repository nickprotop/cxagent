using System.Text.Json;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Core.Agents;

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
/// <param name="CacheControl">
/// Ask this endpoint to cache the system prompt, by sending a cache_control breakpoint.
///
/// <para>OPT-IN BECAUSE WRITES ARE BILLED. Anthropic charges 1.25x normal input to write a
/// five-minute cache entry and 2x for an hour; Gemini charges input plus storage. It repays only
/// when the prefix is reused before expiry, so it must be a decision rather than a default.</para>
///
/// <para>Needed at all because those providers cache NOTHING without it — measured through
/// OpenRouter: the same 7,002-token prefix twice gave 0 cached without a breakpoint and 7,002
/// with one.</para>
/// </param>
public record ProviderInstanceConfig(
    string Kind, string Model, string? ApiKey, string? BaseUrl,
    IReadOnlyDictionary<string, string>? ExtraHeaders, int? ContextWindow = null,
    int? MaxConcurrentAgents = null,
    bool CacheControl = false);

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

    /// <summary>
    /// Whether two configurations describe the same server.
    ///
    /// <para>NOT record equality, which is the trap this exists to close. Three members are
    /// collections — Command, Environment, Headers — and a record compares those BY REFERENCE, so
    /// two identical configs read from disk on either side of a save never compare equal. A caller
    /// asking "did the servers change?" would be told yes every time.</para>
    ///
    /// <para>ON THE TYPE, not at the call site, because the failure mode is an unchecked member: the
    /// first version of this comparison lived in AppBootstrap and silently omitted Headers. Adding
    /// an eighth member is a change to this file, which is where the reader already is.</para>
    /// </summary>
    public bool DescribesSameServerAs(McpServerConfig other) =>
        Enabled == other.Enabled
        && TimeoutMs == other.TimeoutMs
        && WorkingDirectory == other.WorkingDirectory
        && Url == other.Url
        && Command.SequenceEqual(other.Command)
        && SameMap(Environment, other.Environment)
        && SameMap(Headers, other.Headers);

    private static bool SameMap(IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        var left = a ?? EmptyMap;
        var right = b ?? EmptyMap;
        if (left.Count != right.Count) return false;
        foreach (var (k, v) in left)
            if (!right.TryGetValue(k, out var other) || other != v) return false;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>();
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
/// <param name="Description">
/// WHEN to choose this type, and what comes back — for the PARENT deciding. It is the type's one line
/// in the spawn tool's catalog, and the only thing the model sees before picking.
///
/// <para>SEPARATE FROM THE BRIEFING BECAUSE THE READERS ARE. A briefing is written in the second
/// person for the child and opens with what it must do first. A catalog line needs what a chooser
/// should notice first, and what it will get back. The catalog used to DERIVE one from the other —
/// the briefing's first sentence — and produced lines like "You search and report.": accurate,
/// grammatically about the agent, and useless to anyone deciding whether to reach for it.</para>
///
/// <para>NO LENGTH LIMIT. The old derivation capped at 140 characters, which was right for a
/// SCAVENGED sentence that might run away; this is text a human wrote for this slot, and truncating
/// it would be the app rewriting its author. The pressure it guarded against is real — this ships in
/// every request — but that argues for writing short descriptions, not for mangling long ones.</para>
///
/// <para>Null means absent, which is every config written before this key existed. The catalog then
/// says nothing was configured rather than inventing a line.</para>
/// </param>
public record AgentTypeConfig(string? Briefing = null, string? Provider = null, int? MaxTurns = null,
    string? Description = null);

public record ProviderSettings(
    IReadOnlyDictionary<string, ProviderInstanceConfig> Providers,
    string? DefaultProvider,
    IReadOnlyList<string> AllowedProviders,
    IReadOnlyDictionary<string, RoutingTarget> Routing,
    OrchestratorSettings? Orchestrator = null)
{
    public OrchestratorSettings Orchestrator { get; init; } = Orchestrator ?? OrchestratorSettings.Unbounded;

    /// <summary>
    /// Which instance reviews actions in <c>/mode edits auto</c>, or null when none is configured.
    ///
    /// <para>NULL MEANS AUTO IS NOT OFFERED — not listed, not cyclable, not parseable. A mode that
    /// claims background review while nothing reviews is worse than not having the mode.</para>
    /// </summary>
    public string? Classifier { get; init; }

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

                    // ABSENT IS FALSE, and deliberately: a provider that bills for cache writes must
                    // not be opted in by a config that never mentioned caching.
                    var cacheControl = o.TryGetProperty("cacheControl", out var cc)
                        && cc.ValueKind == JsonValueKind.True;

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
                        maxConcurrentAgents, cacheControl);
                }
            else
                errors.Add("config.json has no 'providers' object.");

            string? defaultProvider = root.TryGetProperty("defaultProvider", out var dp) && dp.ValueKind == JsonValueKind.String
                ? dp.GetString() : null;
            if (defaultProvider is not null && !providers.ContainsKey(defaultProvider))
                errors.Add($"defaultProvider '{defaultProvider}' is not a configured provider instance.");

            // WHICH INSTANCE REVIEWS ACTIONS IN `/mode edits auto`. Absent is the common case and is
            // not an error: it means auto is not offered at all — unlisted, unreachable by Shift+Tab,
            // unparseable as a value. A NAMED-BUT-MISSING instance IS an error, because that is a user
            // who believes they configured review and would otherwise get none, silently.
            string? classifier = root.TryGetProperty("classifier", out var cl) && cl.ValueKind == JsonValueKind.String
                ? cl.GetString() : null;
            if (classifier is not null && !providers.ContainsKey(classifier))
                errors.Add($"classifier '{classifier}' is not a configured provider instance.");

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

                    var briefing = entry.Value.TryGetProperty("briefing", out var b)
                                && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                    var description = entry.Value.TryGetProperty("description", out var dsc)
                                   && dsc.ValueKind == JsonValueKind.String ? dsc.GetString() : null;

                    // A BUILT-IN NAME KEEPS ITS SHIPPED TEXT, and says so rather than quietly winning
                    // or quietly losing. The briefing is the contract a type keeps with the code
                    // around it — the planner is told to write the file whose path the spawner
                    // supplies, the spawner reports whether it appeared, and the builder refuses work
                    // that arrives without one — so a copy in a user's config file is a third party to
                    // an agreement between three others, free to drift from all of them. Honouring it
                    // would restore exactly the drift moving them into code was meant to end.
                    //
                    // IGNORED LOUDLY, not silently: an edit that does nothing and says nothing is the
                    // worst of the three options. `provider` and `maxTurns` still apply, because
                    // where a type runs and what it may spend are genuinely the user's.
                    if (BuiltinAgentTypes.IsBuiltin(entry.Name))
                    {
                        if (briefing is not null)
                            warnings.Add($"agents.{entry.Name}.briefing is ignored: built-in agent "
                                       + "briefings ship with cxagent so they stay in step with the "
                                       + "code that depends on them. Remove it, or rename the type to "
                                       + "define your own.");
                        if (description is not null)
                            warnings.Add($"agents.{entry.Name}.description is ignored, for the same "
                                       + $"reason as its briefing. 'provider' and 'maxTurns' still apply.");
                        briefing = null;      // the catalog takes the shipped text
                        description = null;
                    }
                    // REQUIRED FOR A TYPE THAT IS NOT BUILT IN. A type with nothing to say is the
                    // default child under another name, and naming it invites the model to believe it
                    // picked something.
                    else if (string.IsNullOrWhiteSpace(briefing))
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

                    // WHITESPACE IS ABSENT, not a description. A line of spaces would render as a
                    // blank catalog entry, which reads as a bug; no entry reads as "nothing was
                    // configured", which is the truth. Not trimmed-then-kept for the same reason.
                    description = description?.Trim();
                    if (string.IsNullOrWhiteSpace(description)) description = null;

                    // BRIEFING IS EMPTY FOR A BUILT-IN NAME, and the catalog substitutes the shipped
                    // text. Storing the shipped briefing here instead would put it back in the shape
                    // that drifts — a copy, made at load, of something that lives elsewhere.
                    agentTypes[entry.Name] = new AgentTypeConfig(briefing?.Trim(), typeProvider,
                        maxTurns, description);
                }

            if (errors.Count > 0)
                throw new ProviderConfigException(errors);

            return new ProviderSettings(providers, defaultProvider, allowed, routing, orchestrator)
            {
                McpServers = mcpServers,
                AgentTypes = agentTypes,
                Warnings = warnings,
                Classifier = classifier,
            };
        }
    }
}
