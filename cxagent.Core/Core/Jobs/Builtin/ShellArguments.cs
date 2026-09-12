using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// How a shell call's arguments are read, for the arguments whose meaning more than one layer
/// depends on.
///
/// <para>ONE READER, BECAUSE TWO WOULD BE A PERMISSION BYPASS. The gate
/// (<see cref="Permissions.PermissionPolicy.RequestsFor"/>) and
/// <see cref="ShellJobExecutor"/> both have to decide whether a call is backgrounded, and they must
/// reach the same answer for the same JSON. If the executor honoured a spelling the gate did not —
/// <c>"background": "true"</c> from a model that stringifies scalars, say — the call would run
/// unattended while the gate judged it as an ordinary foreground command, and the prompt the spec
/// charges for would never appear. The failure is silent on both sides: nothing errors, the command
/// simply escapes the check. Route every reader through here rather than calling
/// <see cref="JobParameters.Get{T}(string, T)"/> at each site.</para>
/// </summary>
public static class ShellArguments
{
    /// <summary>The argument's name, so no caller has to spell it.</summary>
    public const string Background = "background";

    /// <summary>
    /// Whether this call asked to outlive the turn that issued it.
    ///
    /// <para>ABSENT MEANS FOREGROUND, and the default has to be the conservative one: a call whose
    /// arguments cannot be read as backgrounding must not be treated as backgrounded by the executor,
    /// and — more importantly — a call that IS backgrounded must never read as foreground here,
    /// because that is the reading that skips a prompt. JobParameters.Get already tolerates the type
    /// slips a model makes (<c>"true"</c>, <c>1</c>), which is the whole reason this goes through it.
    /// </para>
    /// </summary>
    public static bool IsBackground(JobParameters parameters) =>
        parameters.Get(Background, false);
}
