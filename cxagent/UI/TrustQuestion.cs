namespace CxAgent.UI;

/// <summary>
/// What the "Trust this folder?" question says, and its answers in their order — for the terminal's list and a
/// browser's prompt alike, so the two cannot drift: the browser's was a shorter text with the risky answer first.
/// </summary>
public static class TrustQuestion
{
    /// <summary>
    /// WHAT TRUST ACTUALLY BUYS, kept current and true in every mode. A consent screen that overstates its own restraint
    /// is the worst kind to leave stale: the user grants something broader than the words they read. It must not
    /// overpromise either: an in-folder write passes silently only under accept-edits — always-ask still asks for it,
    /// trusted or not.
    /// </summary>
    public static IReadOnlyList<string> Lines { get; } =
    [
        "Trusted folders allow file reads, and commands that only read — ls, cat, grep — without asking; writes inside "
            + "this folder go through without asking in accept-edits mode.",
        "Commands that can write, and anything outside this folder, still ask.",
    ];

    /// <summary>
    /// THE CAUTIOUS ANSWER FIRST: whichever sits first is what a stray Enter chooses, and the two are not equally
    /// recoverable — "don't trust" costs prompts and is undone by /trust; an accidental "trust" grants every read and
    /// write in the folder and does not ask again. The first means "don't trust", the second "trust".
    /// </summary>
    public static IReadOnlyList<string> Answers { get; } = ["Don't trust — ask before everything", "Trust this folder"];
}
