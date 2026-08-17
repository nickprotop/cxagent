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
public static class ReadOnlyCommands
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
    /// <para>A PIPE IS NOT ALLOWED EITHER, even though `grep | wc` is harmless: the right-hand side
    /// is another command, and checking it properly means parsing a shell. `|` is common enough in
    /// exploration that allowing it would help — and it is precisely the case where being wrong is
    /// silent, so it stays out until there is a real parser.</para>
    /// </summary>
    private static readonly char[] Dangerous = ['&', ';', '|', '>', '<', '`', '\n', '\r'];

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
    public static bool IsReadOnly(string? command, out string? changesTo)
    {
        changesTo = null;
        if (string.IsNullOrWhiteSpace(command)) return false;

        var text = command.Trim();

        // `cd <somewhere> && <the real command>` IS THE DOMINANT IDIOM, and it defeated everything:
        // the `&&` disqualified the whole line, so `cd /repo && ls` prompted while a bare `ls` did
        // not. Measured across two agentic drives — two of three prompts were this shape, and the
        // model writes it constantly because it cannot rely on the working directory.
        //
        // STRIPPED, NOT IGNORED. The target is handed back so the caller can require it to be inside
        // the trusted folder: `cd /etc && cat shadow` reads a file the boundary exists to protect,
        // and the `cat` half looks perfectly innocent on its own.
        //
        // ONE `cd` ONLY, and only at the start. A second one later in the line is a chain doing
        // something this cannot reason about, and the `&&` check below still refuses it.
        if (TryStripLeadingCd(text, out var target, out var rest))
        {
            changesTo = target;
            text = rest;

            // `cd /somewhere` with nothing after it. It changes no files and reads nothing, so it is
            // read-only by any measure — but the caller still has to accept the DIRECTORY, which is
            // the whole reason the target is reported rather than swallowed.
            if (text.Length == 0) return true;
        }

        if (text.IndexOfAny(Dangerous) >= 0) return false;

        // $( ) — command substitution runs a program whose name never appears in this string.
        if (text.Contains("$(", StringComparison.Ordinal)) return false;

        // A LEADING ASSIGNMENT IS A DIFFERENT PROGRAM. `PATH=/tmp/evil ls` runs whatever /tmp/evil
        // calls ls, so the verb we would check is not the binary that runs.
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || first.Contains('=', StringComparison.Ordinal)) return false;

        // A PATH, NOT A NAME, IS NOT ON THE LIST. `/tmp/ls` and `./ls` are not the `ls` this list
        // means — the list names programs found on PATH, and anything spelled as a path is a binary
        // the user has not vouched for.
        if (first.Contains('/', StringComparison.Ordinal)) return false;

        return SafeVerbs.Contains(first);
    }

    /// <summary>
    /// The paths a read-only command names, for a caller that must confine them.
    ///
    /// <para>THE VERB IS NOT THE WHOLE REQUEST. <see cref="IsReadOnly"/> answers "does this program
    /// write" and nothing about WHAT it reads, so <c>cat /etc/shadow</c> passed it — the same file
    /// the file-read tool refuses, because that path resolves outside the boundary. Two spellings of
    /// one read, opposite answers, and the permissive one is the less inspectable.</para>
    ///
    /// <para>THE CALLER ALREADY DOES THIS FOR <c>cd</c>. IsReadOnly hands back the cd target
    /// precisely so the boundary can bind to it — the comment there says <c>cd /etc &amp;&amp; cat
    /// shadow</c> "reads a file the boundary exists to protect". This is that same reasoning applied
    /// to the arguments, which is where the gap was.</para>
    ///
    /// <para>EXISTING PATHS ONLY. A flag is not a path, and neither is a grep pattern. Returning
    /// every token would make the caller refuse <c>grep TODO</c> for want of a file called TODO;
    /// returning only what exists on disk asks the question that matters — is the agent reading
    /// something real, and is it inside the folder. A path that does not exist reads nothing, so
    /// there is nothing to confine.</para>
    /// </summary>
    public static IReadOnlyList<string> PathArguments(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];

        var text = command.Trim();
        if (TryStripLeadingCd(text, out _, out var rest)) text = rest;

        var paths = new List<string>();

        // FROM THE SECOND TOKEN: the first is the verb, which SafeVerbs has already vouched for and
        // which names a program rather than a file to read.
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            // A FLAG IS NOT A PATH. Treating one as a path would confine flags to the folder.
            if (token.StartsWith('-')) continue;

            var trimmed = token.Trim('"', '\'');
            if (trimmed.Length > 0 && (File.Exists(trimmed) || Directory.Exists(trimmed)))
                paths.Add(trimmed);
        }

        return paths;
    }

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
