using System.Text.Json;

namespace CxAgent.Core.Plugins;

/// <summary>Where a plugin sends what it wants recorded — a plugin that cannot say why it failed is undebuggable.</summary>
public interface IPluginLogger
{
    void Log(string message);
}

/// <summary>
/// Everything a plugin is handed at Load, and nothing else — see PLUGINS.md, "What a plugin is
/// handed at Load".
///
/// <para>NO TRANSCRIPT, NO MODEL, NO PERMISSION STORE. A plugin sees the arguments of its own calls
/// and nothing else: it cannot read the user's messages, the model's replies, or another tool's
/// result, and it cannot start a turn or grant itself a permission. Those are not oversights to be
/// added later — PLUGINS.md, "What a plugin is not" states passive transcript reading is removed on
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
    /// <para>NOT THE PLUGIN'S OWN BOOKKEEPING. PLUGINS.md, "Lifecycle": a plugin that crashed is a
    /// plugin that cannot clean up after itself, which is the entire scenario reaping exists for.
    /// Calling this is not optional for a plugin that spawns a child process — an unregistered child
    /// is exactly the leak the pid record exists to close.</para>
    /// </summary>
    void RegisterChildProcess(int processId);
}
