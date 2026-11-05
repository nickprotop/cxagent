using System.Text.Json;
using System.Text.Json.Nodes;

namespace CxAgent.Core.Hooks;

/// <summary>
/// An embedding app's hooks, on <see cref="Llm.AgentConfig.Hooks"/>: the same <c>hooks</c> block a hooks
/// file holds (<see cref="Json"/>), typed handlers registered in code, or both.
///
/// <para>NEVER BEHIND FOLDER TRUST. Trust protects a user from what a repository brings; an app's own hooks
/// are the app's code, configured by whoever built it, and run in any folder. The repository's hooks files
/// are still read beside them and still need trust.</para>
/// </summary>
public sealed class HooksConfig
{
    /// <summary>
    /// The <c>hooks</c> block as JSON text — event names to matcher groups, exactly as in a hooks file. Null
    /// for none.
    /// </summary>
    public string? Json { get; init; }

    /// <summary>The handlers registered on this config.</summary>
    public HookHandlers Handlers { get; } = new();

    public HooksConfig On(string eventName, Func<HookInput, CancellationToken, Task<HookAnswer>> handle) { Handlers.On(eventName, handle); return this; }
    public HooksConfig On(string eventName, Func<HookInput, HookAnswer> handle) { Handlers.On(eventName, handle); return this; }
    public HooksConfig OnPreToolUse(Func<HookInput, HookAnswer> handle) { Handlers.OnPreToolUse(handle); return this; }
    public HooksConfig OnPreToolUse(Func<HookInput, CancellationToken, Task<HookAnswer>> handle) { Handlers.OnPreToolUse(handle); return this; }
    public HooksConfig OnPostToolUse(Func<HookInput, HookAnswer> handle) { Handlers.OnPostToolUse(handle); return this; }
    public HooksConfig OnUserPromptSubmit(Func<HookInput, HookAnswer> handle) { Handlers.OnUserPromptSubmit(handle); return this; }
    public HooksConfig OnStop(Func<HookInput, HookAnswer> handle) { Handlers.OnStop(handle); return this; }
    public HooksConfig OnSessionStart(Func<HookInput, HookAnswer> handle) { Handlers.OnSessionStart(handle); return this; }
    public HooksConfig OnSessionEnd(Func<HookInput, HookAnswer> handle) { Handlers.OnSessionEnd(handle); return this; }
    public HooksConfig OnNotification(Func<HookInput, HookAnswer> handle) { Handlers.OnNotification(handle); return this; }

    /// <summary>
    /// The settings a session reads, or an error when <see cref="Json"/> cannot be read — reported by
    /// <see cref="Llm.AgentConfig.Resolve"/> with the rest of the config's mistakes.
    /// </summary>
    internal HooksSettings ToSettings(List<string> errors)
    {
        JsonElement? block = null;
        if (!string.IsNullOrWhiteSpace(Json))
        {
            try
            {
                if (JsonNode.Parse(Json) is JsonObject)
                    block = JsonDocument.Parse(Json).RootElement.Clone();
                else
                    errors.Add("Hooks.Json must be a JSON object of event names.");
            }
            catch (JsonException ex)
            {
                errors.Add($"Hooks.Json is not valid JSON: {ex.Message}");
            }
        }
        return new HooksSettings(block)
        {
            Handlers = Handlers.Count > 0 ? Handlers : null,
            Source = HookSource.Library,
            DefaultModel = block is { } b && b.TryGetProperty("defaultModel", out var dm) && dm.ValueKind == JsonValueKind.String
                ? dm.GetString() : null,
        };
    }
}
