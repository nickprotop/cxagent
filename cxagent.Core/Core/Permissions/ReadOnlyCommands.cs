namespace CxAgent.Core.Permissions;

/// <summary>
/// Whether a shell command can only LOOK.
///
/// <para>WHY THIS EXISTS. A shell rule is the exact command string, so `find . -type f` and
/// `find . -name '*.cs'` are unrelated grants — and an agent exploring a codebase never repeats a
/// command verbatim. Measured on a real drive: thirteen shell calls in one turn, thirteen prompts,
/// and the run only finished because approvals were automated. A gate noisy enough to be routed
/// around is worse than a coarser one that is kept.</para>
///
/// <para>THE PRECEDENT IS ALREADY HERE. A file READ inside a trusted folder does not prompt, on the
/// reasoning that reading what you can already read costs nothing. `ls`, `grep` and `cat` are that
/// same read expressed as a command; refusing them the same treatment is a distinction the user
/// cannot see the point of — and it is why our own tools kept losing to `run_shell`, since the
/// model reaches for the verb it knows and the gate then charges the user for it.</para>
///
/// <para>THE ANSWER IS A LIST, NOT AN ANALYSIS. Deciding what an arbitrary command does is
/// undecidable, so this does not try: a command qualifies only if its verb is on a short list of
/// programs that cannot write, and its text contains nothing that could turn it into something
/// else. Everything not proven safe prompts, which is the direction to fail in.</para>
/// </summary>
internal static partial class ReadOnlyCommands
{
    /// <summary>
    /// Programs that read and print, and cannot modify anything.
    ///
    /// <para>EVERY ENTRY EARNS ITS PLACE, and the exclusions matter more than the inclusions. No
    /// `sed` or `awk` — both write files given the right flag (`sed -i`). No `sort` — `-o` writes.
    /// No `find`: `-delete` and `-exec` make it a general executor, and it is the one command here
    /// that most looks read-only and is not. No `git` at all, because `git status` and
    /// `git push --force` share a verb and the subcommand is an argument, which is exactly the
    /// granularity mistake this design is avoiding.</para>
    /// </summary>
    private static readonly HashSet<string> SafeVerbs = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "wc", "file", "stat", "du", "df",
        "grep", "rg", "egrep", "fgrep",
        "pwd", "whoami", "hostname", "date", "uname",
        "which", "type", "basename", "dirname", "realpath", "readlink",
        "echo", "printf", "tree", "diff", "cmp", "md5sum", "sha256sum",
    };

    /// <summary>
    /// Characters that let one command become several, or write somewhere.
    ///
    /// <para>THIS IS THE REAL GUARD. The verb list is worthless without it: `cat x; rm -rf /` starts
    /// with a safe verb, and `grep foo > /etc/passwd` writes with one. Anything that chains,
    /// substitutes or redirects disqualifies the whole command — including backticks and `$(`, which
    /// run a program whose name we never see.</para>
    ///
    /// <para>A PIPE IS NOT ON THIS LIST because it is checked rather than refused: see
    /// <see cref="IsPipeOfReads"/>. Everything here still disqualifies the whole line, and `||` and `|&amp;`
    /// with it — the first leaves an empty stage, the second is caught by its `&amp;`.</para>
    /// </summary>

    /// <summary>
    /// True when this command reads and nothing more.
    /// </summary>
    /// <remarks>
    /// Never throws, and answers false for anything it cannot parse confidently. A wrong `false`
    /// costs one prompt; a wrong `true` runs something nobody approved.
    /// </remarks>
    public static bool IsReadOnly(string? command) => IsReadOnly(command, out _);

    /// <summary>
    /// True when this command reads and nothing more, reporting the directory it first changes to.
    /// </summary>
    /// <param name="changesTo">
    /// The target of a leading <c>cd</c>, or null when there is none. The CALLER decides whether that
    /// directory is acceptable — this class knows nothing about a working boundary, and guessing
    /// would put a security decision in a string helper.
    /// </param>
    /// <remarks>
    /// Never throws, and answers false for anything it cannot parse confidently. A wrong `false`
    /// costs one prompt; a wrong `true` runs something nobody approved.
    /// </remarks>
    /// <param name="command">The shell command to classify.</param>
    public static bool IsReadOnly(string? command, out string? changesTo)
    {
        changesTo = null;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var text = command.Trim();

        // $( ) AND BACKTICKS run a program whose name never appears in this string — anywhere on the line, since a
        // substitution can span what looks like two commands.
        if (text.Contains("$(", StringComparison.Ordinal) || text.Contains('`')) return false;

        // A CHAIN OF READS IS A READ (user, 2026-11-11). The line is split as the shell splits it — at operators outside
        // quotes, redirects lifted out (CommandSubjects.Segments) — and EVERY command in it must be a read on its own: a
        // colleague's `cd X && echo … && ls … | grep …` asked every time while each piece was on this list. One command
        // not on it, and the whole line asks.
        // AN OPERATOR AT EITHER END IS A LINE NOT FINISHED — `ls |` waits for more, `| wc` has nothing feeding it — and the
        // splitter below drops the empty piece, so it is refused here, before it can read as a complete chain.
        if (text[0] is '|' or '&' or ';' || text[^1] is '|' or '&' or ';' || text.EndsWith("\\", StringComparison.Ordinal))
            return false;
        if (CommandSubjects.Segments(text) is not { } segments || segments.Count == 0) return false;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            // IN THE BACKGROUND, a command outlives the line — what it does after is not in this text.
            if (segment.Joiner == "&") return false;

            // A REDIRECT WRITES, OR READS A FILE IN; only throwing output away is a read.
            if (segment.Redirects.Any(r => !Discards.Contains(r))) return false;

            var words = Words(segment.Text);
            if (words.Count == 0) continue;

            // `cd <somewhere>` FIRST, AND ONLY FIRST — the dominant idiom: the model cannot rely on the working directory.
            // STRIPPED, NOT IGNORED: the target is handed back so the caller can require it inside the trusted folder —
            // `cd /etc && cat shadow` reads a file the boundary exists to protect, and the `cat` half looks innocent. A
            // second cd later is the shell moving somewhere this does not follow, so it asks.
            if (words[0].Text == "cd")
            {
                if (i != 0 || words.Count != 2 || words[1].Quoted) return false;
                changesTo = words[1].Text;
                continue;
            }

            if (!IsOneRead(segment.Text, words)) return false;
        }
        return true;
    }

    private static bool Follows(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Any(flag =>
            flag.StartsWith("--follow", StringComparison.Ordinal)
            || flag.StartsWith('-') && !flag.StartsWith("--", StringComparison.Ordinal)
               && flag.AsSpan(1).IndexOfAny('f', 'F') >= 0);

    // WHERE OUTPUT IS THROWN AWAY OR HANDED BACK — the redirects a read may carry.
    private static readonly HashSet<string> Discards = new(StringComparer.Ordinal) { "/dev/null", "/dev/stdout", "/dev/stderr" };

    /// <summary>Whether one command, alone, only reads.</summary>
    private static bool IsOneRead(string text, List<Word> words)
    {
        var first = words[0].Text;

        // A LEADING ASSIGNMENT IS A DIFFERENT PROGRAM. `PATH=/tmp/evil ls` runs whatever /tmp/evil
        // calls ls, so the verb we would check is not the binary that runs.
        if (first.Contains('=', StringComparison.Ordinal)) return false;

        // A PATH, NOT A NAME, IS NOT ON THE LIST. `/tmp/ls` and `./ls` are not the `ls` this list
        // means — the list names programs found on PATH, and anything spelled as a path is a binary
        // the user has not vouched for.
        if (first.Contains('/', StringComparison.Ordinal)) return false;

        // A FOLLOW DOES NOT END. `tail -f` reads one file and never returns, so it runs until the call gives up
        // on it — and a command left running after its call is not one its text can vouch for.
        if (first == "tail" && Follows(text)) return false;

        return SafeVerbs.Contains(first) || (first == "sed" && PrintsLines(words));
    }

    /// <summary>
    /// `sed` that only prints lines: `-n` and a script naming a line or a range and `p` — `'1,50p'`, `'$p'`. NOTHING ELSE
    /// OF sed: `-i` edits the file in place, `w` writes one, `e` runs a command, and a clustered `-ni` is `-i` too.
    /// </summary>
    private static bool PrintsLines(List<Word> words)
    {
        var flags = words.Skip(1).Where(w => !w.Quoted && w.Text.StartsWith('-')).Select(w => w.Text).ToList();
        if (!flags.Contains("-n") && !flags.Contains("--quiet") && !flags.Contains("--silent")) return false;
        if (flags.Any(f => f.StartsWith("--in-place", StringComparison.Ordinal)
                           || (!f.StartsWith("--", StringComparison.Ordinal) && f.AsSpan(1).Contains('i')))) return false;
        if (flags.Any(f => f is not ("-n" or "--quiet" or "--silent"))) return false;
        var script = words.Skip(1).FirstOrDefault(w => w.Quoted || !w.Text.StartsWith('-'));
        return script is not null && LineRange().IsMatch(script.Text);
    }

    // ONE RANGE AND `p`, OR SEVERAL JOINED BY `;` — `'330,350p;415,445p'` prints two stretches of one file.
    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+|\$)(,(\d+|\$))?p(;(\d+|\$)(,(\d+|\$))?p)*$")]
    private static partial System.Text.RegularExpressions.Regex LineRange();

    /// <summary>A command's words, quotes removed, each with whether it was quoted.</summary>
    private sealed record Word(string Text, bool Quoted);

    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var quoted = false;
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; quoted = true; continue; }
            if (c == ' ')
            {
                if (current.Length > 0 || quoted) words.Add(new Word(current.ToString(), quoted));
                current.Clear();
                quoted = false;
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0 || quoted) words.Add(new Word(current.ToString(), quoted));
        return words;
    }

    // PathArguments LIVED HERE AND IS NOW CommandSubjects.Of(...).Paths. It answered "which tokens
    // name a real file", which is most of the question but not all of it: it split on spaces, so a
    // quoted path containing one was dropped, and it skipped every token starting with a dash, so
    // `--file=/etc/shadow` was invisible while `-f /etc/shadow` was caught. Two spellings of one
    // read, opposite answers.
    //
    // THE REPLACEMENT IS A TYPE BECAUSE THE ANSWER NEEDED A SECOND HALF: not just the paths found,
    // but whether anything was left unaccounted for. A list cannot say "and there was something here
    // I could not classify", so every caller had to remember to ask separately — which is how four
    // holes of the same shape reached this file. Deleted rather than kept as a wrapper: a second way
    // to ask the question is how the two doors came to be fixed separately last time.

    /// <summary>
    /// Splits <c>cd &lt;dir&gt; &amp;&amp; rest</c> into its target and the rest, or reports no match.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY STRICT. It matches only `cd`, one unquoted target, and at most one `&amp;&amp;` —
    /// anything else is left whole for the metacharacter check to refuse. That is the safe direction:
    /// a shape this does not recognise costs one prompt, while a shape it mis-parses hands the caller
    /// a target that is not the one that will actually be entered.
    /// </remarks>
    /// <summary>
    /// The command a <c>cd X &amp;&amp; …</c> line actually runs, or null when it is not that shape.
    ///
    /// <para>SO A GRANT CAN BE WRITTEN FOR IT. `cd /repo &amp;&amp; dotnet test` is the idiom the
    /// model writes constantly — it cannot rely on the working directory — and CommandArity refuses
    /// to build a rule from a chain, correctly, since `cd*` was permitting `cd /tmp &amp;&amp; rm -rf
    /// ~`. But that left the idiom asking every single time, and a drive that asks five times for
    /// `dotnet test` is a drive where somebody grants something broader to make it stop. That is not
    /// hypothetical: it is how `cd*` came to be granted here.</para>
    ///
    /// <para>NOT A SHELL PARSER, which is what CommandArity's refusal is guarding against. This is
    /// the one form already stripped and boundary-checked for the ALLOW decision — the cd target is
    /// confined, the remainder is a single command with no operators of its own. Reusing it for the
    /// RULE decision adds no parsing that was not already trusted; a rule written from it says
    /// `dotnet test*`, which is the honest grant for the command being run.</para>
    /// </summary>
    public static string? CommandAfterCd(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (!TryStripLeadingCd(command.Trim(), out _, out var rest)) return null;

        // A REMAINDER THAT IS ITSELF A CHAIN IS STILL A CHAIN. `cd /x && a && b` strips to `a && b`,
        // which no more deserves a rule than the original did.
        return rest.Length > 0 && !rest.AsSpan().ContainsAny(ChainOperators) ? rest : null;
    }

    /// <summary>The operators that make a line several commands — see <c>CommandArity.IsChain</c>,
    /// which uses the same set for the same reason.</summary>
    private static readonly System.Buffers.SearchValues<char> ChainOperators =
        System.Buffers.SearchValues.Create("&;|\n\r");

    private static bool TryStripLeadingCd(string text, out string? target, out string rest)
    {
        target = null;
        rest = text;

        if (!text.StartsWith("cd ", StringComparison.Ordinal)) return false;

        // A QUOTED TARGET IS REFUSED, not unquoted here. `cd "a b" && ls` is legitimate and rare, and
        // unquoting correctly means handling escapes — one more place to be subtly wrong about what
        // directory is really being entered.
        if (text.Contains('"') || text.Contains('\'')) return false;

        var after = text[3..].TrimStart();
        if (after.Length == 0) return false;

        var separator = after.IndexOf("&&", StringComparison.Ordinal);

        // `cd somewhere` on its own — the whole command.
        if (separator < 0)
        {
            // Still one token: `cd a b` is not a directory change this can reason about.
            if (after.Contains(' ')) return false;
            target = after;
            rest = string.Empty;
            return true;
        }

        var dir = after[..separator].Trim();
        if (dir.Length == 0 || dir.Contains(' ')) return false;

        // ONE `cd` AND ONE `&&`. A second `&&` means a longer chain, and the command after it is not
        // something this has looked at — the metacharacter check refuses the remainder anyway, but
        // refusing here keeps the reason legible.
        var remainder = after[(separator + 2)..].Trim();
        if (remainder.Length == 0) return false;

        target = dir;
        rest = remainder;
        return true;
    }
}
