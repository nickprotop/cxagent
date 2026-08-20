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
    /// <summary>
    /// The connected servers, REPLACEABLE at runtime.
    ///
    /// <para>Mutable because the agent reads <see cref="Definitions"/> and
    /// <see cref="InstructionsByServer"/> fresh on every prompt, so swapping the list here is all it
    /// takes for a server added or removed mid-session to reach the model on the next turn — no
    /// restart, and no plumbing to push an update through.</para>
    /// </summary>
    private IReadOnlyList<IMcpServer> _servers = [];

    /// <summary>
    /// The permission gate every call goes through.
    ///
    /// <para>NOT VIA <c>PermissionGatedPlugin</c>, which is the obvious route and silently allows:
    /// it asks <c>PermissionPolicy.RequestsFor(TypeName, …)</c>, whose <c>default:</c> arm returns an
    /// EMPTY list for any plugin type it does not know, and then loops over zero requests and runs
    /// the inner code with no prompt at all. Third-party code would execute ungated while LOOKING
    /// gated — the worst of both. So the request is built here and handed to the gate directly.</para>
    /// </summary>
    private readonly Permissions.IPermissionGate _gate;

    /// <summary>Composed name → the server and the tool's own name on it.</summary>
    private Dictionary<string, (IMcpServer Server, string Tool)> _byName = new(StringComparer.Ordinal);

    private List<string> _warnings = [];

    /// <summary>Tools dropped for colliding, and with what. Shown by <c>/mcp</c>: a tool that silently
    /// never appears is indistinguishable from a broken server.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <param name="gate">
    /// Omitted means DENY, never allow.
    ///
    /// <para>An optional gate is exactly the shape that becomes a silent bypass — a call site that
    /// forgets it would run third-party code unprompted. Defaulting to the refusing gate makes the
    /// omission fail loudly and safely instead, the same reasoning as the headless default gate that
    /// can never say yes.</para>
    /// </param>
    /// <param name="servers">The connected servers whose tools this offers.</param>
    public McpToolset(IReadOnlyList<IMcpServer> servers, Permissions.IPermissionGate? gate = null)
    {
        _gate = gate ?? Permissions.PermissionGate.DenyAll;
        Replace(servers);
    }

    /// <summary>
    /// Swaps in a new set of servers and recomputes every composed name.
    ///
    /// <para>A FULL REBUILD, not an incremental add. Names collide across servers and against the
    /// built-ins, and which tool wins depends on the order everything was claimed in — patching one
    /// server into an existing map would make the winner depend on the order servers happened to be
    /// added, so the same config could produce different tools depending on history.</para>
    /// </summary>
    public void Replace(IReadOnlyList<IMcpServer> servers)
    {
        _servers = servers;
        var byName = new Dictionary<string, (IMcpServer Server, string Tool)>(StringComparer.Ordinal);
        var warnings = new List<string>();

        // Built-in names are claimed FIRST, so a server can never take one. Order matters: whoever
        // is in the map when a duplicate arrives keeps the name.
        //
        // THE FULL ENUM, NEVER A TOOL SELECTION, and this looks like an oversight until you see why:
        // if a session withheld `write_file`, narrowing this set would let a server claim that name
        // and backfill the tool the user just removed. The set answers "which names are spoken for",
        // not "which tools does this agent have" — and every built-in name is spoken for whether or
        // not this agent was offered it.
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
                    warnings.Add($"'{server.Name}' offers '{tool.Name}' as '{name}', which is already taken; skipped.");
                    continue;
                }

                byName[name] = (server, tool.Name);
            }

        // Assigned at the END, so a caller reading tools concurrently sees either the old set or the
        // new one — never a half-built map with some servers' tools missing.
        _byName = byName;
        _warnings = warnings;
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
    /// <param name="requester">
    /// WHO IS ASKING — null for the session's own agent, a short label for a sub-agent.
    ///
    /// <para>The attribution work reached every other request-construction site and missed this one:
    /// <see cref="Permissions.PermissionGatedPlugin"/> copies it from its <c>JobContext</c>, and MCP
    /// had no equivalent because it takes no context at all. With one child at a time that is merely
    /// unhelpful. With two, a prompt saying "may I run this?" has no answer to "which of them wants
    /// it?" — and the user is being asked to approve third-party code on their machine.</para>
    /// </param>
    /// <param name="call">The call the model issued; null is returned when no server owns the name.</param>
    /// <param name="ct">Cancels the call.</param>
    public async Task<string?> TryInvokeAsync(ToolCall call, CancellationToken ct,
        string? requester = null)
    {
        if (!_byName.TryGetValue(call.Name, out var found)) return null;

        // GATED BEFORE THE SERVER IS TOUCHED. An MCP server is third-party code running on the user's
        // machine with the user's credentials; the built-in file, shell and http plugins all prompt,
        // and this gets no weaker treatment.
        //
        // The whole call is shown — server, tool AND arguments — because the user is approving a call
        // into code we cannot inspect, and showing less than all of it is not honest. The rule,
        // though, generalises only to the tool: see PermissionPolicy.RuleSubject.
        var request = new Permissions.PermissionRequest(
            Permissions.PermissionKind.Mcp,
            $"{found.Server.Name} → {found.Tool} {call.Arguments}",
            $"mcp:{call.Name}")
        {
            Requester = requester,
        };

        if (!await _gate.RequestAsync(request, ct))
            // The same shape as a denied built-in: a refusal the MODEL reads, not an exception, and
            // one that tells it not to try again. Retrying a denial burns turns against the cap and
            // re-prompts the user for something they already said no to.
            return $"permission denied by the user: {request.Display}. "
                 + "Do not retry this operation or plan it again unless the user explicitly asks.";

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
