using System.Text.Json.Nodes;

namespace CxAgent.Core.Config;

/// <summary>The keys the loader reads for one MCP server, as an edit — null leaves a key, <see cref="Clear"/> removes it.</summary>
public sealed record McpServerFields
{
    public IReadOnlyList<string>? Command { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Cwd { get; init; }
    public bool? Enabled { get; init; }
    public int? TimeoutMs { get; init; }
    public IReadOnlySet<string> Clear { get; init; } = new HashSet<string>();

    internal void ApplyTo(JsonObject server)
    {
        // AN ARRAY: the loader reads `command` only as one (ProviderConfigLoader), and a string is
        // skipped with a warning — so a command written any other way is a server that silently is not.
        if (Command is { } command) server["command"] = new JsonArray(command.Select(a => (JsonNode?)a).ToArray());
        if (Url is { } url) server["url"] = url;
        if (Headers is { } headers) server["headers"] = Map(headers);
        if (Env is { } env) server["env"] = Map(env);
        if (Cwd is { } cwd) server["cwd"] = cwd;
        if (Enabled is { } enabled) server["enabled"] = enabled;
        if (TimeoutMs is { } timeout) server["timeoutMs"] = timeout;
        foreach (var key in Clear) server.Remove(key);
    }

    private static JsonObject Map(IReadOnlyDictionary<string, string> values) =>
        new(values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
}
