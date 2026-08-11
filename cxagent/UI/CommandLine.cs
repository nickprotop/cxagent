using CxAgent.Core.Agent;

namespace CxAgent.UI;

/// <summary>What the command line asked for.</summary>
/// <param name="UseMock">The mock provider, for driving the UI without a model.</param>
/// <param name="Mode">The mode this session starts in.</param>
/// <param name="Error">
/// What was wrong with the arguments, or null. NON-NULL MEANS DO NOT START: an argument nobody
/// understood must stop the app rather than be ignored, because a user who typed <c>--mode fanout</c>
/// and silently got single mode concludes the feature is broken.
/// </param>
public readonly record struct CommandLineOptions(bool UseMock, AgentMode Mode, string? Error);

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

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

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
                return new(useMock, mode, $"unknown argument '{arg}'. Valid: --mock, --mode <{AgentModes.Valid}>.");

            if (string.IsNullOrWhiteSpace(value))
                return new(useMock, mode, $"--mode needs a value. Valid: {AgentModes.Valid}.");

            var parsed = AgentModes.Parse(value);
            if (parsed is null)
                return new(useMock, mode, $"unknown mode '{value}'. Valid: {AgentModes.Valid}.");

            mode = parsed.Value;
        }

        return new(useMock, mode, null);
    }
}
