namespace CxAgent.Core.Llm;

/// <summary>
/// The model a session is talking to right now.
///
/// <para>THE SECOND OF THREE LIFETIMES <see cref="ResolvedConfig"/> HELD TOGETHER, and the only one
/// that moves: <c>/model</c> replaces this and nothing else. Keeping it apart from
/// <see cref="ProviderCatalog"/> is what makes a switch expressible as a type rather than as a list
/// of fields somebody has to remember — and forgetting one is exactly what happened when
/// SwapProvider moved the agent and the host but not the sub-agent factory's captured default.</para>
///
/// <para>EVERY MEMBER TRAVELS TOGETHER. A provider without its window compacts against the wrong
/// number; a window without its instance name reports spend under the wrong label. They are one
/// fact — "which model, and what that implies" — which is why this is a record and not four
/// parameters.</para>
/// </summary>
/// <param name="Provider">The endpoint that gets called.</param>
/// <param name="InstanceName">
/// Which <c>providers</c> entry is in use, or null when it cannot be named — the mock has no config
/// entry. Half of the <c>instance:model</c> label spend is attributed to.
/// </param>
/// <param name="DisplayName">The driver's own label, distinct from the config name above.</param>
/// <param name="ContextWindow">
/// How much this model holds, in tokens.
///
/// <para>NOT DECORATION. The compression threshold derives from it, so a wrong number compacts far
/// too early or not until the provider refuses the request. Null means unknown and compaction falls
/// back to a fixed threshold — ILlmProvider carries no such property, it is config-only.</para>
/// </param>
public sealed record ActiveModel(
    ILlmProvider Provider,
    string? InstanceName = null,
    string? DisplayName = null,
    int? ContextWindow = null)
{
    /// <summary>What spend is filed under: <c>instance:model</c>, or the bare model id when the
    /// instance cannot be named.</summary>
    public string Label => InstanceName is { Length: > 0 } instance
        ? $"{instance}:{Provider.ModelId}"
        : Provider.ModelId;
}
