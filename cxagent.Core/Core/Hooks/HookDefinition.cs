using System.Text.Json;

namespace CxAgent.Core.Hooks;

/// <summary>
/// One configured hook: the event it listens to, the tool names it matches, and what it runs.
///
/// <para>KEPT EVEN WHEN IT CANNOT RUN. A hook on an event cxagent does not fire, or of a type it does not
/// run, is still a hook the user wrote: F8 shows it, and a save writes it back exactly as it was
/// (<see cref="Raw"/>). <see cref="Runs"/> is the one place that decides whether it fires.</para>
/// </summary>
/// <param name="Event">The contract's event name, as written.</param>
/// <param name="Matcher">The tool-name pattern, or null for every tool (and for events with no tool).</param>
/// <param name="Type">The hook type as written: <c>command</c>, <c>prompt</c>, <c>agent</c>, <c>http</c>, <c>mcp_tool</c>, or anything else.</param>
/// <param name="Source">Which file, or the library, it came from.</param>
public sealed record HookDefinition(string Event, string? Matcher, string Type, HookSource Source)
{
    public string? Name { get; init; }
    public string? Command { get; init; }

    /// <summary><c>bash</c>, <c>powershell</c>, or null for the platform's default.</summary>
    public string? Shell { get; init; }

    public int? TimeoutSeconds { get; init; }

    /// <summary>Whether a failure of this hook blocks what it guards (<c>onFailure: "block"</c>) rather than letting it through.</summary>
    public bool OnFailureBlock { get; init; }

    /// <summary>The question for a <c>prompt</c> or <c>agent</c> hook; <c>$ARGUMENTS</c> stands for the event's JSON.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// A prompt hook's <c>continueOnBlock</c>: on a block, feed the reason back and carry on rather than end the
    /// turn. Agent hooks always carry on.
    /// </summary>
    public bool ContinueOnBlock { get; init; }

    /// <summary>The <c>provider:model</c> entry a <c>prompt</c> or <c>agent</c> hook asks.</summary>
    public string? Model { get; init; }

    /// <summary>Where an <c>http</c> hook posts the event.</summary>
    public string? Url { get; init; }

    /// <summary>An <c>http</c> hook's headers; <c>$NAME</c> in a value expands only for names in <see cref="AllowedEnvVars"/>.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The environment variables an <c>http</c> hook's headers may read.</summary>
    public IReadOnlyList<string> AllowedEnvVars { get; init; } = [];

    /// <summary>The MCP server an <c>mcp_tool</c> hook calls, by its configured name.</summary>
    public string? Server { get; init; }

    /// <summary>The tool on that server.</summary>
    public string? Tool { get; init; }

    /// <summary>The tool's arguments; a string <c>${path}</c> reads that path from the event's JSON.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>The hook's own JSON object, as read — what a save writes back for a hook nobody edited.</summary>
    public JsonElement? Raw { get; init; }

    /// <summary>The types cxagent runs: all five of the contract's.</summary>
    public static bool TypeRuns(string type) => type is "command" or "prompt" or "agent" or "http" or "mcp_tool";

    /// <summary>Whether this hook fires: its event is wired and its type is one cxagent runs.</summary>
    /// <para>A MODEL'S YES OR NO ANSWERS NO PERMISSION REQUEST — the contract gives that event's decision to command
    /// hooks — so a prompt or agent hook on it is kept and shown as not supported here rather than run for nothing.</para>
    public bool Runs => HookEvents.Fires(Event) && TypeRuns(Type)
                        && !(Event == HookEvents.PermissionRequest && Type is "prompt" or "agent");

    /// <summary>
    /// What messages and F8 call this hook: its <c>name</c>, else its event, matcher and the start of what
    /// it runs — enough to tell two hooks on one event apart without a name.
    /// </summary>
    public string DisplayName => Name is { Length: > 0 } n ? n
        : string.Join(' ', new[] { Event, Matcher, FirstWords(Command ?? Prompt ?? Url ?? (Server is not null ? $"{Server}.{Tool}" : Type)) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>What the hook runs, in a few words — its command, prompt, URL or tool.</summary>
    public string? Body => Command ?? Prompt ?? Url ?? (Server is not null ? $"{Server}.{Tool}" : null);

    private static string FirstWords(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2));
}

/// <summary>What one <c>hooks</c> block held: the hooks, and what was wrong with the rest of it.</summary>
public sealed record HookParse(IReadOnlyList<HookDefinition> Hooks, IReadOnlyList<string> Problems);
