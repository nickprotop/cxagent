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

    public string? Model { get; init; }
    public string? Url { get; init; }

    /// <summary>The hook's own JSON object, as read — what a save writes back for a hook nobody edited.</summary>
    public JsonElement? Raw { get; init; }

    /// <summary>The types cxagent runs today.</summary>
    public static bool TypeRuns(string type) => type == "command";

    /// <summary>Whether this hook fires: its event is wired and its type is one cxagent runs.</summary>
    public bool Runs => HookEvents.Fires(Event) && TypeRuns(Type);

    /// <summary>
    /// What messages and F8 call this hook: its <c>name</c>, else its event, matcher and the start of what
    /// it runs — enough to tell two hooks on one event apart without a name.
    /// </summary>
    public string DisplayName => Name is { Length: > 0 } n ? n
        : string.Join(' ', new[] { Event, Matcher, FirstWords(Command ?? Url ?? Type) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string FirstWords(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2));
}

/// <summary>What one <c>hooks</c> block held: the hooks, and what was wrong with the rest of it.</summary>
public sealed record HookParse(IReadOnlyList<HookDefinition> Hooks, IReadOnlyList<string> Problems);
