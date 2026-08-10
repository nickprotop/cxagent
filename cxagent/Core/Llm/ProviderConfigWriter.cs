using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Llm;

/// <summary>
/// Writes &lt;ConfigDir&gt;/config.json. The counterpart to ProviderConfigLoader.
///
/// Three properties matter and each is tested:
///  * ATOMIC — serialize to a .tmp then File.Move(overwrite:true). A crash mid-write leaves the old
///    config intact rather than a truncated file the loader would reject at next startup.
///  * 0600 — the file holds API keys (spec §Config &amp; data location). Unix only; no-op elsewhere.
///  * PRESERVING — only 'providers', 'defaultProvider', 'llmAgent', 'orchestrator' and 'mcp' are cxagent's
///    to own here. Every other top-level key (jobs/ui/plugins, plus anything a future version adds)
///    is read from the existing file and written back untouched, so the wizard never destroys
///    hand-edits. Within an owned block, only the sub-keys the loader reads are ours — anything else
///    (a future knob, a hand-added field) is merged in from the existing object, same as 'roles'.
/// </summary>
public static class ProviderConfigWriter
{
    public static void Write(AppPaths paths, ProviderSettings settings)
    {
        Directory.CreateDirectory(paths.ConfigDir);
        var path = Path.Combine(paths.ConfigDir, "config.json");

        // Start from the existing document so unknown keys survive.
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException) { root = new JsonObject(); }   // unparseable → start clean

        var providers = new JsonObject();
        foreach (var (name, cfg) in settings.Providers)
        {
            var o = new JsonObject
            {
                ["kind"] = cfg.Kind,
                ["model"] = cfg.Model,
            };
            if (!string.IsNullOrEmpty(cfg.ApiKey)) o["apiKey"] = cfg.ApiKey;
            if (!string.IsNullOrEmpty(cfg.BaseUrl)) o["baseUrl"] = cfg.BaseUrl;
            if (cfg.ContextWindow is { } cwv) o["contextWindow"] = cwv;
            if (cfg.ExtraHeaders is { Count: > 0 })
            {
                var h = new JsonObject();
                foreach (var (hk, hv) in cfg.ExtraHeaders) h[hk] = hv;
                o["extraHeaders"] = h;
            }
            providers[name] = o;
        }

        root["providers"] = providers;
        root["defaultProvider"] = settings.DefaultProvider is null ? null : JsonValue.Create(settings.DefaultProvider);

        // The llmAgent/roles block used to be written here. Roles are gone.

        {
            var orch = root["orchestrator"]?.AsObject() ?? new JsonObject();
            var o = settings.Orchestrator;
            // Required fields with real (non-null) defaults are always written — the loader treats
            // absent and default identically for these, so this is safe and makes the file
            // self-documenting (ProviderConfig.cs:293-314).
            orch["maxWorkerTurns"] = o.MaxWorkerTurns;
            // MaxTokensPerCall/GoalTokenBudget/ContextCompressThreshold are null-means-unconfigured
            // (ProviderConfig.cs:25-27,46-58) — omit the key entirely rather than writing null, so
            // "nobody said" survives a round-trip instead of collapsing into an explicit value.
            if (o.MaxTokensPerCall is { } mtpc) orch["maxTokensPerCall"] = mtpc; else orch.Remove("maxTokensPerCall");
            if (o.GoalTokenBudget is { } gtb) orch["goalTokenBudget"] = gtb; else orch.Remove("goalTokenBudget");
            if (o.ContextCompressThreshold is { } cct) orch["contextCompressThreshold"] = cct; else orch.Remove("contextCompressThreshold");
            root["orchestrator"] = orch;
        }

        {
            // THE BLOCK IS WRITTEN WHOLESALE, not merged key-by-key, because a server the user
            // deleted in Settings has to actually disappear. Merging into the existing object would
            // resurrect it on every save.
            var existing = root["mcp"]?.AsObject();
            var mcp = new JsonObject();
            foreach (var (name, cfg) in settings.McpServers)
            {
                // Per-server keys we do not model (a future knob, a hand-added "env") are carried
                // over from the old entry, same as the orchestrator block does. Owning the block is
                // not licence to discard what is inside it.
                var o = existing?[name] is JsonObject prev
                    ? prev.DeepClone().AsObject()
                    : new JsonObject();

                var command = new JsonArray();
                foreach (var part in cfg.Command) command.Add(part);
                o["command"] = command;
                o["enabled"] = cfg.Enabled;
                // Null-means-use-the-default, so omit rather than write a number nobody chose.
                if (cfg.TimeoutMs is { } ms) o["timeoutMs"] = ms; else o.Remove("timeoutMs");

                mcp[name] = o;
            }

            if (mcp.Count > 0) root["mcp"] = mcp; else root.Remove("mcp");
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        ChmodOwnerOnly(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    private static void ChmodOwnerOnly(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort: a filesystem that rejects chmod must not break setup */ }
    }
}
