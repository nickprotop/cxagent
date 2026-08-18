namespace CxAgent.Core.Commands;

/// <summary>
/// Whether a command ran, and — when it did — whether it changed anything.
///
/// <para>WHAT THIS REPLACES: a bare <c>bool</c> on eleven session methods, which meant "handled"
/// rather than "succeeded" and could not tell the two apart. <c>ListSkills()</c> returned true
/// whether it found skills or not, and a caller reading it as success had no way to know.</para>
///
/// <para>THE DISTINCTION IS REAL FOR A CONSUMER. Dispatch only needs "did anything take this" — that
/// is the registry's <see cref="CommandHandler"/> contract and it stays a bool. An app driving a
/// session directly wants more: whether to clear a composer, whether to repaint, whether to report a
/// refusal. Those are different answers and one bool was giving them the same one.</para>
/// </summary>
public enum CommandStatus
{
    /// <summary>
    /// Nothing here services this. The caller may try elsewhere — the registry treats it as unhandled.
    /// </summary>
    Unknown,

    /// <summary>
    /// It ran and said its result. Nothing about the session changed — a listing, a help line, an
    /// already-in-that-state reply.
    /// </summary>
    Reported,

    /// <summary>
    /// It ran and the session moved: a mode set, a model switched, a context cleared. A watcher
    /// should expect the corresponding <see cref="Agent.SessionChangeKind"/> to have been announced.
    /// </summary>
    Changed,

    /// <summary>
    /// It could not run now, and said why. A turn was in flight, or what it needs is not configured —
    /// distinct from <see cref="Unknown"/>, because the command exists and the caller should not
    /// look for another handler.
    /// </summary>
    Refused,
}

/// <summary>Reading a <see cref="CommandStatus"/> the way each caller wants it.</summary>
public static class CommandStatusExtensions
{
    /// <summary>True when something serviced the command, however it turned out — the registry's
    /// question, and the reason dispatch can stay a bool.</summary>
    public static bool Handled(this CommandStatus status) => status != CommandStatus.Unknown;

    /// <summary>True when the session's state moved, so a watcher knows a repaint is warranted.</summary>
    public static bool Moved(this CommandStatus status) => status == CommandStatus.Changed;
}
