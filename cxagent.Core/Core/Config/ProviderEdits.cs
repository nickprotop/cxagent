using System.Text.Json.Nodes;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Config;

/// <summary>A name that is missing, or already taken. Its message is shown to the user as it is.</summary>
internal sealed class ConfigEditException(string message) : Exception(message);

/// <summary>
/// Changes to provider instances, made on the config DOCUMENT.
///
/// <para>THE DOCUMENT, NOT A MODEL OF IT. Rebuilding a block from a parsed record writes only what the
/// record knows, and every key it does not — a field from a newer version, a hand-added note — is
/// gone after the first save. Editing the JSON in place keeps them by construction.</para>
///
/// <para>EVERY REFERENCE MOVES WITH THE NAME, IN EVERY FORM. An instance is named by
/// <c>defaultProvider</c>, <c>classifier</c>, <c>llmAgent.routing.*.provider</c>,
/// <c>llmAgent.allowedProviders</c> and <c>agents.*.provider</c> — the last a string or a list, each
/// <c>name</c> or <c>name:model</c>. The loader refuses a dangling one in all but the last, and a
/// dangling one there disables the type, so a rename or removal that missed one would write a file
/// startup will not load, or a type that quietly stops working.</para>
/// </summary>
public static class ProviderEdits
{
    public static void Add(JsonObject root, string name, InstanceFields fields)
    {
        var providers = Providers(root);
        if (providers.ContainsKey(name)) throw new ConfigEditException($"'{name}' already exists.");
        RefuseColon(name);
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
        RefuseColon(to);
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

        // A TYPE'S `provider` IS A STRING OR A LIST, each `name` or `name:model` — a reference moves in
        // every form, or a rename leaves a type naming nothing, which disables it.
        if (root["agents"] is JsonObject agents)
            foreach (var (_, type) in agents)
                if (type is JsonObject t) RewriteTypeProvider(t, from, to);
    }

    /// <summary>A NEW NAME HOLDS NO COLON: in an agent type's provider list a colon separates a name from
    /// its model, so a name with one reads as something else. Names already in a file keep working — the
    /// loader takes a whole name first — but none is made here.</summary>
    private static void RefuseColon(string name)
    {
        if (name.Contains(':'))
            throw new ConfigEditException($"a provider name cannot contain ':' — in an agent type's provider list it separates a name from its model.");
    }

    private static void RewriteTypeProvider(JsonObject type, string from, string? to)
    {
        static string? Moved(string written, string from, string? to)
        {
            if (written.Trim() == from) return to;
            if (AgentTypeConfig.NameOf(written) != from) return written;
            var colon = written.IndexOf(':');
            return to is null ? null : colon < 0 ? to : to + written[colon..];
        }

        switch (type["provider"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                // A TYPE WITHOUT A PROVIDER RUNS ON THE SESSION'S, which is what removing its only one means.
                if (Moved(s, from, to) is { } next) type["provider"] = next; else type.Remove("provider");
                break;
            case JsonArray list:
                for (var i = list.Count - 1; i >= 0; i--)
                    if (list[i] is JsonValue e && e.TryGetValue<string>(out var w))
                    {
                        if (Moved(w, from, to) is { } moved) list[i] = moved; else list.RemoveAt(i);
                    }
                // AN EMPTY LIST IS A SHAPE ERROR, which would disable the type; no list is the session's.
                if (list.Count == 0) type.Remove("provider");
                break;
        }
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
