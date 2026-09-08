using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.ExperimentManaged;

/// <summary>
/// Exercises <see cref="IPluginClient"/> and nothing else.
///
/// <para>DELIBERATELY TRIVIAL, AND NOT IN THE CATALOG. The three plugins cxagent ships — calculator,
/// clone-finder, csharp-lsp — evaluate expressions, find clones, and wrap a language server; none of
/// them declares the client capability contract 3 added, and none of them will, because none has a
/// reason to start work in its own session. Without a plugin that DOES ask for it, a drive of the
/// published set exercises contract 2 twice and contract 3 never. This plugin exists solely to be
/// driven: one tool a model can call so a drive can trigger <see cref="IPluginClient.Submit"/> from
/// inside a turn. <c>plugins.json</c> does not list it — nobody installs an experiment by accident.</para>
///
/// <para>An experimental plugin that grows features grows reasons for its own bugs, and then a
/// failed drive tells you nothing about the contract it was meant to exercise. So this stays at one
/// tool, one call, one thing reported back.</para>
/// </summary>
public sealed class ExperimentPlugin : IPlugin, IPluginClientConsumer
{
    private PluginManifest? _manifest;
    private IPluginClient? _client;
    private IPluginLogger? _log;
    private string? _workingDirectory;

    /// <summary>
    /// THE SIDECAR IS THE MANIFEST, parsed and returned rather than restated — see calculator's
    /// identical choice (<c>CalculatorPlugin.Load</c>). One JSON, true by construction: the host
    /// refuses a plugin whose code disagrees with the file it read before loading.
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        var here = Path.GetDirectoryName(typeof(ExperimentPlugin).Assembly.Location)!;
        var sidecar = Path.Combine(here, "experiment-managed.plugin.json");

        var parsed = PluginManifest.Parse(File.ReadAllText(sidecar));
        _manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"experiment-managed.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");

        // context.Client IS NULL UNLESS THE SIDECAR DECLARED "client": true — IPluginContext.Client's
        // own gate. This plugin's manifest declares it, so on a contract-3 host this is non-null; on
        // an older host it stays null and every call below reports that rather than throwing.
        _client = context.Client;
        _log = context.Logger;
        _workingDirectory = context.WorkingDirectory;

        // NAMING THE INSTANCE IN EVERY LINE, because the multi-session drive is the point: three
        // instances logging indistinguishably would prove nothing about which one acted. A plugin has
        // no session identifier of its own (IPluginContext carries none — see plugins.md, "What a
        // plugin is handed"), so the working directory is the nearest thing that tells one instance
        // from another.
        _log.Log($"experiment-managed loaded, rooted at {_workingDirectory}");

        return Task.FromResult(_manifest);
    }

    /// <summary>Nothing to start: this plugin holds no connection and spawns nothing.</summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public async Task<JobResult> Invoke(
        string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        if (!string.Equals(toolName, "experiment_submit", StringComparison.Ordinal))
            return new JobResult
            {
                Success = false,
                ErrorMessage = $"this plugin has no tool named '{toolName}'.",
            };

        if (_client is null)
            return new JobResult
            {
                Success = false,
                ErrorMessage = "no client available — the manifest did not declare it, or this host "
                             + "speaks a contract older than 3.",
            };

        string goal;
        try
        {
            goal = call.Get<string>("goal");
        }
        catch (KeyNotFoundException)
        {
            return new JobResult
            {
                Success = false,
                ErrorMessage = "experiment_submit needs a 'goal' argument — what to ask the agent to do.",
            };
        }

        var wantResult = call.Get("want_result", false);

        var result = await _client.Submit(goal, wantResult, ct);

        if (!result.Accepted)
            return new JobResult
            {
                Success = false,
                ErrorMessage = result.Refusal ?? "the session did not accept the goal.",
            };

        // result.Text IS NULL WHEN want_result WAS FALSE, OR WHEN THE TURN PRODUCED NO TEXT — either
        // way that is not an error (SubmitResult's own doc), so the two cases read as one sentence
        // rather than the caller having to guess which happened.
        return new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?>
            {
                ["content"] = result.Text is { } text
                    ? $"submitted, and the turn answered: {text}"
                    : "submitted.",
            },
        };
    }

    public Task Stop(CancellationToken ct)
    {
        _log?.Log($"experiment-managed stopping, rooted at {_workingDirectory}");
        return Task.CompletedTask;
    }
}
