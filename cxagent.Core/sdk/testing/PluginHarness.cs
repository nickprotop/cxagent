using System.Text.Json;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Abi;

namespace CxAgent.Plugins.Testing;

/// <summary>Where a harness looks and what it hands the plugin. Every member is optional.</summary>
public sealed record HarnessOptions
{
    /// <summary>The plugin host for a native plugin. Default: the one this package copies beside the
    /// test, then the one beside the cxagent executable.</summary>
    public string? HostDllPath { get; init; }

    /// <summary>The plugin's <c>settings</c> block, as a user would write it in config.json. Default: <c>{}</c>.</summary>
    public JsonElement? Settings { get; init; }

    /// <summary>The folder the plugin works in. Default: the current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>How long disposing waits for the plugin's Stop before abandoning it. Default: 10 seconds.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>The plugin could not be loaded and started; <see cref="Problems"/> says why, one line each.</summary>
public sealed class PluginLoadException(IReadOnlyList<PluginProblem> problems)
    : Exception(string.Join(Environment.NewLine, problems))
{
    public IReadOnlyList<PluginProblem> Problems { get; } = problems;
}

/// <summary>
/// Checks and runs a plugin folder the way a session would, with no session: the same sidecar rules,
/// the same loaders, the same gate decision.
/// </summary>
public static partial class PluginHarness
{
    /// <summary>Every problem with the plugin in <paramref name="folder"/>, one per line; empty when it loads.</summary>
    public static async Task<IReadOnlyList<PluginProblem>> CheckAsync(
        string folder, HarnessOptions? options = null, CancellationToken ct = default)
    {
        var problems = new List<PluginProblem>();
        if (PluginFolder.Find(folder, problems) is not { } found) return problems;

        var parsed = PluginManifest.Parse(File.ReadAllText(found.Sidecar));
        problems.AddRange(parsed.Errors.Select(e => new PluginProblem("sidecar", e)));
        if (parsed.Manifest is not { } sidecar) return problems;

        StaticProblems(sidecar, found, problems);

        // THE LOAD STAGE RUNS ONLY ON A CLEAN SIDECAR: the loaders refuse on the first problem, and
        // their refusal would repeat one already listed above.
        if (problems.Count == 0) await LoadProblems(found, sidecar, options ?? new(), problems, ct).ConfigureAwait(false);
        return problems;
    }

    /// <summary>
    /// Loads and starts the plugin in <paramref name="folder"/> — the sidecar checks, the loader a
    /// session would pick, then Start. Throws <see cref="PluginLoadException"/> naming every problem
    /// when it cannot.
    /// </summary>
    public static async Task<HarnessPlugin> LoadAsync(string folder, HarnessOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        var problems = new List<PluginProblem>();
        if (PluginFolder.Find(folder, problems) is not { } found) throw new PluginLoadException(problems);

        var parsed = PluginManifest.Parse(File.ReadAllText(found.Sidecar));
        problems.AddRange(parsed.Errors.Select(e => new PluginProblem("sidecar", e)));
        if (parsed.Manifest is { } sidecar) StaticProblems(sidecar, found, problems);
        if (problems.Count > 0 || parsed.Manifest is null) throw new PluginLoadException(problems);

        // A CLIENT ONLY WHEN THE SIDECAR ASKS FOR ONE — as a session hands it — so a plugin that
        // forgot to declare it finds Client null here too, rather than passing a test and failing live.
        var client = parsed.Manifest.Client ? new RecordingClient() : null;
        var context = NewContext(options, client);
        switch (await LoadInto(found, context, options, ct).ConfigureAwait(false))
        {
            case LoadOutcome.Failed f:
                context.Dispose();
                throw new PluginLoadException([new("load", f.Reason)]);
            case LoadOutcome.Loaded l:
                try
                {
                    await l.Instance.Start(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // STOPPED AND REAPED BEFORE THE THROW, whatever was thrown: a Start that failed
                    // half way may already hold a process, and the caller gets no plugin to dispose.
                    context.EndLifetime();
                    await HarnessPlugin.StopWithin(l.Instance, options.StopTimeout).ConfigureAwait(false);
                    context.Dispose();

                    // THE CALLER'S OWN CANCELLATION STAYS A CANCELLATION; anything else — a plugin's
                    // OperationCanceledException of its own included — is the plugin failing to start.
                    if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                    throw new PluginLoadException([new("start", ex.Message)]);
                }

                return new HarnessPlugin(l.Instance, l.Manifest, context, client, options.StopTimeout);
            default:
                context.Dispose();
                throw new InvalidOperationException("unknown load result");
        }
    }

    private static void StaticProblems(PluginManifest sidecar, PluginFolder found, List<PluginProblem> problems)
    {
        if (PluginContract.Refusal(sidecar, found.Sidecar) is { } contract)
            problems.Add(new("pluginContract", contract));
        // ONE LINE PER RULE BROKEN, where PluginContractUse.Refusal — which a loader stops at — names
        // only the first. Same thresholds, so the two cannot disagree on whether a sidecar loads.
        if (sidecar.Client && sidecar.Contract < PluginContractUse.ClientSince)
            problems.Add(new("client", $"declared, but the plugin names contract {sidecar.Contract}; the client arrived in contract {PluginContractUse.ClientSince}, so declare {PluginContractUse.ClientSince}."));
        if (sidecar.Kind is not null && sidecar.Contract < PluginContractUse.KindSince)
            problems.Add(new("kind", $"declared, but the plugin names contract {sidecar.Contract}; \"kind\" arrived in contract {PluginContractUse.KindSince}, so declare {PluginContractUse.KindSince}."));

        var isAssembly = PluginResolver.IsDotNetAssembly(found.Binary);
        if (sidecar.Kind == PluginKind.Native && isAssembly)
            problems.Add(new("kind", $"\"native\", but {Path.GetFileName(found.Binary)} is a .NET assembly."));
        if (sidecar.Kind == PluginKind.Managed && !isAssembly)
            problems.Add(new("kind", $"\"managed\", but {Path.GetFileName(found.Binary)} is not a .NET assembly."));

        for (var i = 0; i < sidecar.Tools.Count; i++)
            if (CxAgent.Core.Jobs.Tool.IsKnown(sidecar.Tools[i].Name))
                problems.Add(new($"tools[{i}].name", $"'{sidecar.Tools[i].Name}' is a built-in tool name; a plugin cannot take it."));

        // THE COMMAND TABLE A SESSION SEEDS: every built-in, and every name the app attaches a handler
        // to, is one of these — so a plugin command named like one is refused at load.
        for (var i = 0; i < sidecar.Commands.Count; i++)
        {
            var typed = "/" + sidecar.Commands[i].Name;
            if (CxAgent.Core.Commands.SessionCommands.All.Any(c => c.Name == typed))
                problems.Add(new($"commands[{i}].name", $"'{typed}' is a built-in command; a plugin cannot take it."));
        }
    }

    internal abstract record LoadOutcome
    {
        internal sealed record Loaded(IPlugin Instance, PluginManifest Manifest) : LoadOutcome;
        internal sealed record Failed(string Reason) : LoadOutcome;
    }

    /// <summary>
    /// The plugin host a native plugin is run in. THE PACKAGE'S OWN COPY FIRST — its targets put it in
    /// <c>plugin-host/</c> beside the test, and being built with this harness it is the host
    /// AbiHostProcess's version check accepts. Then one a project reference put beside the test, then
    /// the cxagent executable's.
    /// </summary>
    internal static string HostDll(HarnessOptions options)
    {
        if (options.HostDllPath is { } given) return given;

        var bundled = Path.Combine(AppContext.BaseDirectory, "plugin-host", "cxagent-plugin-host.dll");
        if (File.Exists(bundled)) return bundled;

        var referenced = Path.Combine(AppContext.BaseDirectory, "cxagent-plugin-host.dll");
        return File.Exists(referenced) ? referenced : PluginResolver.PluginHostDllPath();
    }

    public static string HostDllForTest(HarnessOptions options) => HostDll(options);

    internal static async Task<LoadOutcome> LoadInto(
        PluginFolder found, HarnessContext context, HarnessOptions options, CancellationToken ct)
    {
        // ROUTED AS A SESSION ROUTES IT: the sidecar's kind, else the file.
        if (PluginResolver.IsNative(found.Binary))
        {
            return await AbiPluginLoader.Load(HostDll(options), found.Binary, context, ct).ConfigureAwait(false) switch
            {
                AbiPluginLoadResult.Loaded l => new LoadOutcome.Loaded(l.Instance, l.Manifest),
                AbiPluginLoadResult.Failed f => new LoadOutcome.Failed(f.Reason),
                _ => throw new InvalidOperationException("unknown load result"),
            };
        }

        return await ManagedPluginLoader.Load(found.Binary, context, ct).ConfigureAwait(false) switch
        {
            ManagedPluginLoadResult.Loaded l => new LoadOutcome.Loaded(l.Instance, l.Manifest),
            ManagedPluginLoadResult.Failed f => new LoadOutcome.Failed(f.Reason),
            _ => throw new InvalidOperationException("unknown load result"),
        };
    }

    private static async Task LoadProblems(PluginFolder found, PluginManifest sidecar, HarnessOptions options,
        List<PluginProblem> problems, CancellationToken ct)
    {
        // A CLIENT WHEN THE SIDECAR DECLARES ONE, as a session hands it — a plugin whose Load needs its
        // client would otherwise fail check and pass everywhere else.
        using var context = NewContext(options, sidecar.Client ? new RecordingClient() : null);
        switch (await LoadInto(found, context, options, ct).ConfigureAwait(false))
        {
            case LoadOutcome.Failed f:
                problems.Add(new("load", f.Reason));
                break;
            case LoadOutcome.Loaded l:
                // LOADED, NOT STARTED: check asks whether it loads; Start is call's business. Stopped
                // anyway — a native load already holds a host process. A Stop that fails or hangs
                // after a load that passed is not what check answers; the context's disposal still
                // reaps anything it left.
                context.EndLifetime();
                await HarnessPlugin.StopWithin(l.Instance, options.StopTimeout).ConfigureAwait(false);
                break;
        }
    }

    internal static HarnessContext NewContext(HarnessOptions options, IPluginClient? client) =>
        new(options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            options.Settings ?? JsonSerializer.SerializeToElement(new { }), client);
}
