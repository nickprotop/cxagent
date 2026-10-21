using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.RepoMap;

/// <summary>
/// A map of a codebase for a model that has not read it.
///
/// <para>WHY THIS IS A PLUGIN AT ALL. A model finding its way around a repository lists folders and
/// opens files, and every file opened to learn that it was the wrong one is context spent. One call
/// that returns the declarations that matter most, each with a line number, replaces most of that
/// wandering — the same idea as Aider's repository map, without its parser.</para>
///
/// <para>STANDALONE ON PURPOSE. No language server and no native parser: the outline comes from
/// reading the source as text, which works the moment the plugin is installed, in any repository,
/// for a dozen languages at once. A language server answers more precisely about one language and
/// only after it has indexed; a map is wanted before either.</para>
/// </summary>
public sealed class RepoMapPlugin : IPlugin
{
    private IPluginContext? _context;

    /// <summary>THE SIDECAR IS THE MANIFEST, parsed and returned rather than restated — see clone-finder,
    /// which records why two hand-kept copies of one manifest is how they come to disagree.</summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;

        // BESIDE THIS ASSEMBLY, not AppContext.BaseDirectory: that is the host's folder, and a
        // plugin is loaded from wherever it was installed.
        var here = Path.GetDirectoryName(typeof(RepoMapPlugin).Assembly.Location)!;
        var parsed = PluginManifest.Parse(File.ReadAllText(Path.Combine(here, "repo-map.plugin.json")));
        var manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"repo-map.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");
        return Task.FromResult(manifest);
    }

    /// <summary>Nothing to start: a map is made per call, from the files as they are then.</summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Nothing to stop. The git process a call starts has exited before the call returns.</summary>
    public Task Stop(CancellationToken ct) => Task.CompletedTask;

    public async Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        if (toolName != "repo_map") return Failed($"this plugin has no tool named '{toolName}'.");

        // THE CALLER'S FOLDER, NOT THE SESSION'S. A sub-agent working in a worktree asks for "."
        // and means its worktree; the session's folder is only the answer when the caller has none.
        var folder = context.WorkingDirectory ?? _context?.WorkingDirectory ?? Directory.GetCurrentDirectory();

        var path = call.Get<string?>("path", null);
        if (string.IsNullOrWhiteSpace(path)) path = ".";

        var root = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
        if (!Directory.Exists(root)) return Failed($"repo_map found no folder at '{path}' (resolved to '{root}').");

        // INSIDE THE CALLER'S FOLDER, judged where the path lands. This tool asks nobody's
        // permission, so it must not be a way to read what the caller's own file tools would have to
        // ask about — a map of someone's home folder is a listing of it.
        if (!Contains(RealPath(folder), RealPath(root)))
            return Failed($"repo_map maps folders inside yours ({folder}); '{path}' is outside it.");

        var focus = (call.Get<string?>("focus", null) ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var tokens = Math.Clamp(call.Get("max_tokens", Map.DefaultTokens), Map.MinTokens, Map.MaxTokens);

        var map = await Map.Build(new MapRequest(root, path, focus, tokens), Register, ct);

        // Under "content" because that is the key the host renders bare to the model.
        return new JobResult { Success = true, Output = new Dictionary<string, object?> { ["content"] = map } };
    }

    /// <summary>The git process that lists files is this plugin's child — registered so the host can reap it if this process dies first.</summary>
    private void Register(int processId) => _context?.RegisterChildProcess(processId);

    private static JobResult Failed(string message) => new() { Success = false, ErrorMessage = message };

    private static bool Contains(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var withSeparator = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Equals(folder, comparison) || path.StartsWith(withSeparator, comparison);
    }

    /// <summary>
    /// The folder a path really is, every symlink along it followed.
    ///
    /// <para><see cref="Path.GetFullPath(string)"/> only collapses <c>..</c>; a link inside the
    /// caller's folder that points out of it would pass a check on the spelling and map whatever it
    /// points at.</para>
    /// </summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (IOException)
            {
                // A broken link resolves to nothing; the spelling stands, and Directory.Exists
                // has already refused a folder that is not there.
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
