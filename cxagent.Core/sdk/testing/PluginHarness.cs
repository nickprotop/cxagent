using System.Text.Json;
using CxAgent.Core.Plugins;

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
        if (problems.Count == 0) await LoadProblems(found, options ?? new(), problems, ct).ConfigureAwait(false);
        return problems;
    }

    private static void StaticProblems(PluginManifest sidecar, PluginFolder found, List<PluginProblem> problems)
    {
        if (PluginContract.Refusal(sidecar, found.Sidecar) is { } contract)
            problems.Add(new("pluginContract", contract));
        if (PluginContractUse.Refusal(sidecar) is { } use)
            problems.Add(new(FieldOf(use), use));

        var isAssembly = PluginResolver.IsDotNetAssembly(found.Binary);
        if (sidecar.Kind == PluginKind.Native && isAssembly)
            problems.Add(new("kind", $"\"native\", but {Path.GetFileName(found.Binary)} is a .NET assembly."));
        if (sidecar.Kind == PluginKind.Managed && !isAssembly)
            problems.Add(new("kind", $"\"managed\", but {Path.GetFileName(found.Binary)} is not a .NET assembly."));

        for (var i = 0; i < sidecar.Tools.Count; i++)
            if (CxAgent.Core.Jobs.Tool.IsKnown(sidecar.Tools[i].Name))
                problems.Add(new($"tools[{i}].name", $"'{sidecar.Tools[i].Name}' is a built-in tool name; a plugin cannot take it."));
    }

    // PluginContractUse REFUSES ONE RULE AT A TIME, and its message says which field —
    // "declares \"kind\" but names plugin contract 2" or "declares \"client\" …".
    private static string FieldOf(string refusal) => refusal.Contains("declares \"kind\"") ? "kind" : "client";

    private static Task LoadProblems(PluginFolder found, HarnessOptions options, List<PluginProblem> problems, CancellationToken ct) =>
        Task.CompletedTask;
}
