using System.Text.Json;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Llm;

/// <summary>
/// ContextWindow (P11 Task 1) is the model's real context size in tokens — a property of the MODEL,
/// not of cxagent, so only the user (who knows what a custom endpoint is serving) can set it
/// reliably. Trailing default for the same reason MaxWorkerTurns/Copilot are: nothing recomputes
/// this record after construction, so appending a defaulted field can't break any of the 26
/// existing positional construction sites.
///
/// null-means-unknown, DELIBERATELY not defaulted to some guessed number: "we don't know the
/// window" must stay distinguishable from "we know it is N", because the compression threshold
/// (Task 2) branches on exactly that difference. Substituting a made-up value here would silently
/// drive compression at the wrong point — the bug this feature exists to fix.
/// </summary>
public record ProviderInstanceConfig(
    string Kind, string Model, string? ApiKey, string? BaseUrl,
    IReadOnlyDictionary<string, string>? ExtraHeaders, int? ContextWindow = null);

public record RoutingTarget(string Provider, string Model);

/// <summary>
/// Orchestrator-level cost caps. MaxTokensPerCall/GoalTokenBudget both null means unbounded — the
/// default, so cost control is opt-in and an absent 'orchestrator' block never makes a goal breach
/// instantly on its first call.
///
/// MaxWorkerTurns bounds the agent's tool loop (call model → run tool → call model → ...), which
/// without it can spend the whole token budget inside one session. A real non-zero default, never
/// null-means-unbounded.
///
/// THE VALUES ARE SET WHERE A LOOP LIVES, NOT WHERE WORK LIVES. They exist to stop a runaway, and a
/// cap tight enough to bite ordinary work is worse than none: it fails silently, mid-task, with a
/// partial result the caller reports as success.
///
/// Measured — MaxWorkerTurns was 10, and an implementer asked to edit six files spent all ten turns
/// READING them (16 read_file calls across two jobs, zero writes) and reported done. Editing N files
/// costs roughly 2N turns before discovery or a retry on a failed match, so ten files is already
/// ~25; 10 had no idea how many files the job named, and even 40 would bind a real refactor.
///
/// NOT unbounded, though, and the reason is specific: the token budget that would otherwise be the
/// backstop defaults to NULL — unbounded. Uncapped turns plus unbounded tokens means nothing stops an
/// agent stuck in a read-loop from spending the whole session. 200 is far past any plausible piece of
/// real work and still catches a loop.
///
/// ContextCompressThreshold (P10 Task 3, reshaped by P11 Task 2) is a MEASURED trigger, not a cap: it
/// names the live context size (LlmResponse.Usage.InputTokens — the provider's own count of what it
/// just received) above which AgentHost compresses the shared conversation after a goal completes.
///
/// int?, null meaning "not configured" — NOT null-means-unbounded like the token fields above.
/// Before P11 this was a plain int defaulting to 40,000, which made an explicit
/// `"contextCompressThreshold": 40000` in config indistinguishable from an absent one. That
/// collapsed two different states that <see cref="EffectiveCompressThreshold"/> must tell apart: "the
/// user chose this number" (honour it, even over a known context window) vs "nobody said" (derive
/// something better if we can). Default null, both here and as parsed by
/// <see cref="ProviderConfigLoader.LoadAndValidate"/> when the key is absent — the 40,000 constant
/// only enters at the very end of the chain, in <see cref="AgentHost"/>'s own fallback, once no
/// caller (neither this record nor a known window) had an opinion.
/// </summary>
public record OrchestratorSettings(
    int? MaxTokensPerCall, int? GoalTokenBudget,
    int MaxWorkerTurns = 200, int? ContextCompressThreshold = null)
{
    // NOTE: "Unbounded" only describes the token fields — MaxWorkerTurns always takes its real
    // (non-null, non-zero) default here too. ContextCompressThreshold
    // is unconfigured here too (null): Unbounded means "nothing was said," and EffectiveCompressThreshold
    // (plus AgentHost's own last-resort constant) decides what happens when nothing was said.
    public static readonly OrchestratorSettings Unbounded = new(null, null);

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

public record ProviderSettings(
    IReadOnlyDictionary<string, ProviderInstanceConfig> Providers,
    string? DefaultProvider,
    IReadOnlyList<string> AllowedProviders,
    IReadOnlyDictionary<string, RoutingTarget> Routing,
    OrchestratorSettings? Orchestrator = null)
{
    public OrchestratorSettings Orchestrator { get; init; } = Orchestrator ?? OrchestratorSettings.Unbounded;
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

                    providers[name] = new ProviderInstanceConfig(kind, model, apiKey, baseUrl, extra, contextWindow);
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

            var orchestrator = OrchestratorSettings.Unbounded;
            if (root.TryGetProperty("orchestrator", out var orch) && orch.ValueKind == JsonValueKind.Object)
            {
                int? maxTokensPerCall = orch.TryGetProperty("maxTokensPerCall", out var mtpc) && mtpc.ValueKind == JsonValueKind.Number
                    ? mtpc.GetInt32() : null;
                int? goalTokenBudget = orch.TryGetProperty("goalTokenBudget", out var gtb) && gtb.ValueKind == JsonValueKind.Number
                    ? gtb.GetInt32() : null;
                int maxWorkerTurns = orch.TryGetProperty("maxWorkerTurns", out var mwt) && mwt.ValueKind == JsonValueKind.Number
                    ? mwt.GetInt32() : OrchestratorSettings.Unbounded.MaxWorkerTurns;
                // int? now (P11 Task 2): null must stay distinguishable from "explicitly 40000" so
                // EffectiveCompressThreshold's precedence can tell "the user chose this" apart from
                // "nobody said" — an absent key here means the derived-from-window path stays live.
                int? contextCompressThreshold = orch.TryGetProperty("contextCompressThreshold", out var cct) && cct.ValueKind == JsonValueKind.Number
                    ? cct.GetInt32() : null;
                orchestrator = new OrchestratorSettings(maxTokensPerCall, goalTokenBudget, maxWorkerTurns, contextCompressThreshold);
            }

            if (errors.Count > 0)
                throw new ProviderConfigException(errors);

            return new ProviderSettings(providers, defaultProvider, allowed, routing, orchestrator);
        }
    }
}
