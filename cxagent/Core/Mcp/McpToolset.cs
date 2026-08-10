using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Core.Mcp;

/// <summary>
/// Turns connected MCP servers into tools the model can call, indistinguishable from the built-ins at
/// the point of use.
///
/// <para>The conversion itself is nearly free: <see cref="ToolDefinition"/> — what we already hand the
/// model — has the same three fields as <see cref="McpToolDef"/>. What this type exists for is
/// NAMING: composing a name that cannot collide, and dropping the ones that would anyway.</para>
///
/// <para>WHY THIS IS NOT PART OF <see cref="WorkerToolset"/>. Both of that type's seams are driven by
/// a static table keyed on the <see cref="WorkerTool"/> enum, and an MCP tool has no enum value —
/// so it cannot join the table, and <c>InvokeAsync</c>'s lookup can never match one. Composition
/// happens at the two call sites in <c>Agent</c> instead, leaving <see cref="WorkerToolset"/>
/// closed.</para>
/// </summary>
public sealed class McpToolset
{
    private readonly IReadOnlyList<IMcpServer> _servers;

    /// <summary>Composed name → the server and the tool's own name on it.</summary>
    private readonly Dictionary<string, (IMcpServer Server, string Tool)> _byName = new(StringComparer.Ordinal);

    private readonly List<string> _warnings = [];

    /// <summary>Tools dropped for colliding, and with what. Shown by <c>/mcp</c>: a tool that silently
    /// never appears is indistinguishable from a broken server.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public McpToolset(IReadOnlyList<IMcpServer> servers)
    {
        _servers = servers;

        // Built-in names are claimed FIRST, so a server can never take one. Order matters: whoever
        // is in the map when a duplicate arrives keeps the name.
        var taken = new HashSet<string>(WorkerToolset.NamesFor(Enum.GetValues<WorkerTool>()),
            StringComparer.Ordinal);

        foreach (var server in servers)
            foreach (var tool in server.Tools)
            {
                var name = Compose(server.Name, tool.Name);

                // TWO DIFFERENT COLLISIONS, ONE RULE. Either this shadows a built-in, or a previous
                // server already sanitised to the same thing ("my server" and "my_server" both give
                // "my_server" — a clash that exists only AFTER sanitisation, so no config-level
                // uniqueness check could have caught it).
                if (!taken.Add(name))
                {
                    _warnings.Add($"'{server.Name}' offers '{tool.Name}' as '{name}', which is already taken; skipped.");
                    continue;
                }

                _byName[name] = (server, tool.Name);
            }
    }

    /// <summary>
    /// Every MCP tool, as the model sees it.
    ///
    /// <para>The schema is passed through WHOLE rather than rebuilt from its property names, because
    /// per-parameter descriptions live inside it and are half of what tells the model how to call the
    /// thing. The description is carried for the same reason: it is the only prose about what a tool
    /// is FOR, and a schema cannot say it.</para>
    /// </summary>
    public IReadOnlyList<ToolDefinition> Definitions()
    {
        var list = new List<ToolDefinition>();
        foreach (var (name, (server, toolName)) in _byName)
        {
            var def = server.Tools.FirstOrDefault(t => t.Name == toolName);
            if (def is null) continue;
            list.Add(new ToolDefinition(name, def.Description, def.InputSchema));
        }
        return list;
    }

    /// <summary>
    /// Runs an MCP tool, or returns NULL if no server owns the name.
    ///
    /// <para>Null rather than an error string, so the caller falls through to
    /// <see cref="WorkerToolset.InvokeAsync"/> and its "no such tool" text stays the single message
    /// for a name nobody owns. Two sources each producing their own version of that message is how a
    /// model ends up being told a tool does not exist by one and nothing by the other.</para>
    /// </summary>
    public async Task<string?> TryInvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!_byName.TryGetValue(call.Name, out var found)) return null;

        // THE SERVER'S OWN NAME, not the composed one. The prefix is ours, and a server asked to run
        // "files_read" would rightly say it has no such tool.
        var result = await found.Server.CallToolAsync(found.Tool, call.Arguments, ct);

        // The same cap as every built-in tool result, or one call fills the context window. A second
        // copy of the elision beats widening WorkerToolset's private helper for one caller.
        return Truncate(result, WorkerToolset.MaxToolResultChars);
    }

    /// <summary>
    /// Each server's own usage prose, for the system prompt.
    ///
    /// <para>Servers that sent none are absent rather than present-and-empty, so the caller can emit
    /// nothing at all when nobody had anything to say.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> InstructionsByServer()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in _servers)
            if (!string.IsNullOrWhiteSpace(server.Instructions))
                map[server.Name] = server.Instructions!.Trim();
        return map;
    }

    /// <summary>Every tool name we offer, for the "no such tool" message to list alongside the
    /// built-ins.</summary>
    public IEnumerable<string> Names() => _byName.Keys;

    /// <summary>
    /// <c>sanitize(server) + "_" + sanitize(tool)</c>, matching opencode.
    ///
    /// <para>Providers reject tool names outside <c>[a-zA-Z0-9_-]</c>, so a server called "my server"
    /// must not be able to produce one. Every other character becomes an underscore rather than being
    /// dropped: dropping would silently merge "a-b" and "ab" into one name.</para>
    /// </summary>
    private static string Compose(string server, string tool) =>
        $"{Sanitize(server)}_{Sanitize(tool)}";

    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>The same elision as <see cref="WorkerToolset"/>'s, marker counted inside the cap so
    /// the one number it guarantees is actually guaranteed.</summary>
    private static string Truncate(string text, int cap)
    {
        if (cap <= 0 || text.Length <= cap) return text;

        var marker = $"\n[... {text.Length - cap:N0} bytes elided ...]\n";
        var keep = cap - marker.Length;
        if (keep <= 0) return text[..cap];

        var head = keep / 2;
        return text[..head] + marker + text[^(keep - head)..];
    }
}
