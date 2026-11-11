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
internal sealed record CommandSubjects
{
    /// <summary>The paths this command names that exist on disk — what a boundary check must
    /// confine. This replaced <c>ReadOnlyCommands.PathArguments</c>, and finds the ones its token
    /// split missed: a quoted path containing a space, and a path carried in a flag value.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>The tokens <see cref="Classify"/> found to be paths, as the command spells them — what a reader is
    /// shown. <see cref="Paths"/> is what a boundary is held to.</summary>
    public IReadOnlyList<string> Tokens { get; init; } = [];

    /// <summary>The folder the command reads its paths in: where it runs, or past a leading <c>cd</c>, where that
    /// went — null when the shell decides it (<see cref="CdShellDecides"/>) or it cannot be resolved.</summary>
    public string? After { get; init; }

    /// <summary>Whether a leading <c>cd</c>'s target is the shell's to decide: <c>-</c>, <c>~</c>, a variable.</summary>
    public bool CdShellDecides { get; init; }


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
    /// <param name="command">The command line.</param>
    /// <param name="from">The folder it runs in — where its relative paths are, before any leading cd. The process
    /// directory when not given, for a caller judging a command with no folder of its own.</param>
    public static CommandSubjects Of(string? command, PathResolver? from = null)
    {
        from ??= new PathResolver(PathResolver.FolderOrProcess(null));
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

        // READ AS THE SHELL READS IT: commands split at the operators outside quotes, each redirect's target lifted out.
        // A line this cannot read — an open quote, a here-document — is not guessed at.
        if (Segments(text) is not { } segments)
        {
            unexamined.Add("unreadable: an unterminated quote or a here-document");
            return new CommandSubjects { Paths = paths, Unexamined = unexamined };
        }

        // A LEADING `cd X &&` MOVES EVERY COMMAND AFTER IT, so their paths are read from X — read from where the line
        // starts, `cd src/sub && cat ../b.cs` named a file above the project when it names one beside it.
        //
        // ONLY THAT SHAPE, AND ONLY WITH NO OTHER cd ON THE LINE. Not followed, a path is read from where the line starts,
        // which is never more lenient than the folder it reaches: every cd target is itself a path held to the
        // boundary, so the line stays inside, and a `..` read from the start climbs further than from deeper in. Followed
        // past a second cd, `cd src/sub && cd ../.. && cat ../x` would read ../x from src/sub — inside — while the
        // shell reads it from the root — outside. After `;` or `||` the first cd may not have happened at all.
        var rest = segments;
        if (FollowedCd(segments) is { } target)
        {
            changesTo = target;
            rest = segments.Skip(1).ToList();
        }

        // WHERE ITS PATHS ARE READ: the run folder, or past the cd, where that goes. A target the shell decides
        // leaves nothing to read them in, and that is itself a thing not examined.
        var cd = from.AsShellCd(changesTo);
        var after = cd.Folder;
        if (cd.ShellDecides) unexamined.Add($"cd target the shell decides: {changesTo}");
        var tokensFound = new List<string>();
        var into = new ClassifyInto(after ?? from.Folder, paths, tokensFound, unexamined);

        foreach (var segment in rest)
        {
            var tokens = Tokenize(segment.Text, unexamined);

            // AN EXPANSION NAMES A PATH NOBODY READ, the verb included. `cat $HOME/.ssh/id_rsa` is the user's key
            // by the time the shell runs it, and nothing here can say where `$HOME`, `$OLDPWD` or `$1` lands —
            // the tilde's refusal below, for the same reason, and expanding them here would be a second path
            // resolver beside the shell's.
            foreach (var entry in tokens.Where(t => t.Expands))
                unexamined.Add($"expansion: {entry.Text}");

            // A WALK THAT FOLLOWS LINKS reads wherever they point. `grep -R x .` names one folder and searches
            // every folder a link under it reaches, so confining the folder it names confines nothing.
            if (tokens.Count > 0 && FollowsLinks(tokens))
                unexamined.Add($"follows links: {string.Join(' ', tokens.Select(t => t.Text))}");

            // THE VERB IS NOT A SUBJECT — a caller vouches for it separately, via SafeVerbs — but it is
            // also not nothing: skipping it silently is how "the verb is safe" came to stand in for "the
            // command is safe" in the first place. Each command of the line has its own.
            foreach (var entry in tokens.Skip(1))
                Argument(entry, into);

            foreach (var written in segment.Redirects)
                Redirect(written, into);
        }

        // AND EVERY TOKEN THAT ASSERTS A LOCATION, existing or not — see ReferencedPaths.
        if (after is not null)
            foreach (var referenced in ReferencedPaths(rest, after))
                if (!paths.Contains(referenced)) paths.Add(referenced);

        return new CommandSubjects
        {
            Paths = paths, Tokens = tokensFound, ChangesTo = changesTo, After = after, CdShellDecides = cd.ShellDecides,
            Unexamined = unexamined,
        };
    }

    /// <summary>
    /// One argument of a command: a flag, a flag carrying a path, a glob, a tilde, or a word that may name a path.
    /// </summary>
    private static void Argument(Token entry, ClassifyInto into)
    {
        var token = entry.Text;
        if (token.Length == 0) return;

        // A TILDE IS THE HOME FOLDER, and only a plain one is resolved here. `ls ~/` was once silent in a trusted folder
        // because the token was collected as no path at all, and the confinement check passed on an empty list; now
        // `~` and `~/…` become the folder they name and are held to the boundary like any path, so `ls ~/` still asks
        // while `cat ~/source/proj/x` inside that project does not. `~user` is another user's home, the shell's to find.
        if (token.StartsWith('~') && !entry.Quoted)
        {
            // HELD WHETHER OR NOT IT EXISTS, as a relative path is (see ReferencedPaths): `~/in.txt` asserts a place, and
            // "nothing there yet" is not a reason to stop asking about a read of the user's home.
            if (Tilde(token) is { } home)
            {
                into.Paths.Add(Path.GetFullPath(home));
                into.Tokens.Add(token);
            }
            else into.Unexamined.Add($"tilde: {token}");
            return;
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

                if (value.Length > 0 && !isFilter) Classify(value, into);
            }

            // A BARE FLAG IS NOT A SUBJECT. It selects behaviour within a verb already vouched
            // for, and treating flags as paths would confine `-la` to the folder.
            return;
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
            Glob(token, into);
            return;
        }

        Classify(token, into);
    }

    /// <summary>The home folder a plain tilde names; a test sets it.</summary>
    internal static string? HomeForTest { get; set; }

    private static string Home => HomeForTest ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>What <c>~</c> or <c>~/…</c> names, or null for any other tilde — <c>~user</c>, <c>~+</c> — which only
    /// the shell resolves.</summary>
    private static string? Tilde(string token) =>
        token == "~" ? Home
        : token.StartsWith("~/", StringComparison.Ordinal) && Home.Length > 0 ? Path.Combine(Home, token[2..])
        : null;

    // MORE MATCHES THAN THIS IS NOT A LIST ANYONE CHECKED — and walking it costs every command that names it.
    private const int MostGlobMatches = 1000;

    /// <summary>
    /// An unquoted glob, listed now: its folder part must be spelled out, and that folder and every match are held to
    /// the boundary. A wildcard in the folder part, a bracket class, or more than <see cref="MostGlobMatches"/> matches
    /// leaves it unexamined.
    ///
    /// <para>WHAT IS LISTED IS WHAT THE SHELL WOULD EXPAND, near enough to hold a boundary: a match the shell would skip
    /// (a dot file) only adds a path to check, never removes one. The FOLDER is held even when nothing matches —
    /// <c>../*.cs</c> names a place outside the project whatever is in it.</para>
    /// </summary>
    private static void Glob(string token, ClassifyInto into)
    {
        var slash = token.LastIndexOf('/');
        var folderPart = slash < 0 ? "" : token[..(slash + 1)];
        var pattern = token[(slash + 1)..];
        if (token.Contains('[', StringComparison.Ordinal) || pattern.Length == 0
            || folderPart.IndexOfAny(['*', '?']) >= 0)
        {
            into.Unexamined.Add($"glob: {token}");
            return;
        }

        try
        {
            var spelled = folderPart.StartsWith('~') ? Tilde(folderPart.TrimEnd('/')) : folderPart;
            if (spelled is null)
            {
                into.Unexamined.Add($"glob: {token}");
                return;
            }

            var folder = Path.GetFullPath(Path.Combine(into.Folder, spelled));
            into.Paths.Add(folder);
            if (!Directory.Exists(folder)) return;   // nothing matches: the shell passes the pattern on as text

            var matches = Directory.EnumerateFileSystemEntries(folder, pattern).Take(MostGlobMatches + 1).ToList();
            if (matches.Count > MostGlobMatches)
            {
                into.Unexamined.Add($"glob matching too much: {token}");
                return;
            }
            into.Paths.AddRange(matches);
            into.Tokens.Add(token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            into.Unexamined.Add($"unreadable glob: {token}");
        }
    }

    // WHERE OUTPUT IS THROWN AWAY OR HANDED BACK — not files a command writes into.
    private static readonly HashSet<string> Discards = new(StringComparer.Ordinal) { "/dev/null", "/dev/stdout", "/dev/stderr" };

    /// <summary>
    /// A redirect's target: a path the command WRITES (or reads, for <c>&lt;</c>), held to the boundary whether or not
    /// it exists yet — <c>&gt; /tmp/new</c> creates it, so "only existing paths" would let every new file through.
    /// </summary>
    private static void Redirect(string target, ClassifyInto into)
    {
        if (Discards.Contains(target)) return;
        if (target.StartsWith('~') && Tilde(target) is { } home) target = home;
        if (target.StartsWith('~') || target.Contains('$', StringComparison.Ordinal)
            || target.Contains('*', StringComparison.Ordinal) || target.Contains('?', StringComparison.Ordinal))
        {
            into.Unexamined.Add($"redirect the shell resolves: {target}");
            return;
        }

        try
        {
            into.Paths.Add(Path.GetFullPath(Path.Combine(into.Folder, target)));
            into.Tokens.Add(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            into.Unexamined.Add($"unreadable redirect: {target}");
        }
    }

    // THE VERBS THAT MOVE THE SHELL — any of them after the leading cd ends the following of it.
    private static readonly HashSet<string> Movers = new(StringComparer.Ordinal) { "cd", "pushd", "popd" };

    /// <summary>The target of a leading <c>cd X &amp;&amp;</c> the rest of the line runs in, or null when the line
    /// does not start that way, the target is quoted or expands, or anything after it moves the shell again.</summary>
    private static string? FollowedCd(IReadOnlyList<ShellSegment> segments)
    {
        if (segments.Count < 2 || segments[0].Joiner != "&&" || segments[0].Redirects.Count > 0) return null;
        var words = segments[0].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is not ["cd", var target] || target.IndexOfAny(['"', '\'', '\\']) >= 0) return null;
        target = Tilde(target) ?? target;
        return segments.Skip(1).Any(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } verb
                                         && Movers.Contains(verb))
            ? null
            : target;
    }

    /// <summary>
    /// The commands of a line as the shell splits them — at <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>;</c>,
    /// <c>&amp;</c> and line breaks OUTSIDE QUOTES — each with the operator that ended it and the targets of its
    /// redirects lifted out. Null for a line this cannot read: an unterminated quote, or a here-document.
    ///
    /// <para>QUOTES ARE THE SHELL'S OWN RULE, and splitting without them read <c>grep -E 'a|///b'</c> as a pipe into a
    /// program called <c>///b'</c>. NOT A SHELL PARSER: no expansion, no substitution, no grouping — those stay the
    /// tokenizer's and the callers' to refuse. A redirect's target is a FILE, not a command, so <c>2&gt;/dev/null</c>
    /// is no longer a segment whose verb is a path.</para>
    /// </summary>
    internal static IReadOnlyList<ShellSegment>? Segments(string command)
    {
        var segments = new List<ShellSegment>();
        var current = new System.Text.StringBuilder();
        var redirects = new List<string>();
        var quote = '\0';
        var i = 0;

        void End(string? joiner)
        {
            var text = current.ToString().Trim();
            if (text.Length > 0 || redirects.Count > 0) segments.Add(new ShellSegment(text, joiner, [.. redirects]));
            current.Clear();
            redirects.Clear();
        }

        while (i < command.Length)
        {
            var c = command[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote) quote = '\0';
                else if (quote == '"' && c == '\\' && i + 1 < command.Length) current.Append(command[++i]);
                i++;
                continue;
            }

            if (c == '\\' && i + 1 < command.Length) { current.Append(c).Append(command[i + 1]); i += 2; continue; }
            if (c is '"' or '\'') { quote = c; current.Append(c); i++; continue; }

            if (c is '>' or '<' || (c == '&' && i + 1 < command.Length && command[i + 1] == '>'))
            {
                // A FILE DESCRIPTOR BEFORE IT — the 2 of 2> — belongs to the redirect, not to the command's words.
                var end = current.Length;
                while (end > 0 && char.IsAsciiDigit(current[end - 1])) end--;
                if (end < current.Length && (end == 0 || current[end - 1] == ' ')) current.Length = end;

                var j = i + (c == '&' ? 2 : 1);
                if (c == '<' && j < command.Length && command[j] == '<') return null;   // a here-document
                if (j < command.Length && (command[j] == '>' || command[j] == '|')) j++;

                // A DUPLICATION — 2>&1, >&2 — names a descriptor, not a file.
                if (j < command.Length && command[j] == '&')
                {
                    j++;
                    while (j < command.Length && (char.IsAsciiDigit(command[j]) || command[j] == '-')) j++;
                    i = j;
                    continue;
                }

                while (j < command.Length && command[j] == ' ') j++;
                var target = new System.Text.StringBuilder();
                var targetQuote = '\0';
                while (j < command.Length)
                {
                    var t = command[j];
                    if (targetQuote != '\0') { if (t == targetQuote) targetQuote = '\0'; else target.Append(t); j++; continue; }
                    if (t is '"' or '\'') { targetQuote = t; j++; continue; }
                    if (t is ' ' or '&' or ';' or '|' or '<' or '>' or '\n' or '\r' or '(' or ')') break;
                    target.Append(t);
                    j++;
                }
                if (targetQuote != '\0' || target.Length == 0) return null;
                redirects.Add(target.ToString());
                i = j;
                continue;
            }

            if (c is ';' or '\n' or '\r' or '|' or '&')
            {
                var next = i + 1 < command.Length ? command[i + 1] : '\0';
                var joiner = (c, next) switch
                {
                    ('&', '&') => "&&",
                    ('|', '|') => "||",
                    ('|', '&') => "|&",
                    _ => c.ToString(),
                };
                End(joiner);
                i += joiner.Length;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (quote != '\0') return null;
        End(null);
        return segments;
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
    private static void Classify(string token, ClassifyInto into)
    {
        try
        {
            // FROM THE FOLDER THE COMMAND READS IT IN — the process directory has no say in what a command touches.
            var full = Path.Combine(into.Folder, token);
            if (PathResolver.NamesAt(full))
            {
                into.Paths.Add(full);
                into.Tokens.Add(token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // COULD NOT TELL. A token we cannot even test is the definition of unexamined, and the
            // whole point of this type is that it says so instead of falling through as "not a path".
            into.Unexamined.Add($"unreadable: {token}");
        }
    }

    /// <summary>Where <see cref="Classify"/> looks and what it fills: the folder tokens are read in, the joined paths,
    /// their spellings, and anything it could not tell about.</summary>
    private sealed record ClassifyInto(string Folder, List<string> Paths, List<string> Tokens, List<string> Unexamined);

    /// <summary>
    /// Every token of <paramref name="command"/> that refers to a path relative to <paramref name="from"/> (and an
    /// absolute one with a <c>..</c>), joined onto it — the population the boundary is held to beyond the paths
    /// <see cref="Classify"/> found.
    ///
    /// <para>BY SHAPE, NOT ONLY BY EXISTENCE. <see cref="Classify"/> records a token that names something where the
    /// command reads it; a token that names nothing — <c>rm -rf ../outside</c> before the folder exists,
    /// <c>../missing</c> — would then be no path at all, and a boundary held to the paths found would hold on
    /// nothing. A token that asserts a location by its shape is collected whether or not it exists, joined onto the
    /// folder the command reads it in, so where it would land is judged.</para>
    ///
    /// <para>IT IS A RESOLUTION, NOT A STRING MATCH ON <c>..</c>, and that distinction is the whole
    /// value: <c>./sub/../file.txt</c> names a file inside the root by an ugly spelling and stays
    /// approvable, while <c>../outside</c> does not, because the answer comes from where the path
    /// actually lands. Refusing every dot-dot would have been cheaper and would have made the
    /// predicate lie about what it enforces.</para>
    ///
    /// <para>ONLY TOKENS THAT ARE PATH REFERENCES, so this does not become friction:
    /// <c>grep TODO .</c> stays quiet for want of a file named TODO. A bare word like <c>build</c> or a grep pattern is not
    /// claimed as a path — it names no directory component and the shell will not treat it as one
    /// without a verb's help. A token carrying a separator, or consisting of <c>..</c>, is asserting
    /// a location, and an assertion about a location is exactly what a boundary is for.</para>
    /// </summary>
    private static IEnumerable<string> ReferencedPaths(IReadOnlyList<ShellSegment> segments, string from)
    {
        foreach (var segment in segments)
        {
            // THE VERB IS NOT A SUBJECT, the same carve-out CommandSubjects makes and for the same
            // reason — and ExaminableSegments has ALREADY refused any verb containing a slash, so
            // skipping it here cannot let a path-shaped program through. QUOTED TOKENS COUNT: `cat '../x'` reads ../x.
            foreach (var token in Tokenize(segment.Text, []).Skip(1))
            {
                var text = token.Text;

                // A FLAG CARRYING ITS VALUE, unwrapped for the reason CommandSubjects unwraps it:
                // `--file=../secret` and `-f ../secret` are one read spelled two ways, and a rule
                // that separates them is not one anybody can hold in their head.
                var eq = text.IndexOf('=', StringComparison.Ordinal);
                if (text.StartsWith('-') && eq > 0) text = text[(eq + 1)..].Trim('"', '\'');

                if (text.Length == 0 || text.StartsWith('-')) continue;

                // ABSOLUTE PATHS ARE ALREADY THE OTHER CLAUSE'S JOB, and re-confining them here would
                // be a second opinion on a question already answered — the drift that put four holes
                // of one shape in this system. Tilde is likewise already refused, by FullyExamined.
                //
                // EXCEPT ONE WITH A `..` IN IT. That clause keeps an absolute path only when it exists, and
                // existence is tested on the text collapsed — `/root/link/../secret` is asked about as
                // `/root/secret`, which may not exist, while the kernel reads beside the link's target.
                if (text.StartsWith('~'))
                {
                    if (token.Quoted || Tilde(text) is not { } home) continue;
                    text = home;
                }
                if (Path.IsPathRooted(text) && !text.Split('/').Contains("..")) continue;

                var full = Path.Combine(from, text);

                // A BARE WORD IS A PATH WHEN IT NAMES SOMETHING WHERE THE COMMAND READS IT — from a child's folder
                // or a working_dir, `cat link` is a read through that link. A link counts even when dangling.
                if (!IsPathReference(text) && !PathResolver.NamesAt(full)) continue;

                yield return full;
            }
        }
    }

    /// <summary>
    /// Whether a token asserts a LOCATION rather than merely being a word.
    ///
    /// <para>THE LINE IS DRAWN AT A DIRECTORY SEPARATOR because that is where the shell draws it: a
    /// token containing <c>/</c> can only be a path, while a bare word is whatever the verb decides —
    /// a grep pattern, a git ref, a build target. Claiming bare words as paths is precisely the
    /// friction <c>CommandSubjects.Classify</c> documents avoiding, and friction is what gets a gate
    /// routed around.</para>
    ///
    /// <para><c>..</c> ALONE IS THE EXCEPTION, because it carries no separator and is unambiguously a
    /// location — <c>cd ..</c>, <c>rm -rf ..</c>. A bare <c>.</c> needs no exception: it resolves to
    /// the root itself and is inside by definition, so admitting it would change no answer.</para>
    /// </summary>
    internal static bool IsPathReference(string token) =>
        token.Contains('/', StringComparison.Ordinal)
        || token.Contains('\\', StringComparison.Ordinal)
        || token == "..";

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
        var expands = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // A DOLLAR EXPANDS everywhere but inside single quotes, and only with something after it to name.
            if (c == '$' && quote != '\'' && i + 1 < text.Length && IsExpansionStart(text[i + 1])) expands = true;

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
                    tokens.Add(new Token(current.ToString(), wasQuoted, expands));
                    current.Clear();
                    wasQuoted = false;
                    expands = false;
                }
                continue;
            }

            current.Append(c);
        }

        if (quote != '\0') unexamined.Add("unterminated quote");
        if (current.Length > 0 || wasQuoted) tokens.Add(new Token(current.ToString(), wasQuoted, expands));

        return tokens;
    }

    // WHAT A SHELL EXPANDS AFTER A DOLLAR: a name, a brace, a position, or one of the special parameters.
    // `cost$` and `'end$'` stay text, as they do to the shell.
    private static bool IsExpansionStart(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '{' or '@' or '*' or '#' or '?' or '$' or '!' or '-';

    /// <summary>
    /// Whether this command walks through links: <c>grep -R</c>, <c>rg -L</c>, <c>du -L</c>, <c>tree -l</c>,
    /// <c>ls -RL</c>. The plain recursive forms (<c>grep -r</c>, <c>rg</c>, <c>du</c>, <c>ls -R</c>) list a
    /// link without entering it, so they stay what the folder they name says.
    /// </summary>
    private static bool FollowsLinks(List<Token> stage)
    {
        var verb = stage[0].Text;
        var flags = stage.Skip(1).Where(t => !t.Quoted && t.Text.StartsWith('-')).Select(t => t.Text).ToList();
        bool Short(char letter) => flags.Any(f => !f.StartsWith("--", StringComparison.Ordinal)
                                                   && f.AsSpan(1).Contains(letter));
        bool Long(string name) => flags.Contains(name, StringComparer.Ordinal);

        return verb switch
        {
            "grep" or "egrep" or "fgrep" => Short('R') || Long("--dereference-recursive"),
            "rg" => Short('L') || Long("--follow"),
            "du" => Short('L') || Long("--dereference"),
            "tree" => Short('l'),
            "ls" => (Short('L') || Long("--dereference")) && (Short('R') || Long("--recursive")),
            _ => false,
        };
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
    ///
    /// <para><see cref="Expands"/> SAYS THE SHELL WILL REWRITE IT — a <c>$</c> outside single quotes — so
    /// the text is not the path that runs.</para>
    /// </summary>
    private readonly record struct Token(string Text, bool Quoted, bool Expands);
}

/// <summary>One command of a line: its words, the operator that ended it (null for the last), and the targets of its
/// redirects — see <see cref="CommandSubjects.Segments"/>.</summary>
internal sealed record ShellSegment(string Text, string? Joiner, IReadOnlyList<string> Redirects);
