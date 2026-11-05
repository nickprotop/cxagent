using System.Text.Json.Nodes;

namespace CxAgent.Core.Hooks;

/// <summary>How a hook's run ended, by the contract's exit-code rules.</summary>
public enum HookOutcomeKind
{
    /// <summary>Exit 0: its stdout, if JSON, says what it wants.</summary>
    Ok,
    /// <summary>Exit 2: a block, with stderr as the reason.</summary>
    Block,
    /// <summary>Any other exit, a timeout, a crash, or output that should be JSON and is not.</summary>
    Failed,
}

/// <summary>
/// What one hook answered. The accessors read the contract's output fields wherever it puts them, so a
/// caller asks "did it deny?" rather than knowing which event keeps its decision where.
/// </summary>
/// <param name="Reason">For <see cref="HookOutcomeKind.Block"/> its stderr, for <see cref="HookOutcomeKind.Failed"/> what went wrong.</param>
/// <param name="Json">Its stdout as a JSON object, when it printed one.</param>
public sealed record HookReply(HookOutcomeKind Kind, string? Reason, JsonObject? Json, string Stdout, string Stderr)
{
    public static HookReply Failed(string reason) => new(HookOutcomeKind.Failed, reason, null, "", "");

    private JsonObject? Specific => Json?["hookSpecificOutput"] as JsonObject;

    /// <summary>
    /// <c>allow</c>, <c>deny</c>, <c>ask</c>, <c>defer</c> or <c>block</c>, from wherever this event's output keeps it:
    /// <c>permissionDecision</c>, a PermissionRequest's <c>decision.behavior</c>, or a top-level <c>decision</c>.
    /// </summary>
    public string? Decision =>
        Str(Specific?["permissionDecision"])
        ?? Str((Specific?["decision"] as JsonObject)?["behavior"])
        ?? Str(Json?["decision"]);

    public string? DecisionReason =>
        Str(Specific?["permissionDecisionReason"])
        ?? Str((Specific?["decision"] as JsonObject)?["message"])
        ?? Str(Json?["reason"]);

    public JsonObject? UpdatedInput =>
        Specific?["updatedInput"] as JsonObject
        ?? (Specific?["decision"] as JsonObject)?["updatedInput"] as JsonObject;

    public string? AdditionalContext => Str(Specific?["additionalContext"]);
    public string? SystemMessage => Str(Json?["systemMessage"]);

    /// <summary>False when the hook asked for the whole turn to stop (<c>continue: false</c>).</summary>
    public bool Continue => Json?["continue"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
    public string? StopReason => Str(Json?["stopReason"]);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
