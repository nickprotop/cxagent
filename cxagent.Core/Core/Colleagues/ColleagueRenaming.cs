using CxAgent.Core.Storage;

namespace CxAgent.Core.Colleagues;

/// <summary>A colleague to rename, by any name it has had, and the name it is to have.</summary>
public sealed record RenameOrder(string Name, string NewName);

/// <summary>
/// Renaming a colleague — ONE OPERATION FOR EVERY WAY IN: the verb, F8's page and the browser's page each call this, so
/// none can rename past a check another makes.
///
/// <para>THE RECORD CHANGES, THE FOLDER STAYS: other processes may be appending a round or distilling as it happens, and a
/// folder moved under them would lose the write or, on Windows, refuse to move at all. The old name goes into the record's
/// former names, which is how a session that recorded a worker as <c>colleague:OldName</c> still brings the colleague
/// back (user, 2026-11-09).</para>
/// </summary>
public static class ColleagueRenaming
{
    /// <summary>
    /// Why <paramref name="order"/> cannot be done, without changing anything — the checks a draft makes before it writes
    /// a field, so a refused rename never leaves half a save behind.
    /// </summary>
    public static string? Refusal(ColleagueStore library, FolderSessionStore? sessions, RenameOrder order)
    {
        var to = order.NewName.Trim();
        if (ColleagueStore.NameRefusal(to) is { } bad) return bad;
        if (library.Find(order.Name.Trim()) is not { } record) return $"There is no colleague called '{order.Name.Trim()}'.";
        // A NAME ANOTHER COLLEAGUE HAS, OR HAD: an old session's worker under it would come back as the wrong colleague.
        if (library.Find(to) is { } other && !string.Equals(library.FolderOf(other.Name), library.FolderOf(record.Name), StringComparison.Ordinal))
            return string.Equals(other.Name, to, StringComparison.OrdinalIgnoreCase)
                ? $"There is already a colleague called '{other.Name}'."
                : $"'{to}' was a name of {other.Name}'s, and still finds it.";
        // ITS ONE-TO-ONE, OPEN ANYWHERE, runs under the name it was opened with: renamed beneath it, its rounds would be
        // kept for a name being given up.
        if (sessions is not null && OneToOneHeld(sessions, record) is { } held)
            return $"{record.Name}'s one-to-one is {SessionLock.HeldMessage(held)} — close it first.";
        return null;
    }

    /// <summary>Renames the colleague and answers why not, or null when it did.</summary>
    public static string? Rename(ColleagueStore library, FolderSessionStore? sessions, RenameOrder order)
    {
        if (Refusal(library, sessions, order) is { } why) return why;
        var to = order.NewName.Trim();

        // NOT WHILE IT IS DISTILLED: the distiller reads the colleague by name between its calls, and the drain's own list
        // was taken before the rename.
        DistillingLock? held;
        try
        {
            held = DistillingLock.TakeWithin(library.Root, DistillingLock.BriefWait);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not rename: {e.Message}";
        }
        if (held is null) return "A colleague is being distilled — try again in a moment.";
        using (held)
        {
            // ASKED AGAIN UNDER THE LOCK: another process renaming to the same name, or opening the one-to-one, between
            // the first look and the lock would otherwise pass both.
            if (Refusal(library, sessions, order) is { } now) return now;
            try
            {
                var renamed = library.Update(order.Name.Trim(), r => string.Equals(r.Name, to, StringComparison.Ordinal) ? r : r with
                {
                    Name = to,
                    // THE OLD SPELLING IS KEPT, a case-only rename included: a session holds the name exactly as it was.
                    FormerNames = [.. r.FormerNames.Where(f => !string.Equals(f, to, StringComparison.Ordinal)), r.Name],
                });
                return renamed is null ? $"There is no colleague called '{order.Name.Trim()}'." : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"Could not rename: {e.Message}";
            }
        }
    }

    /// <summary>
    /// Every origin a worker of this colleague may carry, and every tag its one-to-one may be saved under — its name now,
    /// then the names it had, newest first.
    /// </summary>
    public static IEnumerable<string> Origins(ColleagueRecord record) =>
        new[] { record.Name }.Concat(record.FormerNames.Reverse()).Select(n => ColleagueDesk.OriginPrefix + n);

    private static HeldBy? OneToOneHeld(FolderSessionStore sessions, ColleagueRecord record) =>
        Origins(record).SelectMany(sessions.ListTagged).Select(s => sessions.HolderOf(s.Uid)).FirstOrDefault(h => h is not null);
}
