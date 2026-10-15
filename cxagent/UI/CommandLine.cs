using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Agents;

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
/// What <c>-c</c>, <c>-r ID</c> or a bare <c>--resume</c> asked for — Claude Code's split. A bare
/// <c>--resume</c> is a request to PICK, which the TUI answers by listing this folder's sessions; it
/// never guesses which one was meant.
/// </param>
/// <param name="Error">
/// What was wrong with the arguments, or null. NON-NULL MEANS DO NOT START: an argument nobody
/// understood must stop the app rather than be ignored, because a user who typed <c>--mode fanout</c>
/// and silently got single mode concludes the feature is broken.
/// </param>
/// <param name="Headless">Set when <c>-p</c> was given: run one goal without the TUI.</param>
public readonly record struct CommandLineOptions(
    bool UseMock,
    AgentMode Mode,
    string? Error,
    bool ShowVersion = false,
    string? Instance = null,
    bool ListSessions = false,
    ResumeRequest Resume = default,
    bool ListAllSessions = false,
    string? ConfigDir = null,
    string? Theme = null,
    bool ShowHelp = false,
    HeadlessOptions? Headless = null);

/// <summary>Which session the command line asked to reopen, if any.</summary>
public enum ResumeKind
{
    /// <summary>Start a new session.</summary>
    None,

    /// <summary><c>-c</c>: the most recent session in this folder.</summary>
    Continue,

    /// <summary><c>-r ID</c>: the one the id names.</summary>
    ById,

    /// <summary>A bare <c>--resume</c>: list this folder's sessions and let the user choose.</summary>
    Pick,
}

/// <summary>What <c>-c</c>, <c>-r ID</c> or a bare <c>--resume</c> asked for.</summary>
/// <param name="Kind">Which of the four requests it is.</param>
/// <param name="Uid">The id or abbreviation, for <see cref="ResumeKind.ById"/> only.</param>
public readonly record struct ResumeRequest(ResumeKind Kind, string? Uid)
{
    /// <summary>Was any session asked for at all?</summary>
    public bool Wanted => Kind != ResumeKind.None;

    /// <summary>The default: start fresh.</summary>
    public static ResumeRequest No => new(ResumeKind.None, null);

    /// <summary>Continue the newest session in this folder.</summary>
    public static ResumeRequest Continue => new(ResumeKind.Continue, null);

    /// <summary>List this folder's sessions to choose from.</summary>
    public static ResumeRequest Pick => new(ResumeKind.Pick, null);

    /// <summary>Continue the one this names.</summary>
    public static ResumeRequest Of(string uid) => new(ResumeKind.ById, uid);
}

/// <summary>What a headless run writes to stdout — Claude Code's three formats.</summary>
public enum OutputFormat { Text, Json, StreamJson }

/// <summary>
/// The permission mode a headless run starts in. <c>dontAsk</c> folds into <see cref="Default"/>:
/// with nobody to ask, refusing what would prompt is what the default already does.
/// </summary>
public enum HeadlessPermissions { Default, AcceptEdits, Auto, Bypass }

/// <summary>What <c>-p</c> and the options that only make sense with it asked for.</summary>
public sealed record HeadlessOptions
{
    /// <summary>The goal text, or null when the goal is stdin.</summary>
    public string? Goal { get; init; }

    public OutputFormat Output { get; init; } = OutputFormat.Text;
    public bool IncludePartialMessages { get; init; }
    public bool Verbose { get; init; }

    /// <summary>The turn ceiling for this run, or null for the configured one.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>The thinking level for this run (<c>--thinking</c>), or null to keep the session's own.</summary>
    public string? Thinking { get; init; }

    public HeadlessPermissions Permissions { get; init; } = HeadlessPermissions.Default;
    public bool Trust { get; init; }

    /// <summary>Plugins named with <c>--plugin</c>, loaded for this run whether approved or not.</summary>
    public IReadOnlyList<string> Plugins { get; init; } = [];

    /// <summary><c>--plugin none</c>: load no plugins at all.</summary>
    public bool NoPlugins { get; init; }

    // BYPASS IMPLIES TRUST. Skipping every prompt in a folder that still prompts for reads would
    // approve the reads one by one anyway; trusting it says the same thing without the noise.
    public bool TrustsFolder => Trust || Permissions == HeadlessPermissions.Bypass;
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
    // ONE LIST, so the refusal message and the help agree about what cxagent does not do.
    private static readonly string[] Refused =
    [
        "--allowedTools", "--allowed-tools", "--disallowedTools", "--disallowed-tools", "--tools",
        "--append-system-prompt-file", "--append-system-prompt", "--system-prompt-file",
        "--system-prompt", "--mcp-config", "--json-schema", "--max-budget-usd", "--bare",
        "--no-session-persistence", "--input-format",
    ];
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
        string? configDir = null;
        string? theme = null;
        var showHelp = false;
        var resume = ResumeRequest.No;
        var print = false;
        string? goal = null;
        var output = OutputFormat.Text;
        var partial = false;
        var verbose = false;
        int? maxTurns = null;
        string? thinking = null;
        var permissions = HeadlessPermissions.Default;
        var trust = false;
        var plugins = new List<string>();
        var noPlugins = false;
        // WHICH HEADLESS-ONLY OPTION WAS SEEN, so one given without -p can be named in the error.
        string? headlessOnly = null;
        string? positional = null;

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

            // --config-dir, NOT --config: it takes a DIRECTORY, and config.json is only one of the
            // things in it — the resume database, the usage archive and the logs live there too.
            //
            // XDG_CONFIG_HOME ALREADY DID THIS, and still does. The flag exists because an env var
            // is awkward to type, invisible in a bug report, and does not survive being pasted into
            // an issue — while "run it against an empty config dir" is the first thing to ask
            // someone reporting first-run behaviour.
            if (string.Equals(arg, "--config-dir", StringComparison.Ordinal))
            {
                configDir = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
                if (string.IsNullOrWhiteSpace(configDir))
                    return new(useMock, mode, "--config-dir needs a directory path.");
                continue;
            }

            // --theme NAME. Not validated here: which themes exist is a question only the window
            // system can answer, and it does not exist yet. An unknown name falls back to cxagent's
            // own at startup rather than refusing to start over a colour scheme.
            if (string.Equals(arg, "--theme", StringComparison.Ordinal))
            {
                theme = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
                if (string.IsNullOrWhiteSpace(theme))
                    return new(useMock, mode, "--theme needs a theme name.");
                continue;
            }

            if (string.Equals(arg, "--help", StringComparison.Ordinal)
                || string.Equals(arg, "-h", StringComparison.Ordinal))
            {
                showHelp = true;
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

            if (string.Equals(arg, "--mock", StringComparison.Ordinal))
            {
                // A PROVIDER CHOICE, NOT A MODE, and deliberately left orthogonal: a mock session is
                // exactly where someone drives the UI, and it must be able to do so in either mode.
                useMock = true;
                continue;
            }

            // CLAUDE CODE'S OPTIONS THIS CLI DOES NOT HAVE, refused by name. Ignoring one would let a
            // script believe `--allowedTools` narrowed what ran; an error is the only honest answer.
            if (Refused.FirstOrDefault(r => arg == r || arg.StartsWith(r + "=", StringComparison.Ordinal)) is { } refused)
                return new(useMock, mode, $"{refused} is not supported by cxagent");

            if (arg is "-p" or "--print")
            {
                print = true;
                // THE NEXT WORD IS THE GOAL UNLESS IT IS AN OPTION. `cat task.md | cxagent -p` has
                // no text, and stdin is then the goal.
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) goal = args[++i];
                continue;
            }

            if (arg is "-c" or "--continue")
            {
                resume = ResumeRequest.Continue;
                continue;
            }

            if (arg is "-r" or "--resume")
            {
                // THE NEXT WORD, BUT ONLY IF IT IS A VALUE. `--resume --mock` is a pick with the mock
                // provider; swallowing the flag as an id would drop it silently.
                var next = i + 1 < args.Length ? args[i + 1] : null;
                if (next is not null && !next.StartsWith('-'))
                {
                    resume = ResumeRequest.Of(next);
                    i++;
                }
                else
                {
                    resume = ResumeRequest.Pick;
                }
                continue;
            }

            if (arg.StartsWith("--resume=", StringComparison.Ordinal))
            {
                var uid = arg["--resume=".Length..];
                if (string.IsNullOrWhiteSpace(uid))
                    return new(useMock, mode, "--resume= needs an id; -c continues the most recent.");
                resume = ResumeRequest.Of(uid);
                continue;
            }

            if (arg == "--output-format")
            {
                headlessOnly ??= arg;
                var given = i + 1 < args.Length ? args[++i] : null;
                OutputFormat? parsedFormat = given switch
                {
                    "text" => OutputFormat.Text,
                    "json" => OutputFormat.Json,
                    "stream-json" => OutputFormat.StreamJson,
                    _ => null,
                };
                if (parsedFormat is null)
                    return new(useMock, mode, "--output-format needs text, json or stream-json.");
                output = parsedFormat.Value;
                continue;
            }

            if (arg == "--permission-mode")
            {
                headlessOnly ??= arg;
                var given = i + 1 < args.Length ? args[++i] : null;
                if (given == "plan")
                    return new(useMock, mode, "--permission-mode plan is not supported: cxagent has no plan mode.");
                HeadlessPermissions? parsedMode = given switch
                {
                    "default" or "dontAsk" => HeadlessPermissions.Default,
                    "acceptEdits" => HeadlessPermissions.AcceptEdits,
                    "auto" => HeadlessPermissions.Auto,
                    "bypassPermissions" => HeadlessPermissions.Bypass,
                    _ => null,
                };
                if (parsedMode is null)
                    return new(useMock, mode,
                        "--permission-mode needs default, dontAsk, acceptEdits, auto or bypassPermissions.");
                permissions = parsedMode.Value;
                continue;
            }

            if (arg == "--dangerously-skip-permissions")
            {
                headlessOnly ??= arg;
                permissions = HeadlessPermissions.Bypass;
                continue;
            }

            if (arg == "--max-turns")
            {
                headlessOnly ??= arg;
                var given = i + 1 < args.Length ? args[++i] : null;
                if (!int.TryParse(given, out var turns) || turns < 1)
                    return new(useMock, mode, "--max-turns needs a whole number of at least 1.");
                maxTurns = turns;
                continue;
            }

            if (arg == "--thinking")
            {
                headlessOnly ??= arg;
                var given = i + 1 < args.Length ? args[++i] : null;
                if (!ThinkingLevels.IsKnown(given))
                    return new(useMock, mode, $"--thinking needs one of {string.Join(", ", ThinkingLevels.Known)}.");
                thinking = given;
                continue;
            }

            if (arg == "--plugin")
            {
                headlessOnly ??= arg;
                var given = i + 1 < args.Length ? args[++i] : null;
                if (string.IsNullOrWhiteSpace(given))
                    return new(useMock, mode, "--plugin needs a configured plugin name, or none.");
                if (given == "none") noPlugins = true;
                else plugins.AddRange(given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                continue;
            }

            if (arg == "--trust") { headlessOnly ??= arg; trust = true; continue; }
            if (arg == "--include-partial-messages") { headlessOnly ??= arg; partial = true; continue; }

            // ACCEPTED WITHOUT -p TOO: in the TUI it has nothing to add, and refusing it would break
            // a habit carried over from Claude Code for no gain.
            if (arg == "--verbose") { verbose = true; continue; }

            // A BARE WORD IS THE GOAL, wherever it stands — Claude Code's own order is
            // `-p --output-format json "goal"`. Kept aside and checked after the loop: it only means
            // something with -p, and a second one is a mistake rather than more goal.
            if (!arg.StartsWith('-'))
            {
                if (positional is not null)
                    return new(useMock, mode, $"unexpected argument '{arg}': give the goal once, quoted.");
                positional = arg;
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
                    $"unknown argument '{arg}'. Valid: --version, --help, --mock, --config-dir <dir>, "
                    + $"--theme <name>, --mode <{AgentModes.Valid}>, --model <instance>, --sessions, "
                    + "-c, -r <id>, -p <goal>, --thinking <level>.");

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

        if (positional is not null)
        {
            if (!print)
                return new(useMock, mode, $"unknown argument '{positional}'. A goal on the command line needs -p.");
            if (goal is not null)
                return new(useMock, mode, $"unexpected argument '{positional}': give the goal once, quoted.");
            goal = positional;
        }

        if (!print && headlessOnly is not null)
            return new(useMock, mode, $"{headlessOnly} only applies to a headless run: add -p \"<goal>\".");

        if (print && resume.Kind == ResumeKind.Pick)
            return new(useMock, mode,
                "--resume needs an id with -p: nobody is there to pick. -c continues the most recent.");

        if (print && listSessions)
            return new(useMock, mode, "--sessions prints the list and exits, so it cannot be combined with -p.");

        HeadlessOptions? headless = print
            ? new HeadlessOptions
            {
                Goal = goal, Output = output, IncludePartialMessages = partial, Verbose = verbose,
                MaxTurns = maxTurns, Thinking = thinking, Permissions = permissions, Trust = trust,
                Plugins = plugins, NoPlugins = noPlugins,
            }
            : null;

        return new(useMock, mode, null, showVersion, instance, listSessions, resume, listAll, configDir, theme, showHelp,
            headless);
    }
}
