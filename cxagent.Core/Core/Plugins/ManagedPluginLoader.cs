using System.Reflection;
using System.Text.Json;

namespace CxAgent.Core.Plugins;

/// <summary>
/// What a load attempt produced — an <see cref="IPlugin"/> instance past its own <c>Load</c> call,
/// or a reason it never got there. See PLUGINS.md, "Permission" and "Failure": a load failure is
/// reported, never silent, because the load gate is the only place Core can tell the user what a
/// plugin is before trusting it — a plugin that failed to say why defeats that at the first hurdle.
/// </summary>
public abstract record ManagedPluginLoadResult
{
    private ManagedPluginLoadResult() { }

    /// <summary>The assembly loaded, exactly one <see cref="IPlugin"/> was found and constructed,
    /// and its Load call returned a manifest matching the sidecar.</summary>
    public sealed record Loaded(IPlugin Instance, PluginManifest Manifest) : ManagedPluginLoadResult;

    /// <summary>Any reason the plugin is not running — a bad path, a missing or malformed sidecar,
    /// zero or multiple <see cref="IPlugin"/> types, a constructor or Load failure, or a manifest
    /// that does not match its sidecar. <see cref="Reason"/> names which one.</summary>
    public sealed record Failed(string Reason) : ManagedPluginLoadResult;
}

/// <summary>
/// Constructs an <see cref="IPlugin"/> from a managed assembly on disk — the first of the two v1
/// loaders (PLUGINS.md, "The v1 cut": "Both loaders ship in v1. Managed in-process and ABI
/// out-of-process, against one contract.").
///
/// <para>NO <see cref="System.Runtime.Loader.AssemblyLoadContext"/> OF ITS OWN, DELIBERATELY.
/// PLUGINS.md's lifecycle is Unwire, not Unload, and says why: "A managed plugin's assembly cannot
/// be removed from the process without loading it into an AssemblyLoadContext of its own, and even
/// then only if nothing outlives it holding a reference." Giving this loader an ALC would let a
/// plugin's assembly actually be collected, which is a promise Unwire does not make and this loader
/// must not start implying — it would be scope this task was not asked to build, and it would change
/// what every other part of the system may assume Unwire means. The assembly loads into the default
/// context and stays resident for the process's life; that cost is accepted, in writing, by the
/// document this loader implements.</para>
/// </summary>
public static class ManagedPluginLoader
{
    /// <summary>
    /// Loads the plugin at <paramref name="assemblyPath"/>: reads its sidecar manifest, loads the
    /// assembly, finds and constructs its one <see cref="IPlugin"/> implementation, calls
    /// <see cref="IPlugin.Load"/>, and checks the returned manifest against the sidecar before
    /// handing anything back.
    /// </summary>
    /// <param name="assemblyPath">Path to the plugin's entry-point <c>.dll</c>.</param>
    /// <param name="context">Handed to <see cref="IPlugin.Load"/> unchanged.</param>
    /// <param name="ct">Passed to <see cref="IPlugin.Load"/>.</param>
    public static async Task<ManagedPluginLoadResult> Load(string assemblyPath, IPluginContext context,
        CancellationToken ct)
    {
        if (!File.Exists(assemblyPath))
            return new ManagedPluginLoadResult.Failed($"no plugin assembly at '{assemblyPath}'.");

        // THE SIDECAR SITS BESIDE THE DLL, NAMED FROM IT — '<plugin>.dll' pairs with
        // '<plugin>.plugin.json', so a directory holding several plugins does not need a naming
        // scheme invented here; the pairing is the same stem, one extra suffix.
        var sidecarPath = Path.ChangeExtension(assemblyPath, null) + ".plugin.json";
        if (!File.Exists(sidecarPath))
            return new ManagedPluginLoadResult.Failed(
                $"no sidecar manifest at '{sidecarPath}' for plugin '{assemblyPath}'.");

        string sidecarJson;
        try
        {
            sidecarJson = await File.ReadAllTextAsync(sidecarPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ManagedPluginLoadResult.Failed($"could not read sidecar manifest '{sidecarPath}': {ex.Message}");
        }

        var parsed = PluginManifest.Parse(sidecarJson);
        if (parsed.Manifest is null)
            return new ManagedPluginLoadResult.Failed(
                $"sidecar manifest '{sidecarPath}' is invalid: {string.Join("; ", parsed.Errors)}");
        // A REFUSED KIND STILL FAILS THE LOAD, even though PluginManifest.Parse leaves a manifest in
        // place for the parts it could read — see PluginManifest.Parse's own doc. That forward
        // compatibility is for a FUTURE build reading an old manifest; a plugin declaring a kind
        // THIS build cannot service must not load as if the declaration silently succeeded.
        if (!parsed.IsSuccess)
            return new ManagedPluginLoadResult.Failed(
                $"sidecar manifest '{sidecarPath}' declares more than this build services: {string.Join("; ", parsed.Errors)}");

        var sidecar = parsed.Manifest;

        Assembly assembly;
        try
        {
            // NO ALC — see this type's own doc. LoadFrom is the default-context load, resident for
            // the process's life, exactly what Unwire's contract already accepts.
            assembly = Assembly.LoadFrom(assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            return new ManagedPluginLoadResult.Failed($"could not load assembly '{assemblyPath}': {ex.Message}");
        }

        List<Type> pluginTypes;
        try
        {
            pluginTypes = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t))
                .ToList();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A DEPENDENCY FAILED TO RESOLVE. GetTypes() throws for the WHOLE assembly rather than
            // returning the types that did load, so the only honest report names the assembly and
            // surfaces the loader exceptions rather than guessing which type would have mattered.
            var inner = string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message ?? "unknown"));
            return new ManagedPluginLoadResult.Failed(
                $"could not inspect types in '{assemblyPath}': {inner}");
        }

        switch (pluginTypes.Count)
        {
            case 0:
                return new ManagedPluginLoadResult.Failed(
                    $"'{assemblyPath}' contains no {nameof(IPlugin)} implementation.");
            case > 1:
                // DO NOT GUESS WHICH ONE WAS MEANT — an assembly with two IPlugin types is ambiguous
                // by construction, and picking the first by reflection order would load a different
                // plugin on a different runtime or a different build, silently.
                return new ManagedPluginLoadResult.Failed(
                    $"'{assemblyPath}' contains more than one {nameof(IPlugin)} implementation: "
                    + string.Join(", ", pluginTypes.Select(t => t.FullName)));
        }

        var pluginType = pluginTypes[0];

        IPlugin instance;
        try
        {
            // PARAMETERLESS ONLY. A plugin gets everything it needs through IPluginContext at Load,
            // not through its constructor — the same reason IPluginContext exists rather than a
            // constructor argument list nobody could version.
            instance = (IPlugin)(Activator.CreateInstance(pluginType)
                ?? throw new InvalidOperationException("Activator.CreateInstance returned null"));
        }
        catch (Exception ex) when (ex is MissingMethodException or InvalidOperationException
            or TargetInvocationException or MemberAccessException)
        {
            return new ManagedPluginLoadResult.Failed(
                $"could not construct '{pluginType.FullName}' from '{assemblyPath}': {ex.Message}");
        }

        PluginManifest loaded;
        try
        {
            loaded = await instance.Load(context, ct);
        }
        catch (Exception ex)
        {
            return new ManagedPluginLoadResult.Failed(
                $"'{sidecar.Name}' threw from Load: {ex.Message}");
        }

        // THE SIDECAR AND WHAT LOAD RETURNS MUST MATCH — PLUGINS.md is explicit that otherwise the
        // file the user was asked to approve describes something other than what runs, and the load
        // gate's promise is void. Checked here, before anything is handed to the registry, so a
        // mismatched plugin never reaches a caller that would trust either description.
        var difference = DescribeMismatch(sidecar, loaded);
        if (difference is not null)
            return new ManagedPluginLoadResult.Failed(
                $"'{assemblyPath}' does not match its sidecar manifest '{sidecarPath}': {difference}");

        return new ManagedPluginLoadResult.Loaded(instance, loaded);
    }

    /// <summary>
    /// Names the first difference between the sidecar manifest and what <see cref="IPlugin.Load"/>
    /// actually returned, or null when they agree. Compares every field the sidecar can declare —
    /// a match on name and tool names alone would let a plugin's description or gating policy drift
    /// from what the user was shown at approval time without tripping this check.
    /// </summary>
    private static string? DescribeMismatch(PluginManifest sidecar, PluginManifest loaded)
    {
        if (sidecar.Name != loaded.Name)
            return $"sidecar names '{sidecar.Name}', Load returned '{loaded.Name}'.";
        if (sidecar.Version != loaded.Version)
            return $"sidecar declares version '{sidecar.Version}', Load returned '{loaded.Version}'.";
        if (sidecar.Instructions != loaded.Instructions)
            return "sidecar and Load disagree on 'instructions'.";
        if (sidecar.Spawns != loaded.Spawns)
            return $"sidecar declares spawns={sidecar.Spawns}, Load returned spawns={loaded.Spawns}.";

        var sidecarTools = sidecar.Tools.ToDictionary(t => t.Name);
        var loadedTools = loaded.Tools.ToDictionary(t => t.Name);

        var sidecarOnly = sidecarTools.Keys.Except(loadedTools.Keys).ToList();
        if (sidecarOnly.Count > 0)
            return $"sidecar declares tool(s) Load did not return: {string.Join(", ", sidecarOnly)}.";

        var loadedOnly = loadedTools.Keys.Except(sidecarTools.Keys).ToList();
        if (loadedOnly.Count > 0)
            return $"Load returned tool(s) the sidecar did not declare: {string.Join(", ", loadedOnly)}.";

        foreach (var (name, sidecarTool) in sidecarTools)
        {
            var loadedTool = loadedTools[name];
            if (sidecarTool.Description != loadedTool.Description)
                return $"tool '{name}': sidecar and Load disagree on 'description'.";
            if (sidecarTool.Gated != loadedTool.Gated)
                return $"tool '{name}': sidecar declares gated={sidecarTool.Gated}, Load returned gated={loadedTool.Gated}.";
            if (!SchemaEquals(sidecarTool.InputSchema, loadedTool.InputSchema))
                return $"tool '{name}': sidecar and Load disagree on 'inputSchema'.";
        }

        return null;
    }

    /// <summary>
    /// Structural equality for two JSON Schema documents, ignoring formatting and object-key order.
    ///
    /// <para>NOT <c>GetRawText()</c>. The sidecar comes from a human-edited file and the running
    /// plugin's Load call typically builds its schema in code — <c>{ "type": "object" }</c> from the
    /// sidecar and a compact <c>{"type":"object"}</c> from <see cref="JsonSerializer"/> are the SAME
    /// schema, and comparing raw text would refuse every plugin whose sidecar was merely
    /// pretty-printed. <see cref="JsonElement"/> carries no built-in deep-equals, so this walks both
    /// trees by hand.</para>
    /// </summary>
    private static bool SchemaEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToList();
                var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                if (aProps.Count != bProps.Count) return false;
                return aProps.All(p => bProps.TryGetValue(p.Name, out var bv) && SchemaEquals(p.Value, bv));

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                return aItems.Count == bItems.Count
                    && aItems.Zip(bItems, SchemaEquals).All(equal => equal);

            case JsonValueKind.String:
                return a.GetString() == b.GetString();

            case JsonValueKind.Number:
                // COMPARED AS TEXT, not as a parsed double: a JSON Schema never needs float
                // tolerance, and text comparison avoids 1 vs 1.0 silently passing as equal when a
                // plugin author would reasonably expect them written identically.
                return a.GetRawText() == b.GetRawText();

            default:
                // True, False, Null, Undefined — ValueKind equality above already decided these.
                return true;
        }
    }
}
