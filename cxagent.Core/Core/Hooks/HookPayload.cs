using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Models;

namespace CxAgent.Core.Hooks;

/// <summary>
/// Who and where an event happened — the fields every hook's input carries.
/// </summary>
/// <param name="TranscriptPath">The session's <c>history.jsonl</c>.</param>
/// <param name="PermissionMode">The session's edit mode, in the contract's words.</param>
/// <param name="Agent"><c>main</c>, or the sub-agent's type and id.</param>
public sealed record HookEventContext(string SessionId, string TranscriptPath, string Cwd, string PermissionMode, string Agent)
{
    /// <summary>The sub-agent's id; null for the session's own agent.</summary>
    public string? AgentId { get; init; }

    /// <summary>The sub-agent's type; null for the session's own agent.</summary>
    public string? AgentType { get; init; }
}

/// <summary>Builds the JSON a hook reads on stdin.</summary>
public static class HookPayload
{
    /// <summary>An event's input: the common fields, then the event's own.</summary>
    public static JsonObject For(HookEventContext ctx, string eventName, JsonObject fields)
    {
        var o = new JsonObject
        {
            ["session_id"] = ctx.SessionId,
            ["transcript_path"] = ctx.TranscriptPath,
            ["cwd"] = ctx.Cwd,
            ["hook_event_name"] = eventName,
            ["permission_mode"] = ctx.PermissionMode,
            ["agent"] = ctx.Agent,
        };
        // THE CONTRACT'S SUB-AGENT FIELDS, present only inside one — their absence is how a script written
        // for it tells the session's own agent apart.
        if (ctx.AgentId is not null) o["agent_id"] = ctx.AgentId;
        if (ctx.AgentType is not null) o["agent_type"] = ctx.AgentType;
        foreach (var kv in fields) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    /// <summary>A tool event's input: the call as the model made it, with the contract's argument names beside cxagent's.</summary>
    /// <param name="response">What the tool returned, for <c>PostToolUse</c>; null otherwise.</param>
    /// <param name="error">Why the tool failed, for <c>PostToolUseFailure</c>; null otherwise.</param>
    public static JsonObject ForTool(HookEventContext ctx, string eventName, ToolCall call,
        JsonObject? response = null, string? error = null)
    {
        var fields = new JsonObject
        {
            ["tool_name"] = call.Name,
            ["tool_input"] = HookNames.WithAliases(call.Name, InputOf(call)),
            ["tool_use_id"] = call.Id,
        };
        if (response is not null) fields["tool_response"] = response.DeepClone();
        if (error is not null) fields["error"] = error;
        return For(ctx, eventName, fields);
    }

    /// <summary>A call's arguments as an object; arguments that are not one (a malformed call) read as empty.</summary>
    public static JsonObject InputOf(ToolCall call) =>
        call.Arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(call.Arguments.GetRawText())!.AsObject()
            : [];
}
