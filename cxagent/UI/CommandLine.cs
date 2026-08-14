using CxAgent.Core.Agent;

namespace CxAgent.UI;

/// <summary>What the command line asked for.</summary>
/// <param name="UseMock">The mock provider, for driving the UI without a model.</param>
/// <param name="Mode">The mode this session starts in.</param>
/// <param name="ListSessions">
/// <c>--sessions</c>: print the listing to stdout and exit, WITHOUT starting the TUI.
///
/// <para>A QUESTION, NOT A SESSION. "which conversations do I have here" is answered by looking, and
/// making someone launch a full-screen app — and pay for whatever a first turn costs — to read a
/// list is the kind of friction that stops people looking. It also makes the ids scriptable, which
/// is the whole point of having a stable id.</para>
/// </param>
/// <param name="ListAllSessions">
/// <c>--sessions all</c>: every folder, not just this one — the same widening the command offers.
/// </param>
/// <param name="Resume">
/// What <c>--resume</c> asked for. THREE STATES, and they are genuinely different requests:
/// <list type="bullet">
/// <item>absent — start a new session (<see cref="ResumeRequest.No"/>)</item>
/// <item><c>--resume</c> alone — continue the most recent one here</item>
/// <item><c>--resume &lt;id&gt;</c> — continue that specific one</item>
/// </list>
/// A nullable string cannot carry this: null would have to mean both "not asked for" and "asked for,
/// unspecified", and those take opposite actions.
/// </param>
/// <param name="Error">
/// What was wrong with the arguments, or null. NON-NULL MEANS DO NOT START: an argument nobody
/// understood must stop the app rather than be ignored, because a user who typed <c>--mode fanout</c>
/// and silently got single mode concludes the feature is broken.
/// </param>
public readonly record struct CommandLineOptions(
    bool UseMock,
    AgentMode Mode,
    string? Error,
    bool ShowVersion = false,
    string? Instance = null,
    bool ListSessions = false,
    ResumeRequest Resume = default,
    bool ListAllSessions = false);

/// <summary>Which session <c>--resume</c> asked for, if any.</summary>
/// <param name="Wanted">Was <c>--resume</c> given at all?</param>
/// <param name="Uid">The id or abbreviation that followed it, or null for "the most recent".</param>
public readonly record struct ResumeRequest(bool Wanted, string? Uid)
{
    /// <summary>The default: start fresh.</summary>
    public static ResumeRequest No => new(false, null);

    /// <summary>Continue the newest session in this folder.</summary>
    public static ResumeRequest Latest => new(true, null);

    /// <summary>Continue the one this names.</summary>
    public static ResumeRequest Of(string uid) => new(true, uid);
}

/// <summary>
/// Reads the command line.
///
/// <para>A TYPE RATHER THAN A LINE IN <c>Run()</c>. Argument parsing was
/// <c>args.Contains("--mock")</c> inline, with no table, no validation and no test — and
/// <c>Run()</c> cannot be unit-tested at all, since it builds a console driver and takes over the
/// terminal. Everything here is a pure function of <c>string[]</c>.</para>
/// </summary>
public static class CommandLine
{
    public static CommandLineOptions Parse(string[] args)
    {
        var useMock = false;
        // FAN-OUT BY DEFAULT. Sub-agents are built, driven and proven, and a capability nobody
        // discovers is a capability nobody has — this model does not reach for `--mode fan-out` any
        // more than it reaches for the spawn tool unprompted.
        //
        // The cost of being wrong is small in this direction: a fan-out session that never spawns is
        // a single-agent session that paid for a slightly longer system prompt. A single-mode session
        // that WANTED to delegate cannot, and gives no hint that it could.
        var mode = AgentMode.FanOut;
        var listSessions = false;
        var listAll = false;
        var showVersion = false;
        string? instance = null;
        var resume = ResumeRequest.No;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--version", StringComparison.Ordinal)
             || string.Equals(arg, "-v", StringComparison.Ordinal))
            {
                showVersion = true;
                continue;
            }

            // --model NAMES A CONFIGURED INSTANCE, the same thing /model switches between and the
            // same thing `defaultProvider` names. It does not name a model id: a model belongs to an
            // instance here, along with its endpoint and its context window, and accepting a bare id
            // would mean inventing an instance with no window — which is the one field whose absence
            // silently breaks compaction.
            if (arg.StartsWith("--model=", StringComparison.Ordinal))
            {
                instance = arg["--model=".Length..];
                if (string.IsNullOrWhiteSpace(instance))
                    return new(useMock, mode, "--model= needs an instance name from `providers`.");
                continue;
            }

            if (string.Equals(arg, "--model", StringComparison.Ordinal))
            {
                instance = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
                if (string.IsNullOrWhiteSpace(instance))
                    return new(useMock, mode, "--model needs an instance name from `providers`.");
                continue;
            }

            if (string.Equals(arg, "--sessions", StringComparison.Ordinal))
            {
                listSessions = true;

                // `--sessions all`, spelled the way the command spells it. A bare word rather than a
                // second flag, so what a user learns at the prompt is what works on the shell.
                if (i + 1 < args.Length && string.Equals(args[i + 1], "all", StringComparison.OrdinalIgnoreCase))
                {
                    listAll = true;
                    i++;
                }
                continue;
            }

            if (arg.StartsWith("--resume=", StringComparison.Ordinal))
            {
                var uid = arg["--resume=".Length..];
                if (string.IsNullOrWhiteSpace(uid))
                    return new(useMock, mode, "--resume= needs an id, or use --resume on its own.");

                resume = ResumeRequest.Of(uid);
                continue;
            }

            if (string.Equals(arg, "--resume", StringComparison.Ordinal))
            {
                // THE NEXT WORD, BUT ONLY IF IT IS A VALUE. `--resume --mock` asks to continue the
                // most recent session with the mock provider; swallowing the flag as an id would
                // fail to find a session called "--mock" and drop the flag silently.
                var next = i + 1 < args.Length ? args[i + 1] : null;
                if (next is not null && !next.StartsWith('-'))
                {
                    resume = ResumeRequest.Of(next);
                    i++;
                }
                else
                {
                    resume = ResumeRequest.Latest;
                }
                continue;
            }

            if (string.Equals(arg, "--mock", StringComparison.Ordinal))
            {
                // A PROVIDER CHOICE, NOT A MODE, and deliberately left orthogonal: a mock session is
                // exactly where someone drives the UI, and it must be able to do so in either mode.
                useMock = true;
                continue;
            }

            // BOTH FORMS. `--mode fan-out` is what people type; `--mode=fan-out` is what scripts and
            // shell completions generate, and rejecting it would be a puzzle rather than a lesson.
            string? value = null;
            if (arg.StartsWith("--mode=", StringComparison.Ordinal))
                value = arg["--mode=".Length..];
            else if (string.Equals(arg, "--mode", StringComparison.Ordinal))
                value = i + 1 < args.Length ? args[++i] : null;
            else
                return new(useMock, mode,
                    $"unknown argument '{arg}'. Valid: --version, --mock, "
                    + $"--mode <{AgentModes.Valid}>, --model <instance>, --sessions, "
                    + "--resume [<id>].");

            if (string.IsNullOrWhiteSpace(value))
                return new(useMock, mode, $"--mode needs a value. Valid: {AgentModes.Valid}.");

            var parsed = AgentModes.Parse(value);
            if (parsed is null)
                return new(useMock, mode, $"unknown mode '{value}'. Valid: {AgentModes.Valid}.");

            mode = parsed.Value;
        }

        // ONE ASKS A QUESTION, THE OTHER STARTS WORK. Honouring both means either printing a list and
        // ignoring the resume, or resuming and never printing — both are half of what was typed, and
        // the user cannot tell which half they got.
        if (listSessions && resume.Wanted)
            return new(useMock, mode,
                "--sessions prints the list and exits, so it cannot be combined with --resume.");

        return new(useMock, mode, null, showVersion, instance, listSessions, resume, listAll);
    }
}
