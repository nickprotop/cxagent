using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.CloneFinder;

/// <summary>
/// Duplication found without spending the model's context.
///
/// <para>WHY THIS IS A PLUGIN AT ALL. Hunting duplication by reading is the one way a model can do
/// it, and the most expensive: every candidate file read is context spent, and the copies worth
/// finding are exactly the ones nobody remembers writing twice. A token scan over the whole tree
/// costs the model one call and returns locations, not files.</para>
///
/// <para>ONE TOOL TAKING A WHOLE DIRECTORY, because the failure is not "are these two files
/// alike": it is not knowing which two files to ask about.</para>
/// </summary>
public sealed class CloneFinderPlugin : IPlugin
{
    /// <summary>The session's root, captured at Load: a relative <c>path</c> argument must resolve
    /// against the directory the USER is working in, not against wherever the host process happens
    /// to have its own current directory — csharp-lsp resolves its file arguments the same way.</summary>
    private string _workingDirectory = "";

    /// <summary>
    /// THE SIDECAR IS THE MANIFEST, parsed and returned rather than restated. One JSON, true by
    /// construction — the host refuses a plugin whose code disagrees with the file it read before
    /// loading, and two hand-maintained copies is how that disagreement arrives.
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _workingDirectory = context.WorkingDirectory;

        // BESIDE THIS ASSEMBLY, not AppContext.BaseDirectory: that is the host's folder, and a
        // plugin is loaded from wherever it was installed.
        var here = Path.GetDirectoryName(typeof(CloneFinderPlugin).Assembly.Location)!;
        var sidecar = Path.Combine(here, "clone-finder.plugin.json");

        var parsed = PluginManifest.Parse(File.ReadAllText(sidecar));
        var manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"clone-finder.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");

        return Task.FromResult(manifest);
    }

    /// <summary>Nothing to start: the scan is a function, not a service.</summary>
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Nothing to stop, for the same reason.</summary>
    public Task Stop(CancellationToken ct) => Task.CompletedTask;

    public async Task<JobResult> Invoke(
        string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        // A FAILED CALL RATHER THAN A THROW: this plugin has exactly one tool, so the only way to
        // reach this line is a caller that is not the registry, and telling it what happened beats
        // an exception it did not expect — the same choice CalculatorPlugin records at length.
        if (!string.Equals(toolName, "find_clones", StringComparison.Ordinal))
            return new JobResult
            {
                Success = false,
                ErrorMessage = $"this plugin has no tool named '{toolName}'.",
            };

        // A MISSING ARGUMENT IS A FAILED CALL, NOT AN EXCEPTION. JobParameters.Get throws
        // KeyNotFoundException on an absent key, and a model that omitted the argument can fix
        // that if told — where an unhandled exception just reports that the tool broke. Only
        // `path` can fail this way: the other four arguments have defaults and are read with the
        // defaulting accessor below.
        string path;
        try
        {
            path = call.Get<string>("path");
        }
        catch (KeyNotFoundException)
        {
            return new JobResult
            {
                Success = false,
                ErrorMessage = "find_clones needs a 'path' argument — the directory to scan, "
                             + "like . or src/.",
            };
        }

        string root = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(_workingDirectory, path));

        // A wrong directory is the model's mistake to fix, so it fails like a wrong argument —
        // Scanner would otherwise surface it as a DirectoryNotFoundException, a broken tool
        // rather than a correctable call.
        if (!Directory.Exists(root))
            return new JobResult
            {
                Success = false,
                ErrorMessage = $"find_clones found no directory at '{path}' (resolved to '{root}').",
            };

        // The defaults come FROM ScanRequest rather than being restated here: the record is the
        // one place 6/50/20 is written down, and a second copy in these Get calls is how the
        // schema's documented defaults and the actual ones drift apart.
        var defaults = new ScanRequest(root);
        var request = defaults with
        {
            MinLines = call.Get("min_lines", defaults.MinLines),
            MinTokens = call.Get("min_tokens", defaults.MinTokens),
            Exclude = call.Get<string?>("exclude", null),
            MaxResults = call.Get("max_results", defaults.MaxResults),
        };

        var sources = new List<CloneSource>();
        foreach (string file in Scanner.Files(request))
        {
            string text = await File.ReadAllTextAsync(file, ct);
            // RELATIVE TO THE SCANNED ROOT: the report exists to send the reader to the code, and
            // the path the caller can open is the one under the directory it named — a machine's
            // absolute prefix is noise on every line of the report.
            sources.Add(new CloneSource(Path.GetRelativePath(root, file), Tokenizer.Normalise(text), text));
        }

        var clones = Detector.Find(sources, new CloneQuery(request.MinLines, request.MinTokens));

        // belowMinimum: 0 — the detector drops sub-floor repeats without counting them, so there
        // is no number to pass; Render only prints that footnote when the count is nonzero, and
        // zero states nothing false where a guessed count would.
        string report = Report.Render(clones, request.MaxResults, belowMinimum: 0);

        return new JobResult
        {
            Success = true,
            // Under "content" because that is the key the host renders bare to the model — a
            // result under any other key still reaches it, but as `key: value` noise around the
            // one string that is the whole answer.
            Output = new Dictionary<string, object?> { ["content"] = report },
        };
    }
}
