using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// How a plugin's own command turned out — this package's vocabulary, not Core's.
///
/// <para>THIS PACKAGE DOES NOT REFERENCE cxagent.Core — see <see cref="JobLogLevel"/> for the same
/// reason applied to a job's severity, and <see cref="JobResult"/> for a result shape that stayed
/// self-contained rather than returning a Core-side type. Core's own <c>CommandStatus</c> is
/// load-bearing across its session dispatch and cannot become a dependency of the package a plugin
/// author installs stand-alone: the abstractions package is the leaf, and Core depends on IT, never
/// the other way.</para>
///
/// <para>THREE VALUES, NOT CORE'S FOUR. Core's <c>CommandStatus</c> also has <c>Unknown</c> — "nothing
/// here services this line", a dispatcher's answer to "did anyone claim this?" before it even reaches
/// a handler. A plugin implementing this interface was ASKED for a command it itself declared in its
/// manifest, so "I do not know this command" is not an outcome it can honestly return; if it wants to
/// decline, it refuses and says why in <see cref="CommandResult.Message"/>. Core maps these three
/// values onto its own four-value enum at the registration site — the one place a translation between
/// a public contract and an internal enum belongs.</para>
/// </summary>
public enum PluginCommandOutcome
{
    /// <summary>It ran and said its result. Nothing about the session changed.</summary>
    Reported,

    /// <summary>It ran and changed something the session holds.</summary>
    Changed,

    /// <summary>It declined — a bad argument, a precondition unmet. The Message says why.</summary>
    Refused,
}

/// <summary>What a plugin's command did, as the user sees it.</summary>
/// <param name="Message">Text for the transcript, or null when the command says nothing.</param>
/// <param name="Status">
/// How the command turned out, in this package's own vocabulary. See
/// <see cref="PluginCommandOutcome"/> for why it is not Core's <c>CommandStatus</c>.
///
/// <para>NOT A <see cref="JobResult"/>. A tool's result is shaped for a MODEL to read and carries an
/// error field a model reasons about; a command's reader is the person who typed it, and what Core
/// needs back is whether anything serviced the line.</para>
/// </param>
public sealed record CommandResult(string? Message, PluginCommandOutcome Status);

/// <summary>
/// A plugin that services commands it declared in its manifest.
///
/// <para>DECLARED AND TYPE-TESTED, exactly as <see cref="IPluginGateSource"/> is: the manifest is what
/// the load prompt disclosed, so a plugin promising a command its binary cannot run is refused at
/// load rather than failing when someone types it. The check runs in ONE direction — declaring
/// without implementing is a lie; implementing without declaring is merely unused.</para>
///
/// <para>A COMMAND IS NOT A TOOL CALL. It is typed by a person, its argument is whatever they wrote
/// rather than a JSON object matching a schema, and its answer goes to the transcript. That is why
/// this is its own entry point and not a reserved name passed to <see cref="IPlugin.Invoke"/>.</para>
/// </summary>
public interface IPluginCommandHandler
{
    /// <param name="name">The declared command, without its leading slash.</param>
    /// <param name="arguments">Everything the user typed after the name, trimmed. Empty, never null.</param>
    /// <param name="ct">
    /// Cancelled when the session this plugin was loaded into ends — the same token as
    /// <see cref="IPluginContext.Lifetime"/>.
    ///
    /// <para>NOT A TURN'S TOKEN. A command is typed by a person and dispatched outside the turn
    /// loop, so interrupting the model does not cancel it; only the session ending does.</para>
    /// </param>
    Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct);
}
