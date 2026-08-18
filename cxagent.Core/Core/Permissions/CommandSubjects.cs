namespace CxAgent.Core.Permissions;

/// <summary>
/// Everything in a shell command that a permission decision has to have looked at, and whether
/// anything was left over.
///
/// <para>THE BUG THIS TYPE EXISTS TO END. Four holes were found in this system and every one was the
/// same sentence: a check examines part of a request and lets the rest through unexamined. The
/// read-only pass vouched for the VERB and ignored its arguments, so <c>cat /etc/shadow</c> was
/// silent. A stored rule matched the command TEXT and ignored the paths in it, so <c>cat*</c> was
/// the same hole through a different door. The <c>cd</c> guard checked the DIRECTORY and ignored the
/// command after <c>&amp;&amp;</c>. And a pairing heuristic vouched for two segments and ignored the
/// third, so <c>cd x &amp;&amp; dotnet build &amp;&amp; rm -rf /</c> passed — a hole introduced while
/// FIXING the previous one, an hour later, by someone who had just read all three.</para>
///
/// <para>THE FIFTH WAS FOUND BY WRITING THIS. <c>grep --file=/etc/shadow .</c> was silently allowed
/// in any trusted folder: <c>--file=…</c> begins with a dash, <see cref="ReadOnlyCommands"/> skips
/// flags when collecting paths, and the policy then confined the ONE path it could see (<c>.</c>),
/// found it inside the boundary, and returned true. <c>grep -f /etc/shadow .</c> was refused
/// correctly — the identical read, spelled with a space, because the path happened to land in a
/// token of its own. That is not a rule anyone could hold in their head.</para>
///
/// <para>WHY A TYPE RATHER THAN A SIXTH GUARD. Each fix above was correct and none of them made the
/// NEXT one unnecessary, because "did we look at all of it" was never written down — it lived in
/// whether the author remembered. A conjunction of guards answers "nothing I know how to inspect was
/// dangerous"; a caller needs "the whole command was inspected". Those differ exactly when the
/// command contains something new, and the difference is silent.</para>
///
/// <para>SO THE DEFAULT DIRECTION INVERTS. Anything this cannot classify becomes an
/// <see cref="Unexamined"/> entry rather than being skipped, and a caller that requires
/// <see cref="FullyExamined"/> refuses it. A shape nobody anticipated costs one prompt instead of
/// being silently permitted — which is the direction every other fallback in this file already
/// takes (an unresolvable path is outside the boundary; a failed rule subject asks).</para>
///
/// <para>MEASURED BEFORE IT WAS BUILT, because the objection to strictness is friction and friction
/// is an empirical claim. Replaying 13,962 real shell invocations from 248 session logs through the
/// live policy: 30 were silent, and requiring full examination moves 4 of them to asking — 0.03% of
/// all traffic. The four are <c>ls ~/</c>, which reads the user's home directory and is silent today
/// only because nothing expands the tilde. The strict rule costs approximately nothing here because
/// 95.6% of real commands never reach this code at all: they carry a metacharacter (usually
/// <c>2&gt;&amp;1</c>) and <see cref="ReadOnlyCommands.IsReadOnly(string)"/> refuses them outright.</para>
/// </summary>
public sealed record CommandSubjects
{
    /// <summary>The paths this command names that exist on disk — what a boundary check must
    /// confine. This replaced <c>ReadOnlyCommands.PathArguments</c>, and finds the ones its token
    /// split missed: a quoted path containing a space, and a path carried in a flag value.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>The directory a leading <c>cd</c> moves to, or null. Reported separately because the
    /// caller confines it the same way but the failure reads differently in a prompt.</summary>
    public string? ChangesTo { get; init; }

    /// <summary>
    /// Tokens this could not classify, each with why. NON-EMPTY MEANS REFUSE: the point of the type
    /// is that a caller cannot accidentally ignore these, because reading <see cref="FullyExamined"/>
    /// is easier than reconstructing the check.
    /// </summary>
    public required IReadOnlyList<string> Unexamined { get; init; }

    /// <summary>True when every token was accounted for. The ONLY safe basis for an allow.</summary>
    public bool FullyExamined => Unexamined.Count == 0;

    /// <summary>
    /// Decompose a command into the things a decision must look at.
    ///
    /// <para>NEVER THROWS, and an input it cannot parse yields an <see cref="Unexamined"/> entry
    /// rather than an exception or an empty result — an empty result would read as "nothing to
    /// check", which is precisely the failure this type is named after.</para>
    /// </summary>
    public static CommandSubjects Of(string? command)
    {
        var paths = new List<string>();
        var unexamined = new List<string>();

        if (string.IsNullOrWhiteSpace(command))
        {
            // NOT "nothing to check". An empty command is a command we cannot vouch for.
            unexamined.Add("empty command");
            return new CommandSubjects { Paths = paths, Unexamined = unexamined };
        }

        var text = command.Trim();
        string? changesTo = null;

        // The one chain shape already parsed and boundary-checked elsewhere. Reusing it adds no
        // parsing that was not already trusted — see ReadOnlyCommands.CommandAfterCd.
        if (ReadOnlyCommands.CommandAfterCd(text) is { } inner)
        {
            ReadOnlyCommands.IsReadOnly(text, out changesTo);
            text = inner;
        }

        var tokens = Tokenize(text, unexamined);

        // THE VERB IS NOT A SUBJECT — a caller vouches for it separately, via SafeVerbs — but it is
        // also not nothing: skipping it silently is how "the verb is safe" came to stand in for "the
        // command is safe" in the first place.
        foreach (var entry in tokens.Skip(1))
        {
            var token = entry.Text;
            if (token.Length == 0) continue;

            // A TILDE IS A PATH THIS DOES NOT RESOLVE. `ls ~/` was silent in a trusted folder for
            // exactly this reason: the token names the user's home, and nothing here expanded it, so
            // no path was collected and the confinement check passed on an empty list. Expanding it
            // would be the other tempting fix and it is worse — it puts a second path resolver in
            // the system, which is how the boundary and the prompt come to disagree.
            if (token.StartsWith('~'))
            {
                unexamined.Add($"tilde: {token}");
                continue;
            }

            if (token.StartsWith('-'))
            {
                // A FLAG CARRYING ITS VALUE. `--file=/etc/shadow` is a path wearing a flag's clothes;
                // this is bug five, found by writing this type. The value is examined as a path so
                // that `--file=x` and `-f x` — the same read, two spellings — get the same answer.
                var eq = token.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    var value = token[(eq + 1)..].Trim('"', '\'');

                    // A GLOB IN A FLAG VALUE IS A FILTER, NOT A TARGET. `--include=*.cs` narrows what
                    // grep looks at within the paths it was already given, and those paths are
                    // confined separately — so it cannot name a file outside them however it expands.
                    // This is the one place a glob is not an unenumerated read, and it is worth the
                    // exception: 30 invocations of `grep -rn … --include=*.cs` in the measured corpus,
                    // the idiom for searching a codebase by file type.
                    var isFilter = value.Contains('*', StringComparison.Ordinal)
                                || value.Contains('?', StringComparison.Ordinal);

                    if (value.Length > 0 && !isFilter) Classify(value, paths, unexamined);
                }

                // A BARE FLAG IS NOT A SUBJECT. It selects behaviour within a verb already vouched
                // for, and treating flags as paths would confine `-la` to the folder.
                continue;
            }

            // A GLOB NAMES A SET NOBODY ENUMERATED. `cat *.pem` reads whatever matches at run time,
            // which is not knowable from the string — the classic "checked one thing, ran another".
            //
            // AFTER THE FLAG BRANCH, because a flag decides what its own value means: `--include=*.cs`
            // is a filter over paths given elsewhere, while a BARE `*.pem` is the read target itself.
            // Checking globs first made those identical and refused the codebase-search idiom.
            //
            // UNQUOTED ONLY, because that is when the shell expands it. `grep '[A-Z]+' f.cs` hands
            // those characters to grep as a regex and reads exactly the file named after it; treating
            // it as a path set refused 43 ordinary searches in the measured corpus. The quote is the
            // difference between a pattern and a path, and it is the shell's own rule, not ours.
            if (!entry.Quoted
                && (token.Contains('*', StringComparison.Ordinal)
                    || token.Contains('?', StringComparison.Ordinal)
                    || token.Contains('[', StringComparison.Ordinal)))
            {
                unexamined.Add($"glob: {token}");
                continue;
            }

            Classify(token, paths, unexamined);
        }

        return new CommandSubjects { Paths = paths, ChangesTo = changesTo, Unexamined = unexamined };
    }

    /// <summary>
    /// A token that is not a flag is either a path that exists, or something that reads nothing.
    ///
    /// <para>EXISTING PATHS ONLY, kept from the <c>PathArguments</c> this replaced and worth
    /// restating because it looks like a hole and is not: a path that does not exist reads nothing,
    /// so there is nothing to confine. Returning every token would make <c>grep TODO .</c> refuse
    /// for want of a file named TODO — friction with no safety behind it, and friction is what makes
    /// a gate get routed around.</para>
    /// </summary>
    private static void Classify(string token, List<string> paths, List<string> unexamined)
    {
        try
        {
            if (File.Exists(token) || Directory.Exists(token)) paths.Add(token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // COULD NOT TELL. A token we cannot even test is the definition of unexamined, and the
            // whole point of this type is that it says so instead of falling through as "not a path".
            unexamined.Add($"unreadable: {token}");
        }
    }

    /// <summary>
    /// Split on spaces, honouring quotes, so a quoted path with a space stays one token.
    ///
    /// <para>THE PLAIN SPLIT MISSED THESE. <c>cat "my notes.txt"</c> became two tokens, neither of
    /// which exists, so both were dropped and the command was confined against an empty path list.
    /// An UNTERMINATED quote is reported rather than guessed at — the shell would join it with
    /// whatever follows, and guessing which is exactly how a checker comes to inspect a different
    /// command than the one that runs.</para>
    /// </summary>
    private static List<Token> Tokenize(string text, List<string> unexamined)
    {
        var tokens = new List<Token>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var wasQuoted = false;

        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'') { quote = c; wasQuoted = true; continue; }

            if (c == ' ')
            {
                if (current.Length > 0 || wasQuoted)
                {
                    tokens.Add(new Token(current.ToString(), wasQuoted));
                    current.Clear();
                    wasQuoted = false;
                }
                continue;
            }

            current.Append(c);
        }

        if (quote != '\0') unexamined.Add("unterminated quote");
        if (current.Length > 0 || wasQuoted) tokens.Add(new Token(current.ToString(), wasQuoted));

        return tokens;
    }

    /// <summary>
    /// A token and whether the user quoted it.
    ///
    /// <para>QUOTING IS SECURITY-RELEVANT, not cosmetic: the shell expands a glob only when it is
    /// UNquoted, so <c>grep '[A-Z]+' f.cs</c> passes those five characters to grep as a regex while
    /// <c>cat [ab].txt</c> is a file set the shell resolves at run time. Losing the distinction made
    /// every bracketed grep pattern look like an unenumerated path set — measured on the real
    /// corpus, 43 invocations of ordinary searches, which is the friction that gets a gate routed
    /// around.</para>
    /// </summary>
    private readonly record struct Token(string Text, bool Quoted);
}
