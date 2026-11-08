namespace CxAgent.Core.Agents;

/// <summary>
/// What a worker is, when it is more than an ordinary one — how every surface that shows workers names it: the kind
/// in place of "Worker", its name in place of the handle, and a glyph for its tab.
///
/// <para>ONE BADGE FOR EVERY SURFACE IN BOTH FRONT ENDS, answered by the module that marked the worker (see
/// <see cref="SubAgentStore.Badges"/>), so the terminal's row and the browser's card can never call it different
/// things — and the worker code itself still only carries an origin it does not read.</para>
/// </summary>
/// <param name="Kind">The word that replaces "Worker" — "Colleague".</param>
/// <param name="Name">Its name, which replaces the handle where a person reads it.</param>
/// <param name="Glyph">Its tab's mark.</param>
public sealed record WorkerBadge(string Kind, string Name, string Glyph);
