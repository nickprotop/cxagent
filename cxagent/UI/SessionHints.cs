namespace CxAgent.UI;

/// <summary>
/// The two lines this front end volunteers about sessions the user did not ask about: what is here
/// when the app opens, and how to come back when it closes.
///
/// <para>NOT IN CORE, and the distinction is worth stating because they lived there and read as
/// though they belonged. <c>SessionsCommand.Decide</c> and <c>RenderPlain</c> answer a question the
/// user ASKED — they are what <c>/sessions</c> says, and every front end owes the same answer. These
/// two are unprompted: a front end decides whether to say anything at all, and what. A log writer
/// would say neither; a web front end would link rather than print a command line.</para>
///
/// <para>AND ONE OF THEM NAMES THE BINARY. <see cref="Exit"/> emits "cxagent --resume …", which Core
/// cannot know and a library must not assume — the clearest evidence that this text belongs to the
/// app rather than to the session layer.</para>
/// </summary>
/// <remarks>Public rather than internal because this is the app's own text, outside the Core
/// assembly boundary the InternalsVisibleTo grant is drawn around.</remarks>
public static class SessionHints
{
    /// <summary>
    /// The line shown at startup when this folder has history, or null when it has none.
    ///
    /// <para>A HINT, NOT A QUESTION. This replaced a dialog that asked "an earlier session ended
    /// without closing — resume it?" on the first render, before the user had typed anything. It
    /// asked at the worst possible moment, could only ever offer ONE session (everything older was
    /// unreachable), and made resume something that happened TO you rather than something you asked
    /// for.</para>
    ///
    /// <para>THE UNFINISHED ONE IS NAMED, because "ended without closing" is the case where someone
    /// lost work and is looking for it. Everything else is a count and a pointer.</para>
    /// </summary>
    /// <param name="here">How many sessions this folder has.</param>
    /// <param name="unfinishedMessages">
    /// The size of the newest session that ended without closing, or null when there is none.
    /// </param>
    public static string? Startup(int here, int? unfinishedMessages)
    {
        if (here == 0) return null;

        var muted = Core.Commands.Markup.Muted;

        return unfinishedMessages is { } messages
            ? $"[{muted}]An earlier session here ended without closing ({messages} messages). "
              + $"/sessions to see it — {here} in this folder.[/]"
            : $"[{muted}]{here} earlier session{(here == 1 ? "" : "s")} in this folder — "
              + $"/sessions to see {(here == 1 ? "it" : "them")}.[/]";
    }

    /// <summary>
    /// How to reopen the session that just ended, printed on the way out.
    ///
    /// <para>THE ONE MOMENT THE ID IS WORTH SOMETHING. Everywhere else it is an implementation
    /// detail; here it turns "I closed that by accident" into a command that can be pasted. Costless
    /// to ignore — a line on a terminal the user is already leaving — and the alternative is learning
    /// that resume exists from the documentation of an app you have stopped using.</para>
    /// </summary>
    public static string Exit(string uid) =>
        $"Resume this session:  cxagent --resume {Core.Commands.SessionsCommand.Short(uid)}";

    /// <summary>
    /// What this session cost and how to come back, printed on the way out.
    ///
    /// <para>THE TERMINAL IS THE LAST SURFACE, and until now it was blank. Everything the panel
    /// showed — spend, cache, what the sub-agents used — vanished with the alt screen, so a session
    /// that had just spent two million tokens ended with either one line about resume or nothing at
    /// all. The numbers exist right up to the moment the ledger is disposed; not printing them was
    /// an omission rather than a decision.</para>
    ///
    /// <para>NOTHING WHEN NOTHING HAPPENED. A launch that took no turn has no spend to report and no
    /// session to resume — and a summary of zeros reads as a malfunction. That case is exactly why
    /// closing the app immediately after opening it printed a bare line and looked broken.</para>
    ///
    /// <para>PLAIN TEXT, NO MARKUP. This is written with Console.WriteLine after the TUI has released
    /// the terminal, so there is nothing left to interpret tags — a lesson the plain-text listing in
    /// SessionsCommand.RenderPlain already records.</para>
    /// </summary>
    /// <param name="uid">The ended session, or null when nothing was saved.</param>
    /// <param name="spend">What it cost, or null when no turn ran.</param>
    public static string? Farewell(string? uid, SessionSpend? spend)
    {
        var lines = new List<string>();

        if (spend is { Total: > 0 } s)
        {
            lines.Add("");
            lines.Add($"  {s.Total:N0} tokens  ·  {s.Input:N0} in  ·  {s.Output:N0} out");

            // ONLY WHAT IS KNOWN. A cache rate is null on a provider that reports none, and a cost is
            // null when no price is configured — printing "0%" or "$0.00" for either would be a
            // number the user cannot act on and cannot tell from a real zero.
            var extras = new List<string>();
            if (s.CacheHitRate is { } rate) extras.Add($"{rate:P0} cache");
            if (s.SubAgentTokens > 0) extras.Add($"{s.SubAgentTokens:N0} in sub-agents");
            if (s.Cost is { } cost) extras.Add($"${cost:N2}");
            if (extras.Count > 0) lines.Add($"  {string.Join("  ·  ", extras)}");
        }

        if (uid is { Length: > 0 })
        {
            lines.Add("");
            lines.Add($"  {Exit(uid)}");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// What a session spent, read off the ledger before it is disposed.
///
/// <para>A RECORD RATHER THAN SIX ARGUMENTS, per this repo's rule: they are one fact — "what this
/// session cost" — and Total/Input/Output/SubAgentTokens are four ints in a row that would compile
/// cleanly in the wrong order.</para>
/// </summary>
public sealed record SessionSpend(int Total, int Input, int Output, int SubAgentTokens)
{
    /// <summary>Null on a provider that reports no cache statistics — not zero.</summary>
    public double? CacheHitRate { get; init; }

    /// <summary>Null when no price is configured for the model in use — not zero.</summary>
    public decimal? Cost { get; init; }
}