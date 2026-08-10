using CxAgent.Core.Agent;
using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>
/// Forwards every call to whichever <see cref="IChatSink"/> is CURRENT, set via <see cref="Current"/>.
///
/// <para>Exists for <see cref="InteractivePermissionGate"/>: AppBootstrap builds the gate once, near
/// the top of <c>Run</c>, before the real <c>ChatTranscriptSink</c> exists (that sink is built inside
/// <c>WireRunner</c>, which itself needs the gate to already be wired into <c>PluginRegistry</c>). An
/// F5/F7/F8 re-wire replaces the transcript sink with a fresh instance — pointing this wrapper at the
/// new one keeps the gate's permission echoes landing in the visible transcript across every re-wire,
/// without rebuilding the gate (and losing its <see cref="PermissionRulesStore"/>-backed state) each
/// time.</para>
///
/// <para><c>Current</c> is null only in the brief window before the first <c>WireRunner</c> call — no
/// goal can run in that window, so no permission request can reach the gate yet either; every call
/// here degrades to a silent no-op via <see cref="IChatSink"/>'s nullable-caller convention.</para>
/// </summary>
public sealed class LatestChatSink : IChatSink
{
    public IChatSink? Current { get; set; }

    public ChatMessageId AddUserTurn(string text) => Current?.AddUserTurn(text) ?? default;
    public ChatMessageId BeginAssistantTurn() => Current?.BeginAssistantTurn() ?? default;
    public void AppendAssistant(ChatMessageId id, string token) => Current?.AppendAssistant(id, token);
    public void AppendReasoning(ChatMessageId id, string text) => Current?.AppendReasoning(id, text);
    public void SetAssistantHeader(ChatMessageId id, string header) { }

    public void EndAssistantTurn(ChatMessageId id) => Current?.EndAssistantTurn(id);
    public void ShowError(string message) => Current?.ShowError(message);
    public void ShowSystemMessage(string message) => Current?.ShowSystemMessage(message);
}
