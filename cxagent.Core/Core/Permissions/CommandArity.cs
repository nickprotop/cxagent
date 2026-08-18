namespace CxAgent.Core.Permissions;

/// <summary>
/// How many words name a command, as opposed to naming its arguments.
///
/// <para>WHY THIS EXISTS. A shell rule used to be the whole command string, so
/// <c>find Services -type f</c> and <c>find . -type f</c> were unrelated grants — and an agent
/// exploring a codebase never repeats a command verbatim. Measured: 111 stored rules, essentially
/// none of which could ever match again. They were not a permission system, they were a log of what
/// had once been approved.</para>
///
/// <para>THE OBVIOUS FIX IS THE DANGEROUS ONE. Granting the first word turns "allow <c>git
/// status</c>" into "allow <c>git push --force</c>", which is the exact hole that makes
/// prefix-granting unsafe. What makes it safe is knowing that <c>git</c> takes a SUBCOMMAND: two
/// words name that command, so the rule is <c>git status *</c> and pushing still asks.</para>
///
/// <para>FLAGS ARE NEVER PART OF THE NAME. <c>npm --silent install</c> is still <c>npm install</c>,
/// and a table that counted words positionally would be defeated by moving a flag.</para>
///
/// <para>The shape — a prefix table, longest match wins, arity counts subcommands only — is
/// opencode's, whose own table is ~95 entries generated and hand-checked. Ours is smaller and
/// deliberately conservative: an unknown program gets arity 1, which is the SAFEST answer for a
/// tool nobody has classified, because it grants only that program with no arguments generalised.
/// </para>
/// </summary>
public static class CommandArity
{
    /// <summary>
    /// Programs whose first word alone names them. Everything not listed here also lands on 1 — this
    /// exists to say so out loud for the common cases, and to be the place a reader looks first.
    /// </summary>
    private const int JustTheProgram = 1;

    /// <summary>
    /// Prefix → how many words name the command.
    ///
    /// <para>ARITY 2 IS THE INTERESTING CASE: a program whose real identity is its subcommand.
    /// <c>git</c>, <c>docker</c>, <c>npm</c>, <c>dotnet</c> and <c>kubectl</c> all do wildly
    /// different things depending on the second word, and granting the first alone would grant the
    /// lot.</para>
    ///
    /// <para>ARITY 3 IS FOR SUBCOMMANDS THAT THEMSELVES TAKE ONE. <c>npm run</c> is not a command;
    /// <c>npm run build</c> is. Listed individually because rule 4 of the shape says only add a
    /// longer prefix when its arity DIFFERS from what the shorter one implies.</para>
    /// </summary>
    private static readonly Dictionary<string, int> Arity = new(StringComparer.Ordinal)
    {
        // Version control — the canonical case for why arity 1 is wrong.
        ["git"] = 2,
        ["hg"] = 2,
        ["svn"] = 2,

        // .NET, and the one this project uses constantly.
        ["dotnet"] = 2,
        ["dotnet tool"] = 3,
        ["dotnet nuget"] = 3,

        // JavaScript.
        ["npm"] = 2,
        ["npm run"] = 3,
        ["npx"] = 2,
        ["yarn"] = 2,
        ["pnpm"] = 2,
        ["pnpm run"] = 3,
        ["bun"] = 2,
        ["bun run"] = 3,
        ["deno"] = 2,
        ["deno task"] = 3,

        // Other toolchains.
        ["cargo"] = 2,
        ["go"] = 2,
        ["mvn"] = 2,
        ["gradle"] = 2,
        ["make"] = 2,
        ["pip"] = 2,
        ["pip3"] = 2,
        ["python"] = 2,
        ["python3"] = 2,
        ["poetry"] = 2,
        ["uv"] = 2,
        ["composer"] = 2,
        ["gem"] = 2,
        ["bundle"] = 2,

        // Containers and clusters — where a wrong grant is most expensive.
        ["docker"] = 2,
        ["docker compose"] = 3,
        ["podman"] = 2,
        ["kubectl"] = 2,
        ["helm"] = 2,

        // Cloud CLIs, all of which nest at least two deep.
        ["aws"] = 3,
        ["az"] = 3,
        ["gcloud"] = 3,

        // Package managers that install things.
        ["apt"] = 2,
        ["apt-get"] = 2,
        ["brew"] = 2,
        ["dnf"] = 2,
        ["pacman"] = 2,
        ["snap"] = 2,

        // Ours, and the family's.
        ["cxagent"] = 2,
        ["systemctl"] = 2,
        ["journalctl"] = JustTheProgram,
    };

    /// <summary>
    /// The words that NAME this command — its prefix — with flags and arguments dropped.
    /// </summary>
    /// <returns>
    /// An empty list when there is nothing to name, which the caller must treat as "no rule can be
    /// written". Never throws.
    /// </returns>
    public static IReadOnlyList<string> Prefix(string? command)
    {
        var tokens = Tokens(command);
        if (tokens.Count == 0) return [];

        // LONGEST MATCH WINS, so `npm run` beats `npm`. Walking down from the full length rather
        // than up means the most specific classification is found first, and a program whose
        // subcommand needs its own arity gets it without the shorter entry having to know.
        for (var len = tokens.Count; len > 0; len--)
        {
            var candidate = string.Join(' ', tokens.Take(len));
            if (Arity.TryGetValue(candidate, out var arity))
                return tokens.Take(Math.Min(arity, tokens.Count)).ToList();
        }

        // UNKNOWN PROGRAMS GET THE PROGRAM ONLY. That is the conservative answer: it generalises
        // over arguments, which is the point, while granting nothing about a program the table has
        // never been asked to classify.
        return tokens.Take(JustTheProgram).ToList();
    }

    /// <summary>
    /// Whether this is several commands rather than one.
    ///
    /// <para>THE SAME CHARACTERS <c>ReadOnlyCommands</c> CALLS DANGEROUS, and for the same reason:
    /// what follows one of them is a command this check never looked at. There it decides whether to
    /// ask; here it decides whether an answer can be remembered.</para>
    /// </summary>
    public static bool IsChain(string? command) =>
        command is not null && command.AsSpan().IndexOfAny(ChainOperators) >= 0;

    private static readonly char[] ChainOperators = ['&', ';', '|', '\n', '\r'];

    /// <summary>
    /// The rule a grant should store — the prefix plus a wildcard, or null when none can be written.
    /// </summary>
    /// <remarks>
    /// The pattern form is <c>PermissionRulesStore</c>'s existing trailing-<c>*</c> prefix match, so
    /// nothing about matching changes; only what gets written does.
    /// </remarks>
    public static string? RuleFor(string? command)
    {
        // A CHAIN CANNOT BE GRANTED, because there is no honest rule to write for it. Prefix() reads
        // the first word or two and knows nothing about `&&`, `;` or `|`, so `cd /repo && dotnet
        // test` produced the rule `cd*` — and the store matches a pattern by RAW PREFIX, so that
        // rule then permits `cd /tmp && rm -rf ~`. Since almost any command can be written
        // `cd . && <anything>`, one grant became arbitrary shell.
        //
        // MEASURED, NOT THEORISED: a live drive asked seven times, six of them for shell shapes, and
        // pressing Always on one of those chains is exactly what a user under that much friction
        // does. The prompt said "Always allow covers: cd*", which reads as "cd commands" and means
        // "anything whose text starts with cd".
        //
        // NULL, LIKE THE ENV CASE ABOVE IT. The action can still be allowed once; what it cannot do
        // is become a standing rule. Narrowing the pattern instead — quoting, escaping, matching
        // only up to the operator — would be inventing a shell parser inside a permission check, and
        // a parser that is wrong once is a hole that looks like a guard.
        // THE `cd X && one-command` IDIOM GETS A RULE FOR THE COMMAND IT RUNS, and only that idiom.
        // It is the shape the model writes constantly, and refusing it outright is what made a user
        // reach for `cd*` — a grant that then permitted `cd /tmp && rm -rf ~`. The honest rule here
        // is `dotnet test*`: scoped to the folder, no operators, exactly the command being run.
        //
        // SAFE BECAUSE IT IS NOT NEW PARSING. ReadOnlyCommands already strips this form and confines
        // the cd target for the ALLOW decision; CommandAfterCd returns null unless the remainder is a
        // single command with no operators of its own.
        // ONLY WHEN WHAT FOLLOWS CANNOT DESTROY ANYTHING. Stripping the `cd` unconditionally was
        // worse than the problem it solved: `cd /repo && rm -rf build` wrote the rule `rm*`, and one
        // click then made every later `rm -rf` in that folder silent — the source tree included.
        // The path guard in PermissionPolicy keeps such a rule inside the boundary, which is exactly
        // no comfort when the boundary is the project you are working in.
        //
        // SO THE PAIR IS A CONVENIENCE FOR SAFE COMMANDS, NOT A GENERAL UNWRAPPING. `cd X && ls`
        // and `cd X && cat f` earn a rule because a rule for `ls` is a rule to read; `rm`, `mv`,
        // `chmod` and anything else absent from the safe list keep asking, which is what a
        // destructive verb should do however it is spelled.
        //
        // `dotnet test` IS NOT SAFE BY THIS TEST EITHER, and that is the honest cost: it executes a
        // project's own build targets, so a fresh clone running it is running that repo's code. It
        // keeps asking. The friction that led someone to grant `cd*` is real, and the answer to it
        // is not to widen what a click can buy.
        if (ReadOnlyCommands.CommandAfterCd(command) is { } inner
            && ReadOnlyCommands.IsReadOnly(inner, out _))
            command = inner;

        if (IsChain(command)) return null;

        var prefix = Prefix(command);

        // NO SPACE BEFORE THE STAR, and the reason is a real bug this cost. The store's prefix match
        // is `subject.StartsWith(pattern[..^1])`, so `git status *` becomes `git status ` — with a
        // trailing space — which the bare command `git status` does not start with. The user would
        // press Always and be asked the identical question again, for a difference of one invisible
        // character.
        //
        // `git status*` matches `git status` and `git status --short` alike, which is what a grant on
        // a command means. It also matches `git statusfoo`, a command nobody has ever written; the
        // alternative is storing two patterns to express one grant.
        return prefix.Count == 0 ? null : string.Join(' ', prefix) + "*";
    }

    /// <summary>
    /// The command's words, with flags removed.
    ///
    /// <para>FLAGS DROPPED BEFORE COUNTING, so `npm --silent install` still names `npm install`. A
    /// positional count would be defeated by moving a flag, which is a difference the user cannot
    /// see and would not expect to matter.</para>
    ///
    /// <para>A FLAG'S VALUE IS NOT DROPPED, and this is a real limitation rather than an oversight:
    /// knowing that `-C` takes a path while `-v` does not means knowing every program's options.
    /// Measured consequence — <c>git -C /some/path status</c> yields the tokens
    /// <c>git /some/path</c> and so the rule <c>git /some/path *</c>. That rule is useless (nothing
    /// will match it) but it is not UNSAFE: it grants a nonexistent subcommand, and the real
    /// `git status` still asks. Failing toward asking is the direction this whole system fails in,
    /// so a useless rule is an acceptable price for not embedding an options database.</para>
    ///
    /// <para>A TOKEN THAT LOOKS LIKE A PATH ENDS THE NAME. It cannot be a subcommand — no program
    /// names one with a slash — so treating it as one is how the case above produces its junk rule.
    /// Stopping there keeps the prefix to the words that really name the command.</para>
    /// </summary>
    private static List<string> Tokens(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];

        var tokens = new List<string>();
        foreach (var token in command.Split(' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith('-')) continue;

            // A path or a file is an ARGUMENT, and everything after it is too. `git /some/path`
            // would otherwise be stored as though `/some/path` were a subcommand.
            if (tokens.Count > 0 && (token.Contains('/') || token.Contains('\\') || token.Contains('.')))
                break;

            tokens.Add(token);
        }

        return tokens;
    }
}
