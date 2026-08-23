using CxAgent.Core.Permissions;

namespace CxAgent.Core.Commands;

/// <summary>What <c>/trust</c> decided, and the line to show for it.</summary>
/// <param name="NewState">The classification to store, or null when nothing changes.</param>
/// <param name="Reply">The message for the transcript. Never empty — a command that appears to do
/// nothing is indistinguishable from one that silently failed.</param>
public readonly record struct TrustCommandResult(TrustState? NewState, Message Reply);

/// <summary>
/// What <c>/trust</c> needs to report the folder's classification and to change it.
/// </summary>
/// <param name="Argument">Everything after the command word — empty for a bare <c>/trust</c>.</param>
/// <param name="Current">The classification right now, for reporting and for detecting a no-op.</param>
/// <param name="Root">The working directory, named in every reply so "this folder" is concrete.</param>
public readonly record struct TrustQuery(string Argument, TrustState Current, string Root);

/// <summary>
/// The decision behind <c>/trust</c> — the in-app way to see and change a folder's classification.
///
/// <para>WHY IT EXISTS. Trust was answerable exactly once, at the startup question, and thereafter
/// only from a file prompt's "Trust this folder" button. That left two gaps the permissions design
/// named as follow-up work: a user who declined and never triggers an in-boundary file prompt had no
/// in-app path back, and there was NO WAY TO REVOKE trust at all — a single click granted every
/// silent read and write in a folder, and taking it back meant hand-editing permissions.json.</para>
///
/// <para>NOT A STARTUP RE-ASK, deliberately, and this command is what makes that choice affordable.
/// Re-asking an untrusted folder on every launch trains the user to click Trust to stop the
/// interruption — consent manufactured by nagging, which is worth less than no answer at all. A
/// command is user-initiated, so it cannot nag: it is reached at the moment the user wants it.</para>
///
/// <para>DECIDES ONLY, like <see cref="ModeCommand"/>: everything here is a pure function of the
/// argument, the current state and the root, so it is testable with no store, no window and no
/// session. Persisting is the session's, which is also where a failed write is reported.</para>
/// </summary>
public static class TrustCommand
{
    /// <summary>Decides what <c>/trust</c>, <c>/trust yes</c> or <c>/trust no</c> should do.</summary>
    public static TrustCommandResult Decide(TrustQuery query)
    {
        var argument = query.Argument.Trim();

        if (argument.Length == 0) return new(null, Describe(query));

        var requested = Parse(argument);
        if (requested is null)
            return new(null, new($"Unknown option '{argument}'. `/trust yes` to trust this folder, "
                + "`/trust no` to ask before everything, `/trust` to see what is set.",
                Severity.Warning));

        // A NO-OP IS SAID OUT LOUD rather than re-stored. Rewriting the same value would look
        // identical from here and would touch the file for nothing; more to the point, a user typing
        // `/trust no` on an already-untrusted folder is asking a question ("is it off?"), and the
        // honest answer names the state rather than implying something just changed.
        if (requested == query.Current)
            return new(null, new($"This folder is already {Word(query.Current)} — {query.Root}"));

        return new(requested, Changed(requested.Value, query.Root));
    }

    /// <summary>
    /// The message for a classification that just changed.
    ///
    /// <para>PUBLIC BECAUSE TWO ROUTES END HERE. The startup question and this command set the same
    /// state, and wording it twice is how the two drift into describing the same folder differently.</para>
    /// </summary>
    public static Message Changed(TrustState state, string root) => state switch
    {
        TrustState.Trusted => new($"trusted this folder — reads and writes inside {root}, and "
            + "read-only commands, will not ask again"),

        // WARNING, NOT INFO, and not because anything went wrong. This is the severity the startup
        // question uses for the same answer: the user has chosen the noisy mode, and the line is
        // worth colouring because what follows it is a session that prompts for everything.
        _ => new($"not trusted — file operations in {root} will ask every time", Severity.Warning),
    };

    /// <summary>What a bare <c>/trust</c> reports.</summary>
    private static Message Describe(TrustQuery query) => query.Current switch
    {
        TrustState.Trusted => new($"This folder is trusted — {query.Root}\n"
            + "Reads and writes inside it, and read-only commands, do not ask.\n"
            + "`/trust no` to revoke."),

        TrustState.Untrusted => new($"This folder is not trusted — {query.Root}\n"
            + "Every file operation asks. `/trust yes` to trust it."),

        // NEVER ASKED IS ITS OWN ANSWER. Folding it into "not trusted" would be true about the
        // behaviour and false about the user: nobody declined, the question is simply still owed —
        // and on a filesystem with no birth time it is owed on EVERY launch, which is the one case
        // where a user might reasonably wonder why they keep being asked.
        _ => new($"This folder has not been classified — {query.Root}\n"
            + "It behaves as untrusted until it is. `/trust yes` or `/trust no` to decide."),
    };

    /// <summary>The word used for a state in prose, so the two reply paths cannot disagree.</summary>
    private static string Word(TrustState state) =>
        state == TrustState.Trusted ? "trusted" : "not trusted";

    /// <summary>
    /// The argument vocabulary.
    ///
    /// <para>SEVERAL SPELLINGS PER ANSWER, because this command is typed rarely and the word that
    /// comes to mind is whichever the user last read — the button says "Trust this folder", the
    /// stored value says "Untrusted", and the summary says yes/no. Accepting one and rejecting the
    /// others would make a security control feel broken at the moment it is being reached for.</para>
    /// </summary>
    private static TrustState? Parse(string argument) => argument.ToLowerInvariant() switch
    {
        "yes" or "y" or "trust" or "trusted" or "on" => TrustState.Trusted,
        "no" or "n" or "untrust" or "untrusted" or "off" => TrustState.Untrusted,
        _ => null,
    };
}
