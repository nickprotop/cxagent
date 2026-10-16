namespace CxAgent.Core.Agents;

/// <summary>
/// Something delivered to an agent, as the transcript shows it and as the model reads it.
///
/// <para>TWO TEXTS, IN <see cref="SteerDelivery"/>'S ORDER, for its reason: a background agent's report
/// is the child's whole answer, and drawing pages of it in the parent's transcript buries the
/// conversation under work the user delegated to keep out of it. The model needs all of it; the user
/// needs one line saying it arrived. The same order as SteerDelivery because two records of two strings
/// in opposite orders transpose without a compiler error — construct both by name.</para>
/// </summary>
/// <param name="Shown">The one line the transcript draws.</param>
/// <param name="ForModel">What the agent reads.</param>
public sealed record AgentNotice(string Shown, string ForModel);
