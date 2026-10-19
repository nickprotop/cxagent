using CxAgent.Core.Config;
using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>
/// An instance being edited in the dialog: the values on screen beside the ones the file holds.
///
/// <para>ONLY WHAT CHANGED IS WRITTEN. <see cref="ToFields"/> sets a key the user changed and clears one
/// they emptied; everything else is left exactly as the file has it — including keys this draft does not
/// know, which the document edit keeps by construction.</para>
///
/// <para>THE KEY IS WRITE-ONLY. <see cref="NewApiKey"/> starts empty whatever is stored: a value typed
/// replaces it, <see cref="ClearApiKey"/> removes it, and nothing reads the stored one back.</para>
/// </summary>
public sealed class InstanceDraft(string name, ProviderInstanceConfig original)
{
    public string Name { get; set; } = name;

    /// <summary>Not in the file yet: everything it holds is written, its kind included.</summary>
    public bool IsNew { get; init; }
    public ProviderInstanceConfig Original { get; } = original;

    public string Model { get; set; } = original.Model;
    public string? BaseUrl { get; set; } = original.BaseUrl;
    public string? Description { get; set; } = original.Description;
    public string? NewApiKey { get; set; }
    public bool ClearApiKey { get; set; }
    public string? Thinking { get; set; } = original.Thinking;
    public ThinkingDisplay ThinkingDisplay { get; set; } = original.ThinkingDisplay;
    public bool Reasoning { get; set; } = original.Reasoning;
    public bool CacheControl { get; set; } = original.CacheControl;
    public int? ContextWindow { get; set; } = original.ContextWindow;
    public int? MaxConcurrentAgents { get; set; } = original.MaxConcurrentAgents;
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; set; } = original.ExtraHeaders;

    private bool KeyChanged => ClearApiKey ? Original.ApiKey is not null : !string.IsNullOrEmpty(NewApiKey);
    private bool UrlChanged => Blank(BaseUrl) != Blank(Original.BaseUrl);
    private bool DescriptionChanged => Blank(Description) != Blank(Original.Description);
    private bool HeadersChanged => !SameHeaders(ExtraHeaders, Original.ExtraHeaders);

    /// <summary>Whether anything on screen differs from the file.</summary>
    public bool IsDirty =>
        Model != Original.Model || UrlChanged || DescriptionChanged || KeyChanged || Thinking != Original.Thinking
        || ThinkingDisplay != Original.ThinkingDisplay || Reasoning != Original.Reasoning
        || CacheControl != Original.CacheControl || ContextWindow != Original.ContextWindow
        || MaxConcurrentAgents != Original.MaxConcurrentAgents || HeadersChanged;

    /// <summary>The edit: changed keys set, emptied keys cleared, the rest untouched.</summary>
    public InstanceFields ToFields()
    {
        var clear = new HashSet<string>();
        string? Text(string? now, string? was, string key)
        {
            if (Blank(now) == Blank(was)) return null;
            if (Blank(now) is null) { clear.Add(key); return null; }
            return now!.Trim();
        }
        int? Number(int? now, int? was, string key)
        {
            if (now == was) return null;
            if (now is null) { clear.Add(key); return null; }
            return now;
        }

        if (ClearApiKey && Original.ApiKey is not null) clear.Add("apiKey");
        var fields = new InstanceFields
        {
            Kind = IsNew ? Original.Kind : null,
            Model = Model != Original.Model || IsNew ? Model.Trim() : null,
            BaseUrl = Text(BaseUrl, Original.BaseUrl, "baseUrl"),
            Description = Text(Description, Original.Description, "description"),
            ApiKey = !ClearApiKey && !string.IsNullOrEmpty(NewApiKey) ? NewApiKey : null,
            Thinking = Text(Thinking, Original.Thinking, "thinking"),
            ThinkingDisplay = ThinkingDisplay != Original.ThinkingDisplay
                ? (ThinkingDisplay == ThinkingDisplay.Omitted ? "omitted" : "summarized") : null,
            Reasoning = Reasoning != Original.Reasoning ? Reasoning : null,
            CacheControl = CacheControl != Original.CacheControl ? CacheControl : null,
            ContextWindow = Number(ContextWindow, Original.ContextWindow, "contextWindow"),
            MaxConcurrentAgents = Number(MaxConcurrentAgents, Original.MaxConcurrentAgents, "maxConcurrentAgents"),
        };
        if (HeadersChanged)
        {
            if (ExtraHeaders is not { Count: > 0 }) clear.Add("extraHeaders");
            else fields = fields with { ExtraHeaders = ExtraHeaders };
        }
        return fields with { Clear = clear };
    }

    /// <summary>The instance as it would be saved — for asking the endpoint before anything is written.</summary>
    public ProviderInstanceConfig ToConfig() => Original with
    {
        Model = Model.Trim(),
        BaseUrl = Blank(BaseUrl),
        Description = Blank(Description),
        ApiKey = ClearApiKey ? null : !string.IsNullOrEmpty(NewApiKey) ? NewApiKey : Original.ApiKey,
        Thinking = Thinking,
        ThinkingDisplay = ThinkingDisplay,
        Reasoning = Reasoning,
        CacheControl = CacheControl,
        ContextWindow = ContextWindow,
        MaxConcurrentAgents = MaxConcurrentAgents,
        ExtraHeaders = ExtraHeaders,
    };

    /// <summary>
    /// Which running sessions a save of this draft moves.
    ///
    /// <para>EVERYONE when the change reaches every session's wiring: this instance reviews auto mode
    /// (each session builds its classifier at wire time), it is the default and its concurrency limit
    /// changed (that limit sizes each session's sub-agent semaphore), or an agent type lists it (types
    /// capture each listed provider at wire time). Otherwise only the tabs on this instance.</para>
    /// </summary>
    /// <summary>
    /// What adding this instance changes. NOBODY IS ON A NEW INSTANCE, so no tab moves — but EVERYONE is
    /// touched when it is the first one (the edit makes it the default, whose limit sizes every session's
    /// sub-agent semaphore) or when an agent type lists it (a type disabled for naming it comes back, and
    /// types are rebuilt per session).
    /// </summary>
    public ConfigChange AddChange(ProviderSettings settings) =>
        new(new HashSet<string>(), new Dictionary<string, string>(),
            settings.DefaultProvider is null || settings.AgentTypes.Values.Any(t => t.Lists(Name)), $"{Name} added");

    public ConfigChange ChangeFor(ProviderSettings settings)
    {
        var everyone = settings.Classifier == Name
            || (settings.DefaultProvider == Name && MaxConcurrentAgents != Original.MaxConcurrentAgents)
            || settings.AgentTypes.Values.Any(t => t.Lists(Name));
        // THE MODEL OR THE SERVER CHANGED: a tab's cache does not survive either, so it is asked about.
        var breaks = Model.Trim() != Original.Model || UrlChanged;
        return new ConfigChange(new HashSet<string> { Name }, new Dictionary<string, string>(), everyone, Note())
        {
            ModelChanged = breaks ? new HashSet<string> { Name } : new HashSet<string>(),
        };
    }

    /// <summary>What a re-wired tab is told: the instance, and what changed in it.</summary>
    private string Note()
    {
        var what = new List<string>();
        if (Model != Original.Model) what.Add($"model {Original.Model} → {Model}");
        if (UrlChanged) what.Add("endpoint");
        if (DescriptionChanged) what.Add("description");
        if (KeyChanged) what.Add("API key");
        if (Thinking != Original.Thinking) what.Add($"thinking {Original.Thinking ?? "default"} → {Thinking ?? "default"}");
        if (what.Count == 0) what.Add("settings");
        return $"{Name} updated: {string.Join(", ", what)}";
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool SameHeaders(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if ((a?.Count ?? 0) == 0 && (b?.Count ?? 0) == 0) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        return a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }
}
