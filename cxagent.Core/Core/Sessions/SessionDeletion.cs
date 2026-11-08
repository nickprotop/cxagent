using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>What deleting a saved session would do, and the question to ask first — or why it cannot be deleted.</summary>
/// <param name="Uid">The session to delete, or null when it cannot be.</param>
/// <param name="Text">The question to confirm, or, without a uid, why not.</param>
public sealed record SessionDeleting(string? Uid, string Text);

/// <summary>
/// Deleting a saved session for good — ONE RULE FOR BOTH FRONT ENDS: the terminal's <c>/sessions delete</c> and the
/// browser's × on a listed session ask with these words and delete through this, so neither refuses or removes what the
/// other would not.
///
/// <para>NEVER A SESSION IN USE: one open in this process is closed first, and one another cxagent holds is that
/// process's — deleting its folder under it would lose what it writes next.</para>
/// </summary>
public static class SessionDeletion
{
    /// <summary>The question to ask before deleting <paramref name="uid"/>, or why it cannot be deleted.</summary>
    public static SessionDeleting Ask(FolderSessionStore store, SessionManager manager, string uid)
    {
        if (Refusal(store, manager, uid, out var info) is { } why) return new SessionDeleting(null, why);
        return new SessionDeleting(info!.Uid,
            $"Delete \"{Title(info)}\" ({Commands.SessionsCommand.Short(info.Uid)})? Its conversation, logs and workers' "
            + "histories are removed and cannot be brought back.");
    }

    /// <summary>Deletes <paramref name="uid"/> after the user said yes, and answers the line to show.</summary>
    public static string Delete(FolderSessionStore store, SessionManager manager, string uid)
    {
        // ASKED AGAIN AT THE MOMENT OF DELETING: between the question and the yes, the session may have been opened.
        if (Refusal(store, manager, uid, out var info) is { } why) return why;
        try
        {
            store.Delete(info!.Uid);
            return $"Deleted \"{Title(info)}\".";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not delete \"{Title(info!)}\": {e.Message}";
        }
    }

    private static string? Refusal(FolderSessionStore store, SessionManager manager, string uid, out SessionInfo? info)
    {
        info = null;
        // OPEN HERE IS ASKED FIRST: a session open in this process may not have been saved yet, and "no such session"
        // would be the wrong reason to give for the one the user is looking at.
        if (manager.Sessions.Any(s => s.SessionId == uid))
            return "That session is open here. Close it first, then delete it.";
        info = store.List(all: true).FirstOrDefault(i => i.Uid == uid);
        if (info is null) return "No saved session has that id.";
        if (info.Holder is { } holder) return $"That session is {SessionLock.HeldMessage(holder)}; it cannot be deleted while it is.";
        return null;
    }

    private static string Title(SessionInfo info) => info.Title is { Length: > 0 } t ? t : Commands.SessionsCommand.Short(info.Uid);
}
