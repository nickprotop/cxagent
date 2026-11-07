using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.Core.Agents;
using CxAgent.UI.Config;

namespace CxAgent.Web;

/// <summary>
/// F8's tabs as forms for a browser: each tab's fields described from the draft that tab edits, and a save that
/// sets that draft from what the page sent and writes it with the draft's own Apply — so the browser and the terminal
/// write config.json the same way, refuse the same values for the same reasons, and leave the same keys alone.
///
/// <para>A DESCRIPTION, NOT A PAGE. The page draws whatever fields it is sent; the labels, help, choices and rules
/// are here, beside the draft, where the terminal's tab takes them from.</para>
/// </summary>
internal static class WebConfigForms
{
    /// <summary>The tabs, in F8's order. Web is shown read-only: who may log in and what a browser may reach are the terminal's.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> Tabs =
    [
        ("general", "General"), ("providers", "Providers"), ("mcp", "MCP servers"), ("agents", "Agent types"),
        ("hooks", "Hooks"), ("project", "Project"), ("web", "Web"), ("json", "config.json"),
    ];

    // ---- fields ------------------------------------------------------------------------------------------------

    private static JsonObject Field(string id, string label, string kind, JsonNode? value, string? help = null) =>
        new() { ["id"] = id, ["label"] = label, ["kind"] = kind, ["value"] = value, ["help"] = help };

    private static JsonObject Radio(string id, string label, string value, params (string Value, string Label, string? Help)[] options)
    {
        var f = Field(id, label, "radio", value);
        f["options"] = new JsonArray([.. options.Select(o => (JsonNode)new JsonObject { ["value"] = o.Value, ["label"] = o.Label, ["help"] = o.Help })]);
        return f;
    }

    private static JsonObject Section(string title, params JsonObject[] fields) =>
        new() { ["title"] = title, ["fields"] = new JsonArray([.. fields]) };

    private static string? Str(JsonObject values, string id) =>
        values[id] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject values, string id) =>
        values[id] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (Str(values, id) is { } t ? t == "true" : null);

    // ---- General -----------------------------------------------------------------------------------------------

    public static JsonArray General(GeneralDraft d) =>
    [
        Section("Commands",
            Radio("detach", "When a command passes its timeout", d.DetachOnTimeout ? "true" : "false",
                ("true", "Hand it to the background", "The call returns the pid and where its output goes; the agent is told the exit code when it arrives."),
                ("false", "Kill it at the timeout", "No command outlives its call — and what it printed before the kill is lost."))),
        // THE SAME CHOICES, WORDS AND KEY AS F8's MEMORY SECTION — both front ends write GeneralDraft.MemoryEnabled.
        Section("Memory",
            Radio("memory", "Notes across sessions", d.MemoryEnabled ? "true" : "false",
                ("true", "Keep them", "The agent writes what it learns about you and the project to cxagent's folder, and reads it in later sessions. /memory lists them."),
                ("false", "Do not keep any", "No memory tool and no notes in the prompt. Notes already kept stay on disk."))),
        Section("Sandbox",
            Radio("sandboxEnabled", "The model's shell commands", d.SandboxEnabled ? "true" : "false",
                ("true", "Run inside the sandbox", "Writes only in the project, temp and package caches; credentials seen empty."),
                ("false", "Run as you", "Every command that can write asks first.")),
            Radio("sandboxNetwork", "Network for contained commands", d.SandboxNetwork ? "true" : "false",
                ("true", "On", null), ("false", "Off", null)),
            Field("writable", "Also writable", "lines", d.WritableText, "Folders, one per line, starting with / or ~."),
            Field("hidden", "Also hidden", "lines", d.HiddenText, "Folders or files seen empty, one per line."),
            Field("hiddenEnv", "Also unset", "lines", d.HiddenEnvText, "Variable names or patterns like MY_*, one per line."),
            Field("passEnv", "Keep anyway", "lines", d.PassEnvText, "Variable names a credential pattern would remove, one per line.")),
        Section("Limits",
            Radio("cap", "Turns per goal", d.Cap.ToString(),
                ("Default", "Default", "No cap for you; sub-agents stop at their built-in ceiling."),
                ("None", "No cap", "Neither you nor a sub-agent is stopped by a count."),
                ("Number", "Cap at", "For you and every sub-agent, unless its type sets its own.")),
            Field("capText", "Cap", "number", d.CapText),
            Field("compact", "Compact above (tokens)", "number", d.CompactText, "Empty: 80% of the model's window.")),
        Section("Session tools",
            Radio("toolBase", "Start from", d.Base, ("inherited", "inherited", "What the app offers"), ("all", "all", "Every tool")),
            ToolChecks(d)),
        Section("Web search",
            SecretField("tavily", "Tavily API key", d.SavedTavilyKey,
                "Empty: searches use DuckDuckGo, which needs no account but may answer with a bot check.")),
    ];

    private static JsonObject ToolChecks(GeneralDraft d)
    {
        var names = Core.Jobs.Tool.KnownNames.Order(StringComparer.Ordinal).ToList();
        var f = Field("tools", "Tools offered — unticked is taken away", "checks",
            new JsonArray([.. names.Where(n => !d.Removed.Contains(n)).Select(n => (JsonNode)n)]));
        f["options"] = new JsonArray([.. names.Select(n => (JsonNode)new JsonObject { ["value"] = n, ["label"] = n })]);
        return f;
    }

    /// <summary>
    /// A secret: never sent to the page. It shows whether one is set and its last four characters; typing replaces it,
    /// leaving it empty keeps it, and "clear" removes it — F8's rule, so a save of something else never deletes a key.
    /// </summary>
    private static JsonObject SecretField(string id, string label, string saved, string? help)
    {
        var f = Field(id, label, "secret", "", help);
        f["saved"] = SecretField(saved);
        return f;
    }

    private static string SecretField(string saved) =>
        saved.StartsWith("{env:", StringComparison.Ordinal) ? saved : CxAgent.UI.Config.SecretField.Describe(saved);

    /// <summary>Sets a General draft from the page's values; anything the page did not send stays as it was.</summary>
    public static void SetGeneral(GeneralDraft d, JsonObject v)
    {
        if (Bool(v, "detach") is { } detach) d.DetachOnTimeout = detach;
        if (Bool(v, "memory") is { } memory) d.MemoryEnabled = memory;
        if (Bool(v, "sandboxEnabled") is { } on) d.SandboxEnabled = on;
        if (Bool(v, "sandboxNetwork") is { } net) d.SandboxNetwork = net;
        if (Str(v, "writable") is { } w) d.WritableText = w;
        if (Str(v, "hidden") is { } h) d.HiddenText = h;
        if (Str(v, "hiddenEnv") is { } he) d.HiddenEnvText = he;
        if (Str(v, "passEnv") is { } pe) d.PassEnvText = pe;
        if (Str(v, "cap") is { } cap && Enum.TryParse<TurnCap>(cap, out var c)) d.Cap = c;
        if (Str(v, "capText") is { } ct) d.CapText = ct;
        if (Str(v, "compact") is { } cp) d.CompactText = cp;
        if (Str(v, "toolBase") is { } tb) d.Base = tb;
        if (v["tools"] is JsonArray offered)
        {
            var ticked = offered.Select(n => (n as JsonValue)?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            d.Removed.Clear();
            d.Removed.AddRange(Core.Jobs.Tool.KnownNames.Where(n => !ticked.Contains(n)).Order(StringComparer.Ordinal));
        }
        if (Bool(v, "tavilyClear") == true) d.ClearTavilyKey();
        else if (Str(v, "tavily") is { } key) d.TypeTavilyKey(key);
    }

    // ---- list tabs: items on one side, the chosen item's fields on the other ------------------------------------

    private static JsonObject Item(string id, string label, string? sub = null) => new() { ["id"] = id, ["label"] = label, ["sub"] = sub };

    private static JsonObject Action(string id, string label, bool danger = false) => new() { ["id"] = id, ["label"] = label, ["danger"] = danger };

    private static JsonObject Select(string id, string label, string? value, IEnumerable<(string Value, string Label)> options, string? help = null)
    {
        var f = Field(id, label, "select", value, help);
        f["options"] = new JsonArray([.. options.Select(o => (JsonNode)new JsonObject { ["value"] = o.Value, ["label"] = o.Label })]);
        return f;
    }

    private static int? Int(JsonObject v, string id) =>
        Str(v, id) is { } t ? (int.TryParse(t.Trim(), out var n) ? n : null) : null;

    // ---- Providers ---------------------------------------------------------------------------------------------

    public const string NewItem = "+new";

    /// <summary>The Providers tab: the defaults, the instances, and one instance's fields — new when <paramref name="item"/> is <see cref="NewItem"/>.</summary>
    /// <param name="settings">The config as it is now.</param>
    /// <param name="item">The instance shown, <see cref="NewItem"/>, or null for the default.</param>
    /// <param name="tabs">How many open tabs each instance has — F8's "1 tab" and its sentence under the form.</param>
    public static JsonObject Providers(ProviderSettings settings, string? item, IReadOnlyDictionary<string, int>? tabs = null)
    {
        tabs ??= new Dictionary<string, int>();
        var names = settings.Providers.Keys.Order(StringComparer.Ordinal).ToList();
        item ??= settings.DefaultProvider ?? names.FirstOrDefault();
        var form = new JsonObject
        {
            // F8'S RAIL ROWS, from ProviderRows: roles in words, the tabs on it, its model.
            ["items"] = new JsonArray([.. CxAgent.UI.Config.ProviderRows.For(settings, tabs, null)
                .Select(r => (JsonNode)Item(r.Name, r.Name, CxAgent.UI.Config.ProviderRows.Describe(r)))]),
            ["item"] = item,
            ["addLabel"] = "Add a provider",
        };
        var sections = new JsonArray
        {
            Section("Defaults",
                Select("defaultProvider", "New sessions start on", settings.DefaultProvider, names.Select(n => (n, n))),
                Select("classifier", "Auto mode's reviewer", settings.Classifier ?? "", [("", "none — auto mode is not offered"), .. names.Select(n => (n, n))],
                    "The model that reviews each write in /mode edits auto. It fails closed: a timeout or an error asks.")),
        };
        if (item == NewItem)
        {
            sections.Add(Section("New provider",
                Field("name", "Name", "text", "", "What /model and agent types call it."),
                Radio("kind", "Kind", "openai-compatible", ("openai-compatible", "OpenAI-compatible", "llama.cpp, vLLM, OpenRouter, OpenAI, any /v1 endpoint"),
                    ("anthropic", "Anthropic", null), ("ollama", "Ollama", null)),
                Field("model", "Model", "text", ""),
                Field("baseUrl", "Endpoint", "text", "", "Empty for the kind's own."),
                SecretField("apiKey", "API key", "", "Or {env:NAME} to read it from the environment.")));
            form["actions"] = new JsonArray();
        }
        else if (item is not null && settings.Providers.TryGetValue(item, out var cfg))
        {
            var d = new InstanceDraft(item, cfg);
            var onIt = CxAgent.UI.Config.ProviderRows.TabsSentence(item, tabs.GetValueOrDefault(item));
            sections.Add(Section(item,
                Field("kind", "Kind", "info", cfg.Kind, onIt),
                Field("model", "Model", "text", d.Model),
                Field("baseUrl", "Endpoint", "text", d.BaseUrl ?? "", "Empty for the kind's own."),
                Field("description", "Description", "text", d.Description ?? "", "Shown in /model and to the model choosing a type's provider."),
                SecretField("apiKey", "API key", cfg.ApiKey ?? "", "Or {env:NAME} to read it from the environment."),
                Field("contextWindow", "Context window (tokens)", "number", d.ContextWindow?.ToString() ?? "", "Empty: asked of the endpoint."),
                Field("maxConcurrentAgents", "Sub-agents at once", "number", d.MaxConcurrentAgents?.ToString() ?? "", "Empty: the default."),
                Field("thinking", "Thinking level", "text", d.Thinking ?? "", "Empty: the model's own default."),
                Radio("thinkingDisplay", "Thinking shown", d.ThinkingDisplay.ToString(), ("Summarized", "Summarized", null), ("Omitted", "Omitted", null)),
                Field("reasoning", "Reasoning dialect (OpenRouter)", "bool", d.Reasoning),
                Field("cacheControl", "Prompt caching", "bool", d.CacheControl),
                Radio("images", "Images", d.Images switch { true => "true", false => "false", _ => "" },
                    ("", "Ask the endpoint", null), ("true", "Can see images", null), ("false", "Cannot see images", null))));
            form["actions"] = new JsonArray(Action("remove", "Remove this provider", danger: true));
        }
        form["sections"] = sections;
        return form;
    }

    /// <summary>An instance's draft set from the page's values: F8's InstanceDraft, so what changed and what it moves is decided there.</summary>
    public static InstanceDraft SetInstance(string name, ProviderInstanceConfig cfg, JsonObject v)
    {
        var d = new InstanceDraft(name, cfg);
        if (Str(v, "model") is { } model) d.Model = model;
        if (Str(v, "baseUrl") is { } url) d.BaseUrl = url;
        if (Str(v, "description") is { } desc) d.Description = desc;
        if (Bool(v, "apiKeyClear") == true) d.ClearApiKey = true;
        else if (Str(v, "apiKey") is { Length: > 0 } key) d.NewApiKey = key;
        if (Str(v, "contextWindow") is { } cw) d.ContextWindow = cw.Trim().Length == 0 ? null : Int(v, "contextWindow");
        if (Str(v, "maxConcurrentAgents") is { } mc) d.MaxConcurrentAgents = mc.Trim().Length == 0 ? null : Int(v, "maxConcurrentAgents");
        if (Str(v, "thinking") is { } th) d.Thinking = th.Trim().Length == 0 ? null : th.Trim();
        if (Str(v, "thinkingDisplay") is { } td && Enum.TryParse<ThinkingDisplay>(td, out var display)) d.ThinkingDisplay = display;
        if (Bool(v, "reasoning") is { } r) d.Reasoning = r;
        if (Bool(v, "cacheControl") is { } cc) d.CacheControl = cc;
        if (Str(v, "images") is { } img) d.Images = img switch { "true" => true, "false" => false, _ => null };
        return d;
    }

    /// <summary>Why a typed number did not read as one, before the draft quietly takes it as empty.</summary>
    public static string? NumberProblem(JsonObject v, params string[] ids) =>
        ids.Select(id => (id, text: Str(v, id))).FirstOrDefault(x => x.text is { } t && t.Trim().Length > 0 && Int(v, x.id) is not > 0) is { text: not null } bad
            ? $"{bad.id} must be a whole number of 1 or more, or empty."
            : null;

    // ---- MCP servers -------------------------------------------------------------------------------------------

    /// <summary>
    /// The MCP tab: each server, and one server's command or URL, folder and timeout. Its environment and headers
    /// are named, not shown — they hold tokens — and are edited in config.json.
    /// </summary>
    public static JsonObject Mcp(ProviderSettings settings, string? item)
    {
        var names = settings.McpServers.Keys.Order(StringComparer.Ordinal).ToList();
        item ??= names.FirstOrDefault() ?? NewItem;
        var form = new JsonObject
        {
            ["items"] = new JsonArray([.. names.Select(n => (JsonNode)Item(n, n,
                (settings.McpServers[n].IsRemote ? "remote" : "local") + (settings.McpServers[n].Enabled ? "" : " · off")))]),
            ["item"] = item,
            ["addLabel"] = "Add a server",
        };
        var sections = new JsonArray();
        if (item == NewItem)
        {
            sections.Add(Section("New server",
                Field("name", "Name", "text", ""),
                Field("command", "Command", "text", "", "A local server: the command line that starts it. Leave empty for a remote one."),
                Field("url", "URL", "text", "", "A remote server's address, http or https."),
                Field("cwd", "Folder", "text", "", "Where a local server starts. Empty: the session's folder."),
                Field("timeoutMs", "Timeout (ms)", "number", "")));
            form["actions"] = new JsonArray();
        }
        else if (item is not null && settings.McpServers.TryGetValue(item, out var cfg))
        {
            var d = new McpDraft(item, cfg);
            var fields = new List<JsonObject>
            {
                cfg.IsRemote ? Field("url", "URL", "text", d.Url ?? "") : Field("command", "Command", "text", d.CommandLine),
                Field("cwd", "Folder", "text", d.Cwd ?? "", "Where it starts. Empty: the session's folder."),
                Field("timeoutMs", "Timeout (ms)", "number", d.TimeoutMs?.ToString() ?? ""),
            };
            var secrets = (cfg.Environment?.Keys ?? []).Select(k => "env " + k).Concat((cfg.Headers?.Keys ?? []).Select(k => "header " + k)).ToList();
            if (secrets.Count > 0) fields.Add(Field("secrets", "Environment and headers", "info", string.Join(", ", secrets) + " — values hidden; edit them in config.json."));
            sections.Add(Section(item + (cfg.Enabled ? "" : " (off)"), [.. fields]));
            form["actions"] = new JsonArray(cfg.Enabled ? Action("disable", "Turn off") : Action("enable", "Turn on"), Action("remove", "Remove this server", danger: true));
        }
        form["sections"] = sections;
        return form;
    }

    /// <summary>A server's draft set from the page's values: F8's McpDraft decides what is written.</summary>
    public static McpDraft SetMcp(string name, McpServerConfig cfg, bool isNew, JsonObject v)
    {
        var d = new McpDraft(name, cfg) { IsNew = isNew };
        if (Str(v, "command") is { } cmd) d.CommandLine = cmd;
        if (Str(v, "url") is { } url) d.Url = url.Trim().Length == 0 ? null : url.Trim();
        if (Str(v, "cwd") is { } cwd) d.Cwd = cwd;
        if (Str(v, "timeoutMs") is { } t) d.TimeoutMs = t.Trim().Length == 0 ? null : Int(v, "timeoutMs");
        return d;
    }

    // ---- Agent types -------------------------------------------------------------------------------------------

    /// <summary>The Agents tab: the default type, the shipped ones, the user's own, and one type's settings.</summary>
    public static JsonObject Agents(ProviderSettings settings, string? item)
    {
        var configured = settings.AgentTypes;
        var general = AgentTypeCatalog.DefaultTypeName;
        var rows = new List<(string Name, string Sub)> { (general, "default" + (configured.ContainsKey(general) ? " · changed" : "")) };
        rows.AddRange(Core.Agents.BuiltinAgentTypes.All.Select(t => (t.Name, "shipped" + (configured.ContainsKey(t.Name) ? " · changed" : ""))));
        rows.AddRange(configured.Keys.Where(k => !Core.Agents.BuiltinAgentTypes.IsBuiltin(k) && k != general).Order(StringComparer.Ordinal).Select(k => (k, "yours")));
        item ??= general;
        var form = new JsonObject
        {
            ["items"] = new JsonArray([.. rows.Select(r => (JsonNode)Item(r.Name, r.Name, r.Sub))]),
            ["item"] = item,
            ["addLabel"] = "Add a type",
        };
        var d = DraftFor(settings, item);
        var providers = settings.Providers.Keys.Order(StringComparer.Ordinal).ToList();
        var fields = new List<JsonObject>();
        if (item == NewItem) fields.Add(Field("name", "Name", "text", "", "What a spawn's type names."));
        if (d.IsBuiltin && d.Shipped is { } shipped) fields.Add(Field("shipped", "What it is", "info", shipped.Description));
        else
        {
            fields.Add(Field("description", "Description", "text", d.Description ?? "", "What the model reads when choosing a type."));
            fields.Add(Field("briefing", "Briefing", "lines", d.Briefing ?? "", "What a worker of this type is told."));
        }
        fields.Add(Radio("runsOn", "Runs on", d.Mode.ToString(),
            ("Session", "The session's model", null), ("List", "These providers, first that works", null), ("Any", "Any provider configured", null)));
        var checks = Field("providers", "Providers", "checks", new JsonArray([.. d.Providers.Select(p => (JsonNode)p)]), "Used when Runs on is these providers, in this order.");
        checks["options"] = new JsonArray([.. providers.Select(p => (JsonNode)new JsonObject { ["value"] = p, ["label"] = p })]);
        fields.Add(checks);
        fields.Add(Field("maxTurns", "Max turns", "number", d.MaxTurns?.ToString() ?? "",
            d.Shipped?.DefaultMaxTurns is { } m ? $"Empty: the shipped {m}." : "Empty: the session's cap."));
        var tools = Field("tools", "Tools — unticked is taken away", "checks",
            new JsonArray([.. Core.Jobs.Tool.KnownNames.Where(n => !d.Removed.Contains(n)).Order(StringComparer.Ordinal).Select(n => (JsonNode)n)]));
        tools["options"] = new JsonArray([.. Core.Jobs.Tool.KnownNames.Order(StringComparer.Ordinal).Select(n => (JsonNode)new JsonObject { ["value"] = n, ["label"] = n })]);
        fields.Add(Radio("toolBase", "Tools start from", d.Base, ("inherited", "inherited", "What the session offers"), ("all", "all", "Every tool")));
        fields.Add(tools);
        form["sections"] = new JsonArray(Section(item == NewItem ? "New type" : item, [.. fields]));
        form["actions"] = item == NewItem ? new JsonArray()
            : d.IsBuiltin || d.IsGeneral ? (configured.ContainsKey(item) ? new JsonArray(Action("reset", "Reset to shipped")) : new JsonArray())
            : new JsonArray(Action("remove", "Remove this type", danger: true));
        return form;
    }

    /// <summary>The draft F8 edits for <paramref name="item"/>: the default type, a shipped one, the user's own, or new.</summary>
    public static AgentDraft DraftFor(ProviderSettings settings, string item)
    {
        var configured = settings.AgentTypes;
        return item == NewItem ? AgentDraft.ForNew("")
            : item == AgentTypeCatalog.DefaultTypeName ? AgentDraft.ForGeneral(configured.TryGetValue(item, out var g) ? g : null)
            : Core.Agents.BuiltinAgentTypes.Find(item) is { } shipped ? AgentDraft.ForBuiltin(shipped, configured.TryGetValue(item, out var o) ? o : null)
            : configured.TryGetValue(item, out var own) ? AgentDraft.ForOwn(item, own) : AgentDraft.ForNew(item);
    }

    /// <summary>A type's draft set from the page's values.</summary>
    public static AgentDraft SetAgent(AgentDraft d, JsonObject v)
    {
        if (Str(v, "name") is { } name) d.Name = name.Trim();
        if (Str(v, "description") is { } desc) d.Description = desc;
        if (Str(v, "briefing") is { } b) d.Briefing = b;
        if (Str(v, "runsOn") is { } mode && Enum.TryParse<RunsOn>(mode, out var runs)) d.Choose(runs);
        if (d.Mode == RunsOn.List && v["providers"] is JsonArray chosen)
        {
            d.Providers.Clear();
            d.Providers.AddRange(chosen.Select(n => (n as JsonValue)?.GetValue<string>()).OfType<string>());
        }
        if (Str(v, "maxTurns") is { } mt) d.MaxTurns = mt.Trim().Length == 0 ? null : Int(v, "maxTurns");
        if (Str(v, "toolBase") is { } tb) d.Base = tb;
        if (v["tools"] is JsonArray offered)
        {
            var ticked = offered.Select(n => (n as JsonValue)?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            d.Removed.Clear();
            d.Removed.AddRange(Core.Jobs.Tool.KnownNames.Where(n => !ticked.Contains(n)).Order(StringComparer.Ordinal));
        }
        return d;
    }

    // ---- Hooks ---------------------------------------------------------------------------------------------------

    private static readonly string[] HookTypes = ["command", "prompt", "agent", "http", "mcp_tool"];

    /// <summary>
    /// The Hooks tab: every hook from the files F8 edits — this machine's, and the session's project's and local
    /// ones — and one hook's fields. A hook of a type cxagent does not run is listed and left as it is.
    /// </summary>
    public static JsonObject Hooks(HooksDraft draft, string? item)
    {
        var entries = draft.Entries;
        item ??= entries.Count > 0 ? HookId(entries[0], 0) : NewItem;
        var form = new JsonObject
        {
            ["items"] = new JsonArray([.. entries.Select((e, i) => (JsonNode)Item(HookId(e, i),
                e.Name ?? e.Event, $"{CxAgent.UI.Config.HooksFile.Label(e.Source)} · {e.Type}{(e.Matcher is { Length: > 0 } m ? " · " + m : "")}"))]),
            ["item"] = item,
            ["addLabel"] = "Add a hook",
        };
        if (item == NewItem)
        {
            form["sections"] = new JsonArray(Section("New hook",
                Select("source", "Saved in", "User", HooksDraft.Editable.Where(draft.CanSaveTo).Select(x => (x.ToString(), CxAgent.UI.Config.HooksFile.Label(x)))),
                Select("event", "When", Core.Hooks.HookEvents.PreToolUse, Core.Hooks.HookEvents.All.Select(e => (e, e))),
                Field("matcher", "Matching", "text", "", "A tool name or pattern for the tool events; empty matches all."),
                Field("name", "Name", "text", ""),
                Select("type", "Runs", "command", HookTypes.Select(t => (t, t))),
                Field("command", "Command", "text", "", "For a command hook."),
                Field("prompt", "Prompt", "lines", "", "For a prompt or agent hook."),
                Field("url", "URL", "text", "", "For an http hook."),
                Field("server", "MCP server", "text", "", "For an mcp_tool hook."),
                Field("tool", "Tool", "text", "", "For an mcp_tool hook."),
                Field("timeoutSeconds", "Timeout (seconds)", "number", ""),
                Field("onFailureBlock", "A failure blocks", "bool", false)));
            form["actions"] = new JsonArray();
            return form;
        }
        if (HookAt(draft, item) is not { } e)
        {
            form["sections"] = new JsonArray();
            return form;
        }
        var fields = new List<JsonObject>
        {
            Field("source", "Saved in", "info", CxAgent.UI.Config.HooksFile.Label(e.Source)),
            Select("event", "When", e.Event, Core.Hooks.HookEvents.All.Select(x => (x, x))),
            Field("matcher", "Matching", "text", e.Matcher ?? "", "A tool name or pattern for the tool events; empty matches all."),
            Field("name", "Name", "text", e.Name ?? ""),
            Select("type", "Runs", e.Type, HookTypes.Select(t => (t, t))),
        };
        switch (e.Type)
        {
            case "command":
                fields.Add(Field("command", "Command", "text", e.Command ?? ""));
                fields.Add(Field("shell", "Shell", "text", e.Shell ?? "", "Empty: the default shell."));
                break;
            case "prompt" or "agent":
                fields.Add(Field("prompt", "Prompt", "lines", e.Prompt ?? ""));
                fields.Add(Field("model", "Model", "text", e.Model ?? "", "Empty: the hooks' default model."));
                fields.Add(Field("continueOnBlock", "Carry on when it blocks", "bool", e.ContinueOnBlock));
                break;
            case "http":
                fields.Add(Field("url", "URL", "text", e.Url ?? ""));
                break;
            case "mcp_tool":
                fields.Add(Field("server", "MCP server", "text", e.Server ?? ""));
                fields.Add(Field("tool", "Tool", "text", e.Tool ?? ""));
                break;
        }
        fields.Add(Field("timeoutSeconds", "Timeout (seconds)", "number", e.TimeoutSeconds?.ToString() ?? ""));
        fields.Add(Field("onFailureBlock", "A failure blocks", "bool", e.OnFailureBlock));
        form["sections"] = new JsonArray(Section(e.Name ?? e.Event, [.. fields]));
        form["readOnly"] = !e.Editable;
        form["actions"] = new JsonArray(Action("remove", "Remove this hook", danger: true));
        return form;
    }

    /// <summary>
    /// A hook's id for the page: its place, and what it is. A PLACE ALONE NAMES WHATEVER IS THERE NOW — a hook added in
    /// the terminal since the page read the list would take the place, and an edit or a removal would land on it.
    /// </summary>
    public static string HookId(HookEntry e, int index)
    {
        var what = string.Join("\u0001", e.Source, e.Event, e.Matcher, e.Type, e.Command, e.Prompt, e.Url, e.Server, e.Tool, e.Name);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(what)))[..12];
        return $"{index}:{hash}";
    }

    /// <summary>The hook <paramref name="id"/> names, when it is still that hook; null when the list has changed under it.</summary>
    public static HookEntry? HookAt(HooksDraft draft, string? id)
    {
        if (id is null || id.Split(':', 2) is not [var at, _] || !int.TryParse(at, out var index)
            || index < 0 || index >= draft.Entries.Count) return null;
        var e = draft.Entries[index];
        return HookId(e, index) == id ? e : null;
    }

    /// <summary>A hook entry set from the page's values.</summary>
    public static void SetHook(HookEntry e, JsonObject v)
    {
        string? Blank(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        if (Str(v, "event") is { } ev) e.Event = ev;
        if (Str(v, "matcher") is { } m) e.Matcher = Blank(m);
        if (Str(v, "name") is { } n) e.Name = Blank(n);
        if (Str(v, "type") is { } t && HookTypes.Contains(t)) e.Type = t;
        if (Str(v, "command") is { } c) e.Command = Blank(c);
        if (Str(v, "shell") is { } sh) e.Shell = Blank(sh);
        if (Str(v, "prompt") is { } p) e.Prompt = Blank(p);
        if (Str(v, "model") is { } mo) e.Model = Blank(mo);
        if (Bool(v, "continueOnBlock") is { } cb) e.ContinueOnBlock = cb;
        if (Str(v, "url") is { } u) e.Url = Blank(u);
        if (Str(v, "server") is { } se) e.Server = Blank(se);
        if (Str(v, "tool") is { } to) e.Tool = Blank(to);
        if (Str(v, "timeoutSeconds") is { } ts) e.TimeoutSeconds = ts.Trim().Length == 0 ? null : Int(v, "timeoutSeconds");
        if (Bool(v, "onFailureBlock") is { } fb) e.OnFailureBlock = fb;
    }

    // ---- Web, read-only ----------------------------------------------------------------------------------------

    /// <summary>
    /// The Web tab, shown and never edited from a browser: where it listens, who must log in, the users, the folders
    /// and the switches — what a login must not be able to change about itself.
    /// </summary>
    // ---- Project ------------------------------------------------------------------------------------------------

    /// <summary>
    /// F8's Project tab: the repository the page's session works in, its trust, and what it brings — from the same
    /// <see cref="Core.Sessions.ProjectSummary"/>, so the two front ends list the same things the same way. Read-only
    /// but for trust, as there.
    /// </summary>
    public static JsonObject Project(Core.Sessions.ProjectSummary s, string workingDirectory)
    {
        if (s.RepoRoot is not { } root)
            return new()
            {
                ["readOnly"] = true,
                ["sections"] = new JsonArray(Section("No repository here",
                    Field("folder", "Folder", "info", workingDirectory, "Not inside a git repository, so nothing is read from it as a project."))),
            };

        var trusted = s.Trust == Core.Permissions.TrustState.Trusted;
        var brings = s.Items.Select(i => Field(i.Kind, i.Kind, "info",
            (i.Count == 0 ? "none" : i.Count + (i.State is { } st ? " · " + st : ""))
            + (i.Names.Count > 0 ? " — " + string.Join(", ", i.Names.Take(8)) + (i.Names.Count > 8 ? ", …" : "") : ""),
            i.Folder.Length > 0 ? i.Folder : null)).ToArray();
        return new()
        {
            ["sections"] = new JsonArray(
                Section($"{Path.GetFileName(root)} — {root}",
                    Radio("trust", "Trust", trusted ? "true" : "false",
                        ("true", "Trusted", "Reads and writes inside it are not asked about, and its hooks run."),
                        ("false", "Not trusted", "Every file operation asks, and its hooks do not run."))),
                Section("What it brings", brings)),
        };
    }

    /// <summary>The trust the Project form asks for, or null when it did not say.</summary>
    public static bool? ProjectTrust(JsonObject values) => Bool(values, "trust");

    public static JsonObject Web(Core.Web.WebSettings w) => new()
    {
        ["readOnly"] = true,
        ["sections"] = new JsonArray(
            Section("Changed in the terminal only — F8 › Web",
                Field("port", "Port", "info", w.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Field("bind", "Listen on", "info", w.Bind),
                Field("tls", "Transport", "info", w.SelfSigned ? "HTTPS, self-signed" : "Plain HTTP"),
                Field("login", "Who must log in", "info", w.Login switch
                {
                    Core.Web.LoginMode.Always => "Everyone", Core.Web.LoginMode.Off => "No one", _ => "Anyone not on this machine",
                }),
                Field("users", "Users", "info", w.Users.Count == 0 ? "none" : string.Join(", ", w.Users.Keys.Order(StringComparer.Ordinal))),
                Field("roots", "Open sessions under", "info", w.Roots.Count == 0 ? "the folder the server started in" : string.Join(", ", w.Roots)),
                Field("idle", "Unload after (minutes)", "info", w.IdleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            Section("Browser access",
                Field("plugins", "Plugins", "info", w.Allow.Plugins ? "on" : "off"),
                Field("openAnywhere", "Open files anywhere", "info", w.Allow.OpenAnywhere ? "on" : "off"),
                Field("shell", "Shell", "info", w.Allow.Shell ? "on" : "off"),
                Field("config", "Configuration", "info", w.Allow.Config ? "on" : "off"))),
    };
}
