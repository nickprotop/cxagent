using System.Text.Json.Nodes;

namespace CxAgent.Core.Hooks;

/// <summary>What a library handler is told about an event: the same JSON a command hook reads on stdin.</summary>
public sealed record HookInput(string Event, JsonObject Json)
{
    /// <summary>The tool's name, on tool events.</summary>
    public string? ToolName => (string?)Json["tool_name"];

    /// <summary>The tool's arguments as they stand after earlier hooks' rewrites, on tool events.</summary>
    public JsonObject? ToolInput => Json["tool_input"] as JsonObject;

    /// <summary>The user's prompt, on <c>UserPromptSubmit</c>.</summary>
    public string? Prompt => (string?)Json["prompt"];

    /// <summary><c>main</c>, or the sub-agent's type and id.</summary>
    public string? Agent => (string?)Json["agent"];
}

/// <summary>What a handler wants done. <see cref="None"/> is "no objection, nothing to add".</summary>
public sealed record HookAnswer
{
    /// <summary><c>allow</c>, <c>deny</c>, <c>ask</c> or <c>block</c>; null for none.</summary>
    public string? Decision { get; init; }
    public string? Reason { get; init; }

    /// <summary>The tool's arguments to use instead, in cxagent's names.</summary>
    public JsonObject? Rewrite { get; init; }

    /// <summary>Text for the model: added beside the prompt, the tool result, or the session's start.</summary>
    public string? Context { get; init; }

    public static HookAnswer None { get; } = new();
    public static HookAnswer Allow(string? reason = null) => new() { Decision = "allow", Reason = reason };
    public static HookAnswer Deny(string reason) => new() { Decision = "deny", Reason = reason };
    public static HookAnswer Ask(string? reason = null) => new() { Decision = "ask", Reason = reason };
    public static HookAnswer Block(string reason) => new() { Decision = "block", Reason = reason };
    public static HookAnswer WithInput(JsonObject input) => new() { Rewrite = input };
    public static HookAnswer AddContext(string text) => new() { Context = text };
}

/// <summary>
/// Hooks an embedding app registers in code: typed, in-process, no shell and no JSON to print.
///
/// <para>ONE PIPELINE WITH THE FILES. A handler's answer is combined with the command hooks' by the same
/// rules — deny beats ask beats allow, protected folders still ask — so an app's guard and a user's
/// hooks file cannot disagree about what a decision means. Handlers run after the files, and see the
/// arguments as the files' rewrites left them.</para>
/// </summary>
public sealed class HookHandlers
{
    private readonly List<(string Event, Func<HookInput, CancellationToken, Task<HookAnswer>> Handle)> _handlers = [];
    private readonly object _lock = new();

    /// <summary>Registers a handler for any event by its contract name.</summary>
    public HookHandlers On(string eventName, Func<HookInput, CancellationToken, Task<HookAnswer>> handle)
    {
        lock (_lock) _handlers.Add((eventName, handle));
        return this;
    }

    public HookHandlers On(string eventName, Func<HookInput, HookAnswer> handle) =>
        On(eventName, (input, _) => Task.FromResult(handle(input)));

    public HookHandlers OnPreToolUse(Func<HookInput, HookAnswer> handle) => On(HookEvents.PreToolUse, handle);
    public HookHandlers OnPreToolUse(Func<HookInput, CancellationToken, Task<HookAnswer>> handle) => On(HookEvents.PreToolUse, handle);
    public HookHandlers OnPostToolUse(Func<HookInput, HookAnswer> handle) => On(HookEvents.PostToolUse, handle);
    public HookHandlers OnUserPromptSubmit(Func<HookInput, HookAnswer> handle) => On(HookEvents.UserPromptSubmit, handle);
    public HookHandlers OnStop(Func<HookInput, HookAnswer> handle) => On(HookEvents.Stop, handle);
    public HookHandlers OnSessionStart(Func<HookInput, HookAnswer> handle) => On(HookEvents.SessionStart, handle);
    public HookHandlers OnSessionEnd(Func<HookInput, HookAnswer> handle) => On(HookEvents.SessionEnd, handle);
    public HookHandlers OnNotification(Func<HookInput, HookAnswer> handle) => On(HookEvents.Notification, handle);

    internal IReadOnlyList<Func<HookInput, CancellationToken, Task<HookAnswer>>> For(string eventName)
    {
        lock (_lock) return [.. _handlers.Where(h => h.Event == eventName).Select(h => h.Handle)];
    }

    /// <summary>Whether any handler listens to this event.</summary>
    public bool Any(string eventName)
    {
        lock (_lock) return _handlers.Exists(h => h.Event == eventName);
    }

    /// <summary>How many handlers are registered, for <c>/hooks</c>.</summary>
    public int Count
    {
        get { lock (_lock) return _handlers.Count; }
    }
}
