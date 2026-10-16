using System.Text.Json.Nodes;

namespace CxAgent.Core.Config;

/// <summary>
/// Changes to MCP servers on the config document. NOTHING ELSE NAMES A SERVER, so a rename or a
/// removal touches the <c>mcp</c> block alone.
/// </summary>
public static class McpEdits
{
    public static void Add(JsonObject root, string name, McpServerFields fields)
    {
        var servers = Servers(root);
        if (servers.ContainsKey(name)) throw new ConfigEditException($"'{name}' already exists.");
        var server = new JsonObject();
        fields.ApplyTo(server);
        servers[name] = server;
    }

    public static void Update(JsonObject root, string name, McpServerFields fields) => fields.ApplyTo(Server(root, name));

    public static void Rename(JsonObject root, string from, string to)
    {
        if (from == to) return;
        var server = Server(root, from);
        var servers = Servers(root);
        if (servers.ContainsKey(to)) throw new ConfigEditException($"'{to}' already exists.");
        servers.Remove(from);
        servers[to] = server;
    }

    public static void Remove(JsonObject root, string name)
    {
        Server(root, name);
        Servers(root).Remove(name);
    }

    /// <summary>"Not now" without retyping a command later — the command stays exactly as it was.</summary>
    public static void SetEnabled(JsonObject root, string name, bool enabled) => Server(root, name)["enabled"] = enabled;

    private static JsonObject Servers(JsonObject root)
    {
        if (root["mcp"] is JsonObject m) return m;
        var created = new JsonObject();
        root["mcp"] = created;
        return created;
    }

    private static JsonObject Server(JsonObject root, string name) =>
        Servers(root)[name] as JsonObject ?? throw new ConfigEditException($"No MCP server called '{name}'.");
}
