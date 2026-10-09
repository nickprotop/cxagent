namespace CxAgent.Core.Agents;

/// <summary>
/// A steer as it is handed to the model: what the model reads, and what the transcript shows.
///
/// <para>TWO TEXTS BECAUSE THEY DIFFER. Text the app queued for the user's next message — a job's
/// output copied to the transcript — travels with the steer to the model, but the user did not type
/// it, and drawing it in their message bar puts a log in their mouth. An idle submit already makes
/// this split with its echo; a steer needs the same.</para>
/// </summary>
/// <param name="Shown">What the user typed — the transcript's account of their message.</param>
/// <param name="ForModel">What the model receives: anything queued for this message, then the
/// user's words.</param>
public sealed record SteerDelivery(string Shown, string ForModel);
