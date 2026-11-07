using CxAgent.Core.Colleagues;

namespace CxAgent.Core.Sessions;

/// <summary>The session's desk for colleagues — keeping its workers, bringing colleagues in.</summary>
public sealed partial class Session
{
    /// <summary>This session's desk for colleagues, or null when colleagues are off.</summary>
    public ColleagueDesk? Colleagues { get; private set; }

    /// <summary>
    /// Points the session at its colleagues — on every wire, so turning them off in config takes effect, and a re-wire's
    /// new spawner and models reach the desk. ONE DESK FOR THE SESSION'S LIFE while the library stays the same: it
    /// listens to the session's workers, whose store outlives every re-wire, and a second desk would hear each round
    /// twice.
    /// </summary>
    internal void UseColleagues(ColleagueDeskParts? parts)
    {
        if (parts is null)
        {
            Colleagues?.Dispose();
            Colleagues = null;
            return;
        }
        if (Colleagues is { } desk && desk.Library.Root == parts.Library.Root)
        {
            desk.Rewire(parts);
            return;
        }
        Colleagues?.Dispose();
        Colleagues = new ColleagueDesk(parts);
    }
}
