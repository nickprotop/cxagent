using CxAgent.Core.Commands;
using CxAgent.Core.Sandbox;

namespace CxAgent.Core.Sessions;

/// <summary>The session's side of the sandbox: what it says about it, and taking a changed setting.</summary>
public sealed partial class Session
{
    /// <summary>
    /// Says, once per session, that the model's commands are not contained — the sandbox is on and this machine
    /// has none, or it will not start. A working sandbox says nothing: it is the default, not news.
    ///
    /// <para>ONCE, because the condition does not change while the session runs, and a line per command would bury
    /// the transcript. The flag rides the sandbox state across a re-wire, so a /model switch does not repeat it.</para>
    /// </summary>
    public void NoteSandbox()
    {
        if (Policy?.Sandbox is { } sandbox) NoteSandbox(sandbox);
    }

    /// <summary>
    /// The same, for the sandbox state the wiring just built.
    ///
    /// <para>HANDED IN, NOT LOOKED UP. The wiring starts this off the calling thread before the session holds its
    /// policy, and a probe that answers at once — bubblewrap failing in milliseconds — would otherwise find no policy
    /// and say nothing, while the status bar, which reads later, says "sandbox failed".</para>
    /// </summary>
    /// <param name="sandbox">The state to report on.</param>
    public void NoteSandbox(SandboxState sandbox)
    {
        if (sandbox.FallbackNoticeShown) return;
        var status = sandbox.Status;
        var fix = status.Fix is { Length: > 0 } f ? " " + f : "";
        var line = status.Kind switch
        {
            SandboxStatusKind.Unavailable => $"Commands are not contained: {status.Reason}.{fix}",
            SandboxStatusKind.Failed =>
                $"The sandbox could not start: {status.Reason}.{fix} Each command asks before it runs uncontained.",
            _ => null,
        };
        if (line is null) return;
        sandbox.FallbackNoticeShown = true;
        Say(new Message(line, Severity.Warning));
    }

    /// <summary>
    /// Takes a changed sandbox setting now, for this session — what <c>/sandbox on</c> and <c>off</c> mean: the user
    /// typed it in this tab and expects this tab's next command to follow it.
    /// </summary>
    /// <param name="settings">The settings as saved.</param>
    public void UseSandbox(SandboxSettings settings)
    {
        if (Policy is not { } policy) return;
        var shown = policy.Sandbox?.FallbackNoticeShown ?? false;
        policy.Sandbox = SandboxState.ForThisMachine(settings, Services?.GlobalInstructionsDir);
        policy.Sandbox.FallbackNoticeShown = shown;
        policy.Sandbox.Warn = NotifyWarning;
    }

    /// <summary>
    /// /sandbox review: the user has looked at what a contained command changed, and contained commands run without
    /// asking again.
    /// </summary>
    public CommandStatus ReviewSandbox()
    {
        if (Policy?.Sandbox is not { } sandbox)
        {
            Say(new Message("There is no sandbox in this session to review.", Severity.Warning));
            return CommandStatus.Reported;
        }
        var was = sandbox.Tampered;
        sandbox.Review();
        Say(new Message(was is null
            ? "Nothing to review: no contained command changed what runs later."
            : $"Reviewed ({was}). Contained commands run without asking again.", Severity.Info));
        return CommandStatus.Reported;
    }

    /// <summary>/sandbox: whether commands are contained here, and what a contained command may touch.</summary>
    public CommandStatus DescribeSandbox()
    {
        Say(new Message(SandboxText(), Severity.Info));
        return CommandStatus.Reported;
    }

    private string SandboxText()
    {
        if (Policy?.Sandbox is not { } sandbox || !sandbox.Settings.Enabled)
            return "Sandbox: off — the model's shell commands run as you. `/sandbox on` turns it on.";

        var status = sandbox.Status;
        if (status.Kind != SandboxStatusKind.Active)
        {
            var fix = status.Fix is { Length: > 0 } f ? "\n" + f : "";
            return status.Kind == SandboxStatusKind.Failed
                ? $"Sandbox: could not start — {status.Reason}.{fix}\nEach command asks before it runs uncontained."
                : $"Sandbox: not available — {status.Reason}.{fix}";
        }

        var layout = SandboxLayout.For(new SandboxLayoutRequest(Policy.Root, Policy.Root, false, null, sandbox.Home, sandbox.Settings));
        static string List(IEnumerable<string> paths) =>
            string.Join("\n", paths.Select(p => $"  - `{p}`")) is { Length: > 0 } text ? text : "  - (none)";
        var tampered = sandbox.Tampered is { } t
            ? $"A contained command changed what runs later: {t}. Contained commands ask until you run `/sandbox review`.\n"
            : "";
        return "Sandbox: active — the model's shell commands run inside bubblewrap.\n" + tampered
            + $"Writable:\n{List(layout.Writable)}\n"
            + $"Read-only inside them (they run code later):\n{List(layout.ReadOnly)}\n"
            + $"Seen empty:\n{List(layout.HiddenFolders.Concat(layout.HiddenFiles))}\n"
            + $"network: {(layout.Network ? "on — a command can still send what it reads" : "off")}\n"
            + "A command asks to leave with `unsandboxed`; only you can say yes.";
    }
}
