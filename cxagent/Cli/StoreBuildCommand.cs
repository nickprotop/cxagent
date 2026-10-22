using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Plugins;

namespace CxAgent.Cli;

/// <summary>
/// <c>cxagent store build &lt;folder&gt; --url &lt;base&gt; --out &lt;dir&gt;</c> — a folder holding one built
/// plugin per subfolder becomes the zips and the <c>catalog.json</c> a static web server serves as a
/// plugin store.
///
/// <para>ALL OR NOTHING: a catalog with one entry the store cannot stand behind is a listing every user
/// of the store sees refused at install, so a pack problem, a field nobody filled in, a duplicate name
/// or an entry verify's shape rules reject writes no <c>catalog.json</c> at all. A half-built store is
/// worse than none, because the previous catalog in <c>--out</c> is still a working one.</para>
///
/// <para>EXIT CODES as the plugin commands: 0 built, 1 a plugin or the folder is at fault, 2 usage.</para>
/// </summary>
internal static class StoreBuildCommand
{
    private const string Usage = """
        usage: cxagent store build <folder> --url <base-url> --out <folder> [--json]

        <folder> holds one built plugin per subfolder, each with its *.plugin.json and optionally a
        pack-info.json (or .yaml) for the catalog text. A native plugin's other platforms sit in
        <subfolder>/<rid>/<sidecar-name>.so|.dylib|.dll.

        exit: 0 built, 1 a plugin is at fault, 2 usage or nothing to build
        """;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What one run works with: the request, its staging folder and where it reports.</summary>
    private sealed record BuildRun(Build Build, string Staging, TextWriter Stdout, TextWriter Stderr);

    /// <summary>What the command line asked for.</summary>
    /// <paramref name="OutShown"/> is --out as typed, which the printed paths use so a person can paste them.
    private sealed record Build(string Folder, string BaseUrl, string OutDir, string OutShown, bool Json);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (Parse(args, out var error) is not { } build) return Fail(stderr, error!);

        // EVERYTHING IS MADE IN A STAGING FOLDER INSIDE --out (same volume, so the final moves are
        // renames) AND MOVED IN ONLY WHEN ALL OF IT PASSED: --out may already be a working store, and a
        // zip written straight into it would replace a download whose hash the old catalog.json still
        // carries. The folder goes on every exit, a refusal and an IO failure included.
        var staging = Path.Combine(build.OutDir, $".build-{Guid.NewGuid():N}");
        var outExisted = Directory.Exists(build.OutDir);
        var keepStaging = false;
        try
        {
            return await BuildAsync(new BuildRun(build, staging, stdout, stderr), ct).ConfigureAwait(false);
        }
        catch (RollbackFailed ex)
        {
            // THE PREVIOUS FILES ARE IN STAGING: deleting it would delete the only copy of the store
            // that was there.
            keepStaging = true;
            return Refuse(stderr, $"{ex.Message} The previous files are in '{Path.Combine(staging, "previous")}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(stderr, $"{ex.Message}");
        }
        finally
        {
            if (!keepStaging)
            {
                // A STAGING FOLDER THAT WILL NOT GO IS SAID, WITH ITS PATH: it holds a whole build's zips,
                // and a folder nobody is told about is one that sits inside the served --out for good.
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stderr.WriteLine($"cxagent store: could not remove '{staging}': {ex.Message}");
                }
            }

            // AN --out THIS RUN MADE AND FILLED WITH NOTHING is a folder a refused build left behind.
            try { if (!outExisted && Directory.Exists(build.OutDir) && !Directory.EnumerateFileSystemEntries(build.OutDir).Any()) Directory.Delete(build.OutDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"cxagent store: could not remove '{build.OutDir}': {ex.Message}");
            }
        }
    }

    private static async Task<int> BuildAsync(BuildRun run, CancellationToken ct)
    {
        var (build, staging, stdout, stderr) = (run.Build, run.Staging, run.Stdout, run.Stderr);
        var all = Directory.GetDirectories(build.Folder).Order(StringComparer.Ordinal).ToList();
        var subfolders = all.Where(d => Directory.GetFiles(d, "*.plugin.json").Length > 0).ToList();

        // A SUBFOLDER THAT IS NOT A PLUGIN IS NAMED, NOT REFUSED: a docs folder, or --out itself, inside
        // the plugins folder is an ordinary layout, so it does not fail the build — but a plugin whose
        // sidecar was not copied is one too, and without this line it would drop out of the regenerated
        // catalog with nothing said. On stderr, so --json's stdout stays one document.
        foreach (var skipped in all.Except(subfolders))
            stderr.WriteLine($"cxagent store: skipped {Path.GetFileName(skipped)}{Path.DirectorySeparatorChar}: no *.plugin.json");
        if (subfolders.Count == 0) return Fail(stderr, $"no plugin in '{build.Folder}': each plugin is a subfolder holding a *.plugin.json.");

        var problems = new List<string>();
        var packed = new List<(string Folder, PluginPackCommand.PackOutcome Outcome)>();
        foreach (var sub in subfolders)
        {
            var label = Path.GetFileName(sub);

            // A NAME `/plugin get <store>/<name>` CANNOT TYPE is a plugin nobody could install from this
            // store, and a user finds that out only after adding it. Asked of the store's own rule.
            if (SidecarName(sub) is { } sidecarName && !PluginStores.TrySplit($"store/{sidecarName}", out _, out _))
            {
                problems.Add($"{label}: the plugin name '{sidecarName}' cannot be installed from a store; a name is letters, digits, '-' and '_'.");
                continue;
            }

            var request = RequestFor(sub, staging, out var infoError);
            if (infoError is not null)
            {
                problems.Add($"{label}: {infoError}");
                continue;
            }

            // ESCAPED: a zip's name goes into a URL, and the address a client fetches must be the one
            // the server has the file under.
            var outcome = await PluginPackCommand.PackAsync(request, zip => $"{build.BaseUrl}/{Uri.EscapeDataString(zip)}", ct).ConfigureAwait(false);
            if (outcome.Packed is null)
            {
                problems.AddRange(outcome.Problems.Select(p => $"{label}: {p}"));
                continue;
            }

            if (outcome.Unfilled.Count > 0)
                problems.Add($"{label}: still to fill in: {string.Join(", ", outcome.Unfilled)} — put them in a pack-info.json beside the plugin.");
            packed.Add((label, outcome));
        }

        foreach (var name in packed.GroupBy(p => p.Outcome.Packed!.Sidecar.Name).Where(g => g.Count() > 1))
            problems.Add($"two plugins are named '{name.Key}': {string.Join(", ", name.Select(p => p.Folder))}.");

        if (problems.Count > 0) return Refuse(stderr, problems);

        var catalog = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["plugins"] = new JsonArray([.. packed.Select(p => (JsonNode)p.Outcome.Entry!)]),
        };

        // READ BACK THROUGH VERIFY'S OWN READER, before anything leaves staging: the catalog a user's
        // client fetches is held to the shape rules before it exists under its served name.
        var staged = Path.Combine(staging, "catalog.json");
        File.WriteAllText(staged, catalog.ToJsonString(Indented) + Environment.NewLine);
        var claims = CatalogClaims.Read(File.ReadAllText(staged), out var readError);
        var shape = claims?.SelectMany(c => c.Shape.Select(s => $"{c.Name}: {s.Field} {s.Message}")).ToList() ?? [];
        if (readError is not null || shape.Count > 0)
            return Refuse(stderr, readError is null ? shape : [readError]);

        var zips = packed.SelectMany(p => p.Outcome.Packed!.Zips).ToList();
        var catalogPath = Path.Combine(build.OutDir, "catalog.json");
        Install([.. zips.Select(z => z.Zip), staged], build.OutDir, Path.Combine(staging, "previous"));

        // PATHS AS THE PERSON TYPED --out, not made relative to the working directory: an --out
        // elsewhere would otherwise print as ../../../…, which is not what they gave.
        string Shown(string file) => Path.Combine(build.OutShown, file);
        if (build.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                catalog = Shown("catalog.json"),
                url = $"{build.BaseUrl}/catalog.json",
                plugins = packed.Select(p => p.Outcome.Packed!.Sidecar.Name),
                zips = zips.Select(z => new { file = Shown(Path.GetFileName(z.Zip)), sha256 = z.Hash, platform = z.Rid }),
            }, Indented));
            return 0;
        }

        foreach (var z in zips) stdout.WriteLine($"{z.Hash}  {Shown(Path.GetFileName(z.Zip))}");
        stdout.WriteLine($"wrote {Shown("catalog.json")} — {packed.Count} plugins");
        stdout.WriteLine($"serve {build.OutShown} at {build.BaseUrl}, then users run: /plugin store add <name> {build.BaseUrl}/catalog.json");
        return 0;
    }

    /// <summary>
    /// Moves the staged files into <paramref name="outDir"/>, the catalog last (it is what makes a
    /// download reachable, so it never names a zip not yet in place).
    ///
    /// <para>ZIPS FIRST IS THE ORDER THAT FAILS SAFE. Catalog first would name hashes of zips not yet
    /// there; zips first means a client reading the old catalog mid-move may fetch a new zip against the
    /// old hash, which the installer refuses as a mismatch — nothing unverified is installed, and the
    /// next read sees the new catalog. A server the folder is uploaded to has the same window, which
    /// publishing.md covers.</para> Each file it replaces is first
    /// moved into <paramref name="previous"/>; if any move fails, the new files already in place are
    /// removed and the old ones put back, so the store is the one that was there or the new one.
    ///
    /// <para>THE GUARANTEE IS BEST EFFORT: the rollback moves can fail as the forward ones did (a
    /// full or read-only volume), and <see cref="RollbackFailed"/> is thrown, which keeps the staging folder with
    /// the old files in <paramref name="previous"/>.</para>
    /// </summary>
    /// <param name="move">The file move, replacing its target; a test passes one that fails on cue.</param>
    internal static void Install(IReadOnlyList<string> staged, string outDir, string previous, Action<string, string>? move = null)
    {
        move ??= (from, to) => File.Move(from, to, overwrite: true);
        Directory.CreateDirectory(previous);
        var done = new List<(string Placed, string? Saved)>();
        try
        {
            foreach (var file in staged)
            {
                var target = Path.Combine(outDir, Path.GetFileName(file));
                string? saved = null;
                if (File.Exists(target))
                {
                    saved = Path.Combine(previous, Path.GetFileName(file));
                    move(target, saved);
                }

                done.Add((target, saved));
                move(file, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // EVERY FILE IS UNDONE, EVEN AFTER ONE UNDO FAILS: stopping there would leave every file
            // earlier in the list as the new build's, against an old catalog — more of the store broken
            // than the one file that could not be put back. The failures are reported together after.
            var failed = new List<string>();
            foreach (var (placed, saved) in Enumerable.Reverse(done))
            {
                try
                {
                    if (File.Exists(placed)) File.Delete(placed);
                    if (saved is not null) move(saved, placed);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{Path.GetFileName(placed)} ({undo.Message})");
                }
            }

            if (failed.Count > 0)
                throw new RollbackFailed(
                    $"{ex.Message} — and these previous files could not be put back: {string.Join(", ", failed)}.", ex);
            throw;
        }
    }

    internal sealed class RollbackFailed(string message, Exception inner) : IOException(message, inner);

    private static string? SidecarName(string sub) =>
        PluginManifest.Parse(File.ReadAllText(Directory.GetFiles(sub, "*.plugin.json").Order(StringComparer.Ordinal).First())).Manifest?.Name;

    // THE PLUGIN'S OWN FILES DECIDE ITS REQUEST: a pack-info beside it for the catalog text, and a
    // <rid>/<library> folder for each platform it was built for. Without the latter a native plugin
    // is packed for this machine's platform alone, as `plugin pack` does.
    private static PackRequest RequestFor(string sub, string outDir, out string? error)
    {
        error = null;
        IReadOnlyDictionary<string, string> info = new Dictionary<string, string>();
        var infoFile = new[] { "pack-info.json", "pack-info.yaml", "pack-info.yml" }
            .Select(n => Path.Combine(sub, n)).FirstOrDefault(File.Exists);
        if (infoFile is not null) info = PackInfoFile.Read(infoFile, out error) ?? info;

        var native = NativeBuildsOf(sub);

        return new PackRequest(Path.GetFullPath(sub), native, outDir, new CatalogText(null, null, null, null), info, Json: false);
    }

    /// <summary>
    /// The platforms a native plugin's folder holds builds for: each <c>&lt;rid&gt;/&lt;sidecar-name&gt;.so|.dylib|.dll</c>,
    /// plus the library beside the sidecar as this machine's platform. Empty for a managed plugin and
    /// for a native one with no platform folders, where pack's own default applies.
    ///
    /// <para>MANAGED PLUGINS ARE NEVER PROBED: <c>dotnet build -r</c> leaves a <c>&lt;rid&gt;/&lt;name&gt;.dll</c> that
    /// is a .NET assembly, and reading it as a native build would pack something else than the plugin.
    /// The top-level library is kept because pack checks the folder it is given, and that library is the
    /// one this machine runs; leaving it out would drop a platform the author built.</para>
    /// </summary>
    internal static IReadOnlyList<NativeBuild> NativeBuildsOf(string sub)
    {
        var sidecar = Directory.GetFiles(sub, "*.plugin.json").Order(StringComparer.Ordinal).First();
        var stem = Path.GetFileName(sidecar)[..^".plugin.json".Length];
        string[] extensions = [".so", ".dylib", ".dll"];
        var binary = extensions.Select(e => Path.Combine(sub, stem + e)).FirstOrDefault(File.Exists);
        if (binary is null || !PluginResolver.IsNative(binary)) return [];

        var native = new List<NativeBuild>();
        foreach (var rid in PluginPackCommand.Rids)
            if (extensions.Select(e => Path.Combine(sub, rid, stem + e)).FirstOrDefault(File.Exists) is { } library)
                native.Add(new NativeBuild(Path.GetFullPath(library), rid));
        if (native.Count == 0) return [];

        var thisMachine = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        if (PluginPackCommand.Rids.Contains(thisMachine) && native.All(n => n.Rid != thisMachine))
            native.Add(new NativeBuild(Path.GetFullPath(binary), thisMachine));
        return native;
    }

    private static Build? Parse(string[] args, out string? error)
    {
        string? folder = null, url = null, outDir = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--json": json = true; break;
                case "--url": url = Next(); break;
                case "--out": outDir = Next(); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || folder is not null)
                    {
                        error = $"unexpected '{args[i]}'.";
                        return null;
                    }

                    folder = args[i];
                    break;
            }
        }

        error = folder is null ? "build takes a folder of plugins."
            : string.IsNullOrEmpty(url) ? "--url takes the address the store is served from."
            : string.IsNullOrEmpty(outDir) ? "--out takes a folder."
            // THE URL RULE A USER'S `/plugin store add` APPLIES, checked here with a stand-in name so a
            // store built for http://example.com is not found out only when its first user adds it.
            : PluginStores.Problem("x", url) is { } urlProblem ? $"--url: {urlProblem}."
            : !Directory.Exists(folder) ? $"no folder '{folder}'."
            : null;
        return error is null
            ? new Build(Path.GetFullPath(folder!), url!.TrimEnd('/'), Path.GetFullPath(outDir!), outDir!, json)
            : null;
    }

    private static int Refuse(TextWriter stderr, string problem) => Refuse(stderr, [problem]);

    private static int Refuse(TextWriter stderr, IEnumerable<string> problems)
    {
        stderr.WriteLine("cxagent store: not built — no catalog written:");
        foreach (var p in problems) stderr.WriteLine($"  {p}");
        return 1;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"cxagent store: {message}");
        stderr.WriteLine(Usage);
        return 2;
    }

    /// <summary>The verbs of <c>cxagent store</c>: only <c>build</c>.</summary>
    public static Task<int> RunVerbAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct) =>
        args is ["build", ..]
            ? RunAsync(args[1..], stdout, stderr, ct)
            : Task.FromResult(Fail(stderr, args.Length == 0 ? "no command." : $"unknown command '{args[0]}'."));
}
