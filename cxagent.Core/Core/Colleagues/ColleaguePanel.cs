namespace CxAgent.Core.Colleagues;

/// <summary>
/// The colleagues as a session's panel shows them — every one in the library, here or not, and who is distilling —
/// ONE RECORD FOR BOTH FRONT ENDS, so the terminal's section and the browser's pane list the same rows in the same way.
/// </summary>
/// <param name="Rows">Each colleague, most recently worked first.</param>
/// <param name="Distilling">Colleagues this session's drains are distilling now.</param>
public sealed record ColleaguePanel(IReadOnlyList<ColleaguePanelRow> Rows, IReadOnlyList<string> Distilling);

/// <summary>One colleague in the panel.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its one line.</param>
/// <param name="Handle">Its worker's handle in this session, or null when it is not here — then the panel offers Bring.</param>
/// <param name="State">"working", "idle" or "distilling" for one here; "distilling" or "" for one not here.</param>
public sealed record ColleaguePanelRow(string Name, string Description, string? Handle, string State)
{
    /// <summary>Its worker's agent id when it is here — what opens its tab.</summary>
    public string? AgentId { get; init; }
}
