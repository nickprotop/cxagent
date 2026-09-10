using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Triggers;

/// <summary>
/// Work that starts without anybody typing — on a clock, or when a process exits.
///
/// <para>THE FIRST PRODUCTION CONSUMER OF CONTRACT 3. It declares the client, so
/// <see cref="IPluginContext.Client"/> is non-null and a fire can submit into the session that
/// created the trigger. Everything else it needs already shipped: commands on both loaders, a
/// <see cref="IPluginContext.Lifetime"/> that actually cancels so a timer dies with its session, and
/// a sever that outranks a turn the plugin started.</para>
///
/// <para>A TRIGGER DIES WITH THE PROCESS, and that is not a choice this plugin gets to make. A
/// session is a folder since phase one and a half, and IPluginContext carries no path to it — so
/// there is nowhere to write that would be deleted with the conversation. Phase three owns
/// durability, because a daemon has to answer missed-fire policy anyway.</para>
/// </summary>
public sealed class TriggersPlugin : IPlugin, IPluginClientConsumer
{
    private IPluginContext? _context;

    /// <summary>
    /// THE SIDECAR IS THE MANIFEST, parsed and returned rather than restated — see CalculatorPlugin
    /// for the same shape. One JSON, true by construction.
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;

        // THE HOST'S CONTRACT IS CHECKED FROM THIS SIDE TOO. A host OLDER than contract 3 cannot
        // refuse what it has never heard of: it reads the manifest with its own rules and a field it
        // does not recognise becomes whatever its parser falls back to. Throwing here fails the load
        // cleanly and says why, which is the only honest outcome.
        if (context.HostContract < 3)
            throw new InvalidOperationException(
                $"triggers needs plugin contract 3; this host speaks {context.HostContract}. "
                + "It submits into its session, which contract 2 has no way to express.");

        // BESIDE THIS ASSEMBLY, not AppContext.BaseDirectory: that is the host's folder, and a
        // plugin is loaded from wherever it was installed.
        var here = Path.GetDirectoryName(typeof(TriggersPlugin).Assembly.Location)!;
        var sidecar = Path.Combine(here, "triggers.plugin.json");

        var parsed = PluginManifest.Parse(File.ReadAllText(sidecar));
        var manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"triggers.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");

        return Task.FromResult(manifest);
    }

    /// <summary>
    /// Opens what it needs and RETURNS, holding nothing.
    ///
    /// <para>Session.LoadPlugin awaits this before it reports the load, so a plugin that held its
    /// timer by not returning would hang the session that asked for it. The timer lives on a task
    /// this plugin owns, ended by Lifetime cancelling at Stop.</para>
    /// </summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
        CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = false, ErrorMessage = $"unknown tool '{toolName}'" });

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
