using CxAgent.Core.Permissions;

namespace CxAgent.Core.Sessions;

/// <summary>
/// What a session says when its edit mode moves.
///
/// <para>IN CORE, BESIDE THE THING IT DESCRIBES. This lived in the UI and both callers composed their
/// own version: /mode used this sentence, Shift+Tab used a thinner one — "edits: accept-edits."
/// against "edits: accept-edits — writes in this folder are silent." Two wordings for one action is
/// two things to keep in step, and the shorter one omitted exactly the part that matters.</para>
///
/// <para>WHAT IS ACTUALLY IN FORCE, not what the name promises. On an untrusted folder accept-edits
/// changes nothing observable, and a readout that says "writes are now silent" there is wrong
/// exactly when it matters most.</para>
/// </summary>
public static class ModeNotice
{
    public static string EditsChanged(EditMode mode, bool folderTrusted, string root) =>
        $"edits: {EditModes.Name(mode)} — {Effect(mode, folderTrusted, root)}.";

    /// <summary>What an edit mode actually does here, without the "edits: X —" prefix — for a
    /// listing that describes the current state rather than reporting a change.</summary>
    public static string Effect(EditMode mode, bool trusted, string root) => mode switch
    {
        _ when !trusted => "asks for everything (this folder is not trusted)",
        EditMode.AlwaysAsk => "every write asks; stored rules still apply",
        EditMode.Auto => $"a classifier reviews each write; outside {root} asks",
        _ => $"writes inside {root} are silent; elsewhere asks",
    };
}
