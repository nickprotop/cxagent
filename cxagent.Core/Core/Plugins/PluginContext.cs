using System.Text.Json;

namespace CxAgent.Core.Plugins;

/// <summary>Where a plugin sends what it wants recorded — a plugin that cannot say why it failed is undebuggable.</summary>
public interface IPluginLogger
{
    void Log(string message);
}

/// <summary>
/// Everything a plugin is handed at Load, and nothing else — see the plugin design, "What a plugin is
/// handed at Load".
///
/// <para>NO TRANSCRIPT, NO MODEL, NO PERMISSION STORE. A plugin sees the arguments of its own calls
/// and nothing else: it cannot read the user's messages, the model's replies, or another tool's
/// result, and it cannot start a turn or grant itself a permission. Those are not oversights to be
/// added later — the plugin design, "What a plugin is not" states passive transcript reading is removed on
/// purpose, and "The plugin provides its own policy; Core enforces it" states a plugin declares
/// permission gates rather than reading or writing grants itself.</para>
/// </summary>
public interface IPluginContext
{
    /// <summary>Where the plugin should root itself — an LSP plugin starts its server here.</summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// This plugin's own <c>settings</c> object from config, verbatim — not merged with anything,
    /// not validated by Core, because Core does not know this plugin's settings shape.
    /// </summary>
    JsonElement Settings { get; }

    /// <summary>
    /// The plugin contract this host speaks — the same number a plugin declares as
    /// <c>pluginContract</c> in its sidecar.
    ///
    /// <para>THE CHECK GOES BOTH WAYS, AND THIS IS THE HALF ONLY A PLUGIN CAN MAKE. The host
    /// refuses a contract it does not know before this plugin's assembly is even loaded — but a
    /// host OLDER than a contract cannot refuse what it has never heard of. It reads the newer
    /// manifest with its own rules, and a field it does not recognise becomes whatever its parser
    /// falls back to; a gating policy can go missing that way without anything failing.</para>
    ///
    /// <para>So a plugin that needs a newer host has to look. Compare this against the contract you
    /// were built for and throw from <see cref="IPlugin.Load"/> if it is lower — a throw there
    /// fails the load cleanly and says why. ZERO MEANS A HOST TOO OLD TO HAVE THIS PROPERTY: it
    /// cannot be read on a build that predates it, so a plugin reaching that build gets whatever
    /// default its own host shim supplies, and treating an absent contract as 0 makes "older than
    /// anything I know" the honest reading rather than an unanswerable one.</para>
    /// </summary>
    int HostContract { get; }

    /// <summary>
    /// The cxagent version hosting this plugin, as "major.minor.patch".
    ///
    /// <para>FOR LOGGING AND DISPLAY, NOT COMPATIBILITY. Whether this host understands you is
    /// settled by <c>pluginContract</c> before your assembly is loaded — a number compared exactly,
    /// where a version is a moving target that could be high enough while missing the very feature
    /// you needed.</para>
    ///
    /// <para>A LOCAL BUILD REPORTS "0.0.0" — the placeholder the release workflow replaces with the
    /// git tag. Treat it as "unknown, probably newest" rather than as older than everything.</para>
    /// </summary>
    string HostVersion { get; }

    /// <summary>Where this plugin logs. See <see cref="IPluginLogger"/>.</summary>
    IPluginLogger Logger { get; }

    /// <summary>
    /// Cancelled at Stop, and only at Stop.
    ///
    /// <para>THIS IS THE PLUGIN INSTANCE'S TOKEN, NOT A TURN'S. A session holds a per-turn scope,
    /// replaced each lap — there is no "the session's token" to hand out. Using a turn's token here
    /// would cancel a language server mid-index because a user pressed Escape on an unrelated
    /// question. Long-lived work started under this token survives turns and dies only with the
    /// plugin. A per-call token is handed to the executor separately by the caller, and that one IS
    /// the turn's.</para>
    /// </summary>
    CancellationToken Lifetime { get; }

    /// <summary>
    /// Registers a process this plugin spawned, so Core can record its pid and reap it — at Stop, at
    /// unwire, and at startup if the previous run never reached either.
    ///
    /// <para>NOT THE PLUGIN'S OWN BOOKKEEPING. The plugin design, "Lifecycle": a plugin that crashed is a
    /// plugin that cannot clean up after itself, which is the entire scenario reaping exists for.
    /// Calling this is not optional for a plugin that spawns a child process — an unregistered child
    /// is exactly the leak the pid record exists to close.</para>
    /// </summary>
    void RegisterChildProcess(int processId);
}
