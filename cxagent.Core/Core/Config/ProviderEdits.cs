using System.Text.Json.Nodes;

namespace CxAgent.Core.Config;

/// <summary>A name that is missing, or already taken. Its message is shown to the user as it is.</summary>
public sealed class ConfigEditException(string message) : Exception(message);

/// <summary>
/// Changes to provider instances, made on the config DOCUMENT.
///
/// <para>THE DOCUMENT, NOT A MODEL OF IT. Rebuilding a block from a parsed record writes only what the
/// record knows, and every key it does not — a field from a newer version, a hand-added note — is
/// gone after the first save. Editing the JSON in place keeps them by construction.</para>
///
/// <para>EVERY REFERENCE MOVES WITH THE NAME. An instance is named by <c>defaultProvider</c>,
/// <c>classifier</c>, <c>llmAgent.routing.*.provider</c>, <c>llmAgent.allowedProviders</c> and
/// <c>agents.*.provider</c>; the loader refuses a dangling one in all but the last, so a rename or
/// removal that missed one would write a file startup will not load.</para>
/// </summary>
public static class ProviderEdits
{
    public static void Add(JsonObject root, string name, InstanceFields fields)
    {
        var providers = Providers(root);
        if (providers.ContainsKey(name)) throw new ConfigEditException($"'{name}' already exists.");
        var instance = new JsonObject();
        fields.ApplyTo(instance);
        providers[name] = instance;
        if (root["defaultProvider"] is null) root["defaultProvider"] = name;
    }

    public static void Update(JsonObject root, string name, InstanceFields fields) =>
        fields.ApplyTo(Instance(root, name));

    public static void Rename(JsonObject root, string from, string to)
    {
        if (from == to) return;
        var providers = Providers(root);
        var instance = Instance(root, from);
        if (providers.ContainsKey(to)) throw new ConfigEditException($"'{to}' already exists.");
        providers.Remove(from);
        providers[to] = instance;
        Rewrite(root, from, to);
    }

    public static void Remove(JsonObject root, string name)
    {
        Instance(root, name);
        Providers(root).Remove(name);
        Rewrite(root, name, null);

        // THE DEFAULT PASSES ON BY A STATED RULE — the first remaining name, ordinally — so which
        // instance inherits it is predictable rather than whatever the dictionary enumerates first.
        if (root["defaultProvider"] is null
            && Providers(root).Select(kv => kv.Key).Order(StringComparer.Ordinal).FirstOrDefault() is { } next)
            root["defaultProvider"] = next;
    }

    public static void SetDefault(JsonObject root, string name)
    {
        Instance(root, name);
        root["defaultProvider"] = name;
    }

    /// <summary>Names the auto-mode reviewer, or clears it — auto mode is then not offered.</summary>
    public static void SetReviewer(JsonObject root, string? name)
    {
        if (name is null) { root.Remove("classifier"); return; }
        Instance(root, name);
        root["classifier"] = name;
    }

    /// <summary>Points every reference to <paramref name="from"/> at <paramref name="to"/>, or drops it when null.</summary>
    private static void Rewrite(JsonObject root, string from, string? to)
    {
        foreach (var key in new[] { "defaultProvider", "classifier" })
            if ((string?)root[key] == from)
                if (to is null) root.Remove(key); else root[key] = to;

        if (root["llmAgent"] is JsonObject agent)
        {
            if (agent["routing"] is JsonObject routing)
                foreach (var (role, target) in routing.ToList())
                    if (target is JsonObject t && (string?)t["provider"] == from)
                        if (to is null) routing.Remove(role); else t["provider"] = to;

            if (agent["allowedProviders"] is JsonArray allowed)
                for (var i = allowed.Count - 1; i >= 0; i--)
                    if ((string?)allowed[i] == from)
                        if (to is null) allowed.RemoveAt(i); else allowed[i] = to;
        }

        // A TYPE WITHOUT A PROVIDER RUNS ON THE SESSION'S, which is what removing its binding means.
        if (root["agents"] is JsonObject agents)
            foreach (var (_, type) in agents)
                if (type is JsonObject t && (string?)t["provider"] == from)
                    if (to is null) t.Remove("provider"); else t["provider"] = to;
    }

    private static JsonObject Providers(JsonObject root)
    {
        if (root["providers"] is JsonObject p) return p;
        var created = new JsonObject();
        root["providers"] = created;
        return created;
    }

    private static JsonObject Instance(JsonObject root, string name) =>
        Providers(root)[name] as JsonObject ?? throw new ConfigEditException($"No provider called '{name}'.");
}
