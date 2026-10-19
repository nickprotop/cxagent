using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CxAgent.Core.Plugins;
using CxAgent.Plugins.Testing;

namespace CxAgent.Cli;

/// <summary>A native library and the platform it was built for — one <c>--native lib=rid</c>.</summary>
internal sealed record NativeBuild(string Library, string Rid);

/// <summary>The catalog entry's text that the sidecar does not hold, as given on the command line.</summary>
internal sealed record CatalogText(string? Description, string? Publisher, string? License, string? Repository);

/// <summary>One zip pack wrote: the platform it is for ("any" for managed), its path, its hash, the binary inside.</summary>
internal sealed record PackedZip(string Rid, string Zip, string Hash, string File);

/// <summary>What pack made: the sidecar it read, whether the plugin is native, and its zips.</summary>
internal sealed record PackedPlugin(PluginManifest Sidecar, bool Native, IReadOnlyList<PackedZip> Zips);

/// <summary>What <c>cxagent plugin pack</c> was asked to do. <paramref name="Info"/> is the --info file's fields, empty without one.</summary>
internal sealed record PackRequest(string Folder, IReadOnlyList<NativeBuild> Native, string OutDir, CatalogText Text,
    IReadOnlyDictionary<string, string> Info, bool Json);

/// <summary>
/// <c>cxagent plugin pack &lt;folder&gt;</c> — the zips a release ships, each sha256, the catalog entry
/// that describes them, and the steps left between this and a listing.
///
/// <para>CHECK FIRST, AND NOTHING WRITTEN ON A PROBLEM: a zip of a plugin that will not load is
/// something to upload, list and have refused at every user's install.</para>
/// </summary>
internal static class PluginPackCommand
{
    /// <summary>.NET's runtime identifiers — the vocabulary the catalog's <c>sources</c> keys use, and
    /// the only keys verify accepts there.</summary>
    internal static readonly string[] Rids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];

    // THE HOST CARRIES THESE: a plugin binds to cxagent's own copy, so one in the zip is a file the
    // user approves that is never used.
    private static readonly string[] HostAssemblies = ["CxAgent.Plugins.Abstractions.dll", "CxAgent.Core.dll"];

    private const string UploadUrl = "<upload URL>";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (Parse(args, out var error) is not { } request) return PluginCli.Fail(stderr, error!);

        var problems = await PluginHarness.CheckAsync(request.Folder, ct: ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            stdout.WriteLine("not packed — check found:");
            foreach (var p in problems) stdout.WriteLine(p.ToString());
            return 1;
        }

        var sidecarPath = Directory.GetFiles(request.Folder, "*.plugin.json").Single();
        var sidecar = PluginManifest.Parse(File.ReadAllText(sidecarPath)).Manifest!;
        var binary = BinaryBeside(sidecarPath);
        var native = PluginResolver.IsNative(binary);

        // --native NAMES NATIVE BUILDS: on a managed plugin it is a wrong folder or a wrong flag, and
        // ignoring it would pack something other than what was asked for.
        if (!native && request.Native.Count > 0)
            return PluginCli.Fail(stderr, $"{Path.GetFileName(binary)} is a .NET plugin; --native is for native libraries.");

        // THIS MACHINE'S PLATFORM, WHEN NONE IS NAMED — but only one the catalog's vocabulary has: a
        // distro or musl runtime reports a RID that --native itself would refuse.
        var thisMachine = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        if (native && request.Native.Count == 0 && !Rids.Contains(thisMachine))
            return PluginCli.Fail(stderr, $"this machine reports platform '{thisMachine}', which the catalog does not name; pass --native {Path.GetFileName(binary)}=<rid>.");

        Directory.CreateDirectory(request.OutDir);
        var zips = new List<PackedZip>();
        if (native)
        {
            var builds = request.Native.Count > 0 ? request.Native : [new NativeBuild(binary, thisMachine)];
            foreach (var build in builds)
            {
                // STORED UNDER THE SIDECAR'S NAME: the loader finds a library's sidecar from the
                // library's own name, so a build named libweather.so beside weather.plugin.json would
                // install and never load.
                var stored = Path.GetFileName(sidecarPath)[..^".plugin.json".Length] + Path.GetExtension(build.Library);
                var zip = Path.Combine(request.OutDir, $"{sidecar.Name}-{build.Rid}.zip");
                WriteZip(zip, [new ZipItem(build.Library, stored), new ZipItem(sidecarPath), .. Readme(request.Folder).Select(r => new ZipItem(r))]);
                zips.Add(new PackedZip(build.Rid, zip, Sha256(zip), stored));
            }
        }
        else
        {
            var zip = Path.Combine(request.OutDir, $"{sidecar.Name}.zip");
            WriteZip(zip, ManagedFiles(request.Folder).Select(f => new ZipItem(f)));
            zips.Add(new PackedZip("any", zip, Sha256(zip), Path.GetFileName(binary)));
        }

        var packed = new PackedPlugin(sidecar, native, zips);
        var text = TextFor(request, binary);
        var entryPath = Path.Combine(request.OutDir, $"{sidecar.Name}.catalog.json");
        File.WriteAllText(entryPath, Entry(packed, text).ToJsonString(Indented) + Environment.NewLine);

        var unfilled = text.Where(t => t.Value.StartsWith("<fill in:", StringComparison.Ordinal)).Select(t => t.Key).ToList();
        if (request.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                zips = zips.Select(d => new { file = d.Zip, sha256 = d.Hash, platform = d.Rid }),
                entry = entryPath,
                unfilled,
            }, Indented));
            return 0;
        }

        foreach (var d in zips) stdout.WriteLine($"{d.Hash}  {Shown(d.Zip)}");
        stdout.WriteLine($"wrote {Shown(entryPath)}");
        if (!File.Exists(Path.Combine(request.Folder, "README.md")))
            stdout.WriteLine("note: no README.md beside the plugin; the plugin manager shows the catalog description instead.");
        if (unfilled.Count > 0) stdout.WriteLine($"still to fill in: {string.Join(", ", unfilled)} — in {Shown(entryPath)}");
        NextSteps(stdout, packed, entryPath);
        return 0;
    }

    // A PATH AS THE PERSON TYPES IT: relative to where they are, so a printed command runs as printed
    // whatever --out was.
    private static string Shown(string path) => Path.GetRelativePath(Environment.CurrentDirectory, path);

    private static PackRequest? Parse(string[] args, out string? error)
    {
        string? folder = null, outDir = null, description = null, publisher = null, license = null, repository = null, info = null;
        var native = new List<NativeBuild>();
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--json": json = true; break;
                case "--out": outDir = Next(); break;
                case "--description": description = Next(); break;
                case "--publisher": publisher = Next(); break;
                case "--license": license = Next(); break;
                case "--repository": repository = Next(); break;
                case "--info": info = Next(); break;
                case "--native":
                    var pair = Next().Split('=', 2);
                    if (pair.Length != 2 || !Rids.Contains(pair[1]))
                    {
                        error = $"--native takes <library>=<rid>, the rid one of {string.Join(", ", Rids)}.";
                        return null;
                    }

                    if (!File.Exists(pair[0]))
                    {
                        error = $"no library '{pair[0]}'.";
                        return null;
                    }

                    native.Add(new NativeBuild(Path.GetFullPath(pair[0]), pair[1]));
                    break;
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

        error = folder is null ? "pack takes a plugin folder."
            : !Directory.Exists(folder) ? $"no folder '{folder}'."
            : outDir is "" ? "--out takes a folder."
            : null;
        IReadOnlyDictionary<string, string> fields = new Dictionary<string, string>();
        if (error is null && info is not null && PackInfoFile.Read(info, out error) is { } read) fields = read;
        return error is null
            ? new PackRequest(Path.GetFullPath(folder!), native, Path.GetFullPath(outDir ?? "."),
                new CatalogText(description, publisher, license, repository), fields, json)
            : null;
    }

    private static string BinaryBeside(string sidecarPath)
    {
        var stem = sidecarPath[..^".plugin.json".Length];
        return new[] { ".dll", ".so", ".dylib" }.Select(e => stem + e).First(File.Exists);
    }

    private static IEnumerable<string> Readme(string folder) =>
        File.Exists(Path.Combine(folder, "README.md")) ? [Path.Combine(folder, "README.md")] : [];

    // EVERYTHING AT THE TOP OF THE CHECKED FOLDER, LESS THE BUILD'S LEFTOVERS: check passed on this
    // folder, so the zip leaves out only what a build adds and nothing reads at run time — a native
    // library the plugin P/Invokes, or a data file it opens, ships with it. Not subfolders: the loader
    // resolves from the plugin's own folder only.
    private static IEnumerable<string> ManagedFiles(string folder) =>
        Directory.GetFiles(folder).Where(f => !IsLeftover(f, folder));

    private static bool IsLeftover(string file, string folder)
    {
        var name = Path.GetFileName(file);
        return HostAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".catalog.json", StringComparison.OrdinalIgnoreCase)
            // AN ASSEMBLY'S XML DOCS — foo.xml beside foo.dll — are for an IDE; any other .xml is data.
            || (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(folder, Path.ChangeExtension(name, ".dll"))));
    }

    /// <summary>One file for a zip, and the name it has inside — its own unless renamed.</summary>
    private sealed record ZipItem(string Path, string? StoredAs = null);

    private static void WriteZip(string zip, IEnumerable<ZipItem> files)
    {
        if (File.Exists(zip)) File.Delete(zip);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var file in files.DistinctBy(f => f.Path))
            archive.CreateEntryFromFile(file.Path, file.StoredAs ?? System.IO.Path.GetFileName(file.Path), CompressionLevel.Optimal);
    }

    // LOWERCASE HEX, as PluginInstaller computes it, so the entry and the install agree byte for byte.
    private static string Sha256(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    /// <summary>
    /// The entry's text: the option, else the --info file, else the plugin's csproj, else a placeholder
    /// verify refuses — so a field nobody filled in cannot reach the catalog looking deliberate.
    /// </summary>
    private static IReadOnlyDictionary<string, string> TextFor(PackRequest request, string binary)
    {
        var project = ProjectOf(Path.GetDirectoryName(binary)!, Path.GetFileNameWithoutExtension(binary));
        string Pick(string field, string? given, string property) =>
            given is { Length: > 0 } ? given
            : request.Info.TryGetValue(field, out var fromFile) && fromFile.Length > 0 ? fromFile
            : project?.Descendants(property).FirstOrDefault()?.Value is { Length: > 0 } fromProject ? fromProject
            : $"<fill in: {field}>";

        return new Dictionary<string, string>
        {
            ["description"] = Pick("description", request.Text.Description, "Description"),
            ["publisher"] = Pick("publisher", request.Text.Publisher, "Authors"),
            ["license"] = Pick("license", request.Text.License, "PackageLicenseExpression"),
            ["repository"] = Pick("repository", request.Text.Repository, "RepositoryUrl"),
        };
    }

    /// <summary>
    /// The plugin's project, found by walking up from its build folder to the nearest directory with
    /// exactly one csproj named for the binary (or whose AssemblyName is) — bin/Release/net10.0 is
    /// three levels below it. Null for a plugin with no project, a C one say.
    /// </summary>
    private static XDocument? ProjectOf(string folder, string stem)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var projects = dir.GetFiles("*.csproj");
            if (projects.Length != 1) continue;
            try
            {
                var doc = XDocument.Load(projects[0].FullName);
                var assemblyName = doc.Descendants("AssemblyName").FirstOrDefault()?.Value;
                if (Path.GetFileNameWithoutExtension(projects[0].Name) == stem || assemblyName == stem) return doc;
            }
            catch (System.Xml.XmlException)
            {
                // A PROJECT THAT WILL NOT PARSE IS NOT THE PLUGIN'S: keep walking up.
            }
        }

        return null;
    }

    private static JsonObject Entry(PackedPlugin packed, IReadOnlyDictionary<string, string> text)
    {
        var (sidecar, native, downloads) = (packed.Sidecar, packed.Native, packed.Zips);
        var entry = new JsonObject
        {
            ["name"] = sidecar.Name,
            ["displayName"] = sidecar.Name,
            ["version"] = sidecar.Version,
            ["description"] = text["description"],
            ["publisher"] = text["publisher"],
            ["license"] = text["license"],
            ["repository"] = text["repository"],
        };

        if (native)
        {
            var sources = new JsonObject();
            foreach (var d in downloads)
                sources[d.Rid] = new JsonObject { ["kind"] = "url", ["url"] = UploadUrl, ["sha256"] = d.Hash, ["file"] = d.File };
            entry["sources"] = sources;
            entry["kind"] = "native";
        }
        else
        {
            entry["source"] = new JsonObject { ["kind"] = "url", ["url"] = UploadUrl, ["sha256"] = downloads[0].Hash };
            entry["file"] = downloads[0].File;
            entry["kind"] = "managed";
        }

        entry["compatibility"] = new JsonObject
        {
            ["pluginContract"] = sidecar.Contract ?? 0,
            ["platforms"] = new JsonArray([.. downloads.Select(d => (JsonNode)JsonValue.Create(d.Rid)!)]),
        };
        entry["spawns"] = sidecar.Spawns;
        entry["tools"] = new JsonArray([.. sidecar.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,

            // THE SIDECAR'S OWN SPELLING: booleans for never and always, "dynamic" as a string.
            ["gated"] = t.Gated switch
            {
                PluginGating.Never => JsonValue.Create(false),
                PluginGating.Always => JsonValue.Create(true),
                _ => JsonValue.Create("dynamic"),
            },
        })]);
        return entry;
    }

    private static void NextSteps(TextWriter stdout, PackedPlugin packed, string entryPath)
    {
        var sidecar = packed.Sidecar;
        var entryFile = Shown(entryPath);
        var zips = packed.Native
            ? Path.Combine(Path.GetDirectoryName(Shown(packed.Zips[0].Zip)) ?? "", $"{sidecar.Name}-*.zip")
            : Shown(packed.Zips[0].Zip);
        stdout.WriteLine();
        stdout.WriteLine("  Next:");
        stdout.WriteLine("    1. Upload the zips somewhere they stay — e.g. a GitHub release in your repository:");
        stdout.WriteLine($"         gh release create v{sidecar.Version} {zips}");
        stdout.WriteLine($"    2. Put each zip's URL into {entryFile} (the \"url\" fields marked {UploadUrl}).");
        stdout.WriteLine($"    3. Check what a user would install:  cxagent plugin verify {entryFile}");
        stdout.WriteLine("    4. Submit it: open \"Submit a plugin\" at https://github.com/nickprotop/cxagent/issues/new/choose");
        stdout.WriteLine($"       and paste {entryFile} — or open a PR adding it to plugins/plugins.json.");
    }
}
