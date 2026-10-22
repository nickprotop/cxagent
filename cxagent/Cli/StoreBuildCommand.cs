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
        <subfolder>/<rid>/<library>.

        exit: 0 built, 1 a plugin is at fault, 2 usage
        """;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What the command line asked for.</summary>
    private sealed record Build(string Folder, string BaseUrl, string OutDir, bool Json);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (Parse(args, out var error) is not { } build) return Fail(stderr, error!);

        var subfolders = Directory.GetDirectories(build.Folder)
            .Where(d => Directory.GetFiles(d, "*.plugin.json").Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (subfolders.Count == 0) return Refuse(stderr, $"no plugin in '{build.Folder}': each plugin is a subfolder holding a *.plugin.json.");

        var problems = new List<string>();
        var packed = new List<(string Folder, PluginPackCommand.PackOutcome Outcome)>();
        foreach (var sub in subfolders)
        {
            var label = Path.GetFileName(sub);
            var request = RequestFor(sub, build.OutDir, out var infoError);
            if (infoError is not null)
            {
                problems.Add($"{label}: {infoError}");
                continue;
            }

            var outcome = await PluginPackCommand.PackAsync(request, zip => $"{build.BaseUrl}/{zip}", ct).ConfigureAwait(false);
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

        // READ BACK THROUGH VERIFY'S OWN READER, from a file beside the real one: the catalog a user's
        // client fetches is held to the shape rules before it exists under its served name.
        var catalogPath = Path.Combine(build.OutDir, "catalog.json");
        var staged = catalogPath + ".tmp";
        File.WriteAllText(staged, catalog.ToJsonString(Indented) + Environment.NewLine);
        var claims = CatalogClaims.Read(File.ReadAllText(staged), out var readError);
        var shape = claims?.SelectMany(c => c.Shape.Select(s => $"{c.Name}: {s.Field} {s.Message}")).ToList() ?? [];
        if (readError is not null || shape.Count > 0)
        {
            File.Delete(staged);
            return Refuse(stderr, readError is null ? shape : [readError]);
        }

        File.Move(staged, catalogPath, overwrite: true);

        var zips = packed.SelectMany(p => p.Outcome.Packed!.Zips).ToList();
        if (build.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                catalog = catalogPath,
                url = $"{build.BaseUrl}/catalog.json",
                plugins = packed.Select(p => p.Outcome.Packed!.Sidecar.Name),
                zips = zips.Select(z => new { file = z.Zip, sha256 = z.Hash, platform = z.Rid }),
            }, Indented));
            return 0;
        }

        foreach (var z in zips) stdout.WriteLine($"{z.Hash}  {PluginPackCommand.Shown(z.Zip)}");
        stdout.WriteLine($"wrote {PluginPackCommand.Shown(catalogPath)} — {packed.Count} plugins");
        stdout.WriteLine($"serve {PluginPackCommand.Shown(build.OutDir)} at {build.BaseUrl}, then users run: /plugin store add <name> {build.BaseUrl}/catalog.json");
        return 0;
    }

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

        var stem = Path.GetFileName(Directory.GetFiles(sub, "*.plugin.json").Order(StringComparer.Ordinal).First())[..^".plugin.json".Length];
        var native = new List<NativeBuild>();
        foreach (var rid in PluginPackCommand.Rids)
            if (new[] { ".so", ".dylib", ".dll" }.Select(e => Path.Combine(sub, rid, stem + e)).FirstOrDefault(File.Exists) is { } library)
                native.Add(new NativeBuild(Path.GetFullPath(library), rid));

        return new PackRequest(Path.GetFullPath(sub), native, outDir, new CatalogText(null, null, null, null), info, Json: false);
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
            ? new Build(Path.GetFullPath(folder!), url!.TrimEnd('/'), Path.GetFullPath(outDir!), json)
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
