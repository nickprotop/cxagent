using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>Outcome of a Test Connection: reachability plus the tool-calling capability probe.</summary>
public record ProbeResult(bool Reachable, bool SupportsTools, string? Error);

/// <summary>
/// "Test Connection" for the setup wizard: one minimal chat round-trip against a constructed
/// provider. Never throws — a failure is data (ProbeResult.Error) the wizard renders, because a bad
/// key or unreachable endpoint is an expected outcome of setup, not an exceptional one.
/// </summary>
public static class ProviderProbe
{
    public static async Task<ProbeResult> TestAsync(ILlmProvider provider, CancellationToken ct)
    {
        try
        {
            // ChatMessage.Role is a STRING ("user"/"assistant"/"system"), not an enum, and both
            // Role and Content are `required` — verified against cxagent/Core/Models/ChatMessage.cs.
            var msgs = new List<ChatMessage>
            {
                new() { Role = "user", Content = "ping" },
            };
            await provider.ChatAsync(msgs, null, ct);
            return new ProbeResult(true, provider.SupportsToolCalling, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProbeResult(false, false, ex.Message);
        }
    }
}
