using System.Diagnostics;

namespace CxAgent.Core.Sandbox;

/// <summary>
/// cxagent's own git calls — the status panel, /diff, the ignore check — with the repository's fsmonitor and hooks off.
///
/// <para>A CONTAINED COMMAND CAN REDIRECT GIT (see <see cref="GitGuard"/>) to a config naming a fsmonitor, which git
/// runs on a plain <c>status</c>. The app runs status on its own, outside any sandbox, every few seconds; it must not
/// be what runs that command. Set through GIT_CONFIG_COUNT so it reaches git however the call spells its arguments, and
/// counted after any the environment already holds.</para>
/// </summary>
public static class GitSafe
{
    /// <summary>Turns off <c>core.fsmonitor</c> and <c>core.hooksPath</c> for this one git process.</summary>
    /// <param name="info">The git process about to start.</param>
    public static ProcessStartInfo Harden(ProcessStartInfo info)
    {
        var count = int.TryParse(info.Environment.TryGetValue("GIT_CONFIG_COUNT", out var c) ? c : null, out var n) ? n : 0;
        void Add(string key, string value)
        {
            info.Environment[$"GIT_CONFIG_KEY_{count}"] = key;
            info.Environment[$"GIT_CONFIG_VALUE_{count}"] = value;
            count++;
        }
        Add("core.fsmonitor", "false");
        Add("core.hooksPath", "/dev/null");
        info.Environment["GIT_CONFIG_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return info;
    }
}
