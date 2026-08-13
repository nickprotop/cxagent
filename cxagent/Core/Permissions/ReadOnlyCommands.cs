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
    public static bool IsReadOnly(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        var text = command.Trim();

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
}
