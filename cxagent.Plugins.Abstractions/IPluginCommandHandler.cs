using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Whether a plugin's command ran, and how — the same four-way answer Core's own commands give
/// (<c>CxAgent.Core.Commands.CommandStatus</c>), duplicated here rather than referenced.
///
/// <para>THIS PACKAGE DOES NOT REFERENCE cxagent.Core — see <see cref="JobLogLevel"/> for the same
/// reason applied to a job's severity, and <see cref="JobResult"/> for a result shape that stayed
/// self-contained rather than returning a Core-side type. <c>CommandStatus</c> is load-bearing across
/// 50+ call sites in Session's own dispatch and cannot become a dependency of the package a plugin
/// author installs stand-alone. Core converts between the two one-for-one at the boundary that calls
/// <see cref="IPluginCommandHandler.RunCommand"/>.</para>
/// </summary>
public enum CommandOutcome
{
    /// <summary>Nothing here services this. Mirrors <c>CommandStatus.Unknown</c>.</summary>
    Unknown,

    /// <summary>It ran and said its result; nothing about the session changed. Mirrors
    /// <c>CommandStatus.Reported</c>.</summary>
    Reported,

    /// <summary>It ran and something changed. Mirrors <c>CommandStatus.Changed</c>.</summary>
    Changed,

    /// <summary>It could not run now, and said why. Mirrors <c>CommandStatus.Refused</c>.</summary>
    Refused,
}

/// <summary>What a plugin's command did, as the user sees it.</summary>
/// <param name="Message">Text for the transcript, or null when the command says nothing.</param>
/// <param name="Status">
/// Whether it was handled — the same answer Core's own commands give, in this package's own
/// vocabulary. See <see cref="CommandOutcome"/> for why it is not the Core type itself.
///
/// <para>NOT A <see cref="JobResult"/>. A tool's result is shaped for a MODEL to read and carries an
/// error field a model reasons about; a command's reader is the person who typed it, and what Core
/// needs back is whether anything serviced the line.</para>
/// </param>
public sealed record CommandResult(string? Message, CommandOutcome Status);

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
    /// <param name="ct">Cancelled if the session ends or the user interrupts while this is running.</param>
    Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct);
}
