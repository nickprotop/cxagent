using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using CxAgent.Core.Plugins;
using CxAgent.UI;

namespace CxAgent.Cli;

/// <summary>One thing verify found wrong: which entry, which field, what.</summary>
internal sealed record VerifyProblem(string Entry, string Field, string Message)
{
    public override string ToString() => $"{Entry}: {Field}: {Message}";
}

/// <summary>
/// <c>cxagent plugin verify &lt;entry.json | plugins.json&gt;</c> — what a user's install would get,
/// checked against everything the entry claims.
///
/// <para>NO PLUGIN CODE RUNS. Zips are downloaded, hashed, unpacked and read as files; nothing in
/// them is loaded — the same promise a catalog check on someone else's submission has to keep.</para>
/// </summary>
internal static class PluginVerifyCommand
{
    private static readonly HttpClient Http = new();

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var json = args.Contains("--json");
        var files = args.Where(a => a != "--json").ToArray();
        if (files.Any(a => a.StartsWith("--", StringComparison.Ordinal))) return PluginCli.Fail(stderr, $"unknown option '{files.First(a => a.StartsWith("--"))}'.");
        if (files.Length != 1) return PluginCli.Fail(stderr, "verify takes one entry file or plugins.json.");
        if (!File.Exists(files[0])) return PluginCli.Fail(stderr, $"no file '{files[0]}'.");

        if (CatalogClaims.Read(File.ReadAllText(files[0]), out var error) is not { } entries)
        {
            stdout.WriteLine($"{Path.GetFileName(files[0])}: {error}");
            return 1;
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(files[0]))!;
        var problems = new List<VerifyProblem>();
        var lines = new List<string>();
        foreach (var claims in entries)
        {
            if (claims.FirstParty)
            {
                // FIRST-PARTY ENTRIES ARE PluginCatalogTests' to check: their "latest" URL is whatever
                // the newest release carries, not this tree's build.
                lines.Add($"{claims.Name}: first-party, skipped");
                continue;
            }

            var before = problems.Count;
            var found = new List<VerifyProblem>();
            ClaimProblems(claims, found);
            if (found.Count == 0)
                foreach (var download in claims.Downloads)
                    await DownloadProblems(claims, download, baseDir, found, ct).ConfigureAwait(false);
            problems.AddRange(found);
            if (problems.Count == before)
                lines.Add($"ok — {claims.Name} {claims.Version}: {claims.Downloads.Count} zip{(claims.Downloads.Count == 1 ? "" : "s")}, sha256 and contents match.");
        }

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                ok = problems.Count == 0,
                problems = problems.Select(p => new { entry = p.Entry, field = p.Field, message = p.Message }),
            }, Indented));
        }
        else
        {
            foreach (var line in lines) stdout.WriteLine(line);
            foreach (var p in problems) stdout.WriteLine(p.ToString());
        }

        return problems.Count == 0 ? 0 : 1;
    }

    /// <summary>What the entry gets wrong on its own, before anything is fetched.</summary>
    private static void ClaimProblems(CatalogClaims claims, List<VerifyProblem> problems)
    {
        void Add(string field, string message) => problems.Add(new VerifyProblem(claims.Name, field, message));

        foreach (var (field, value) in claims.Text)
            if (value.StartsWith("<fill in:", StringComparison.Ordinal) || value.Length == 0)
                Add(field, $"is still \"{value}\"; say it in the entry.");

        if (claims.Downloads.Count == 0) Add("source", "the entry names nothing to download.");
        var native = claims.Kind == "native";
        foreach (var d in claims.Downloads)
        {
            var at = d.Rid is null ? "source" : $"sources.{d.Rid}";
            if (d.Url.Contains("<upload URL>", StringComparison.Ordinal))
                Add($"{at}.url", "is still <upload URL>; put the zip's real URL there.");
            else if (d.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                Add($"{at}.url", "is plain http; a download must be https:// (or, before uploading, a local file).");
            if (string.IsNullOrWhiteSpace(d.Sha256)) Add($"{at}.sha256", "is missing; install refuses a download it cannot check.");
            var fileField = d.Rid is null ? "file" : $"{at}.file";
            if (d.File.Length == 0) Add(fileField, "names no file.");

            // A NAME IN THE ZIP, NEVER A PATH: "../" would point the check — and an install, which
            // joins it onto the plugin folder — at a file the zip does not hold.
            else if (Path.GetFileName(d.File) != d.File) Add(fileField, $"is '{d.File}', a path; it must be a file name in the zip.");
        }

        var expected = native ? claims.Downloads.Select(d => d.Rid ?? "").ToList() : ["any"];
        if (!claims.Platforms.SequenceEqual(expected))
            Add("compatibility.platforms", $"says [{string.Join(", ", claims.Platforms)}], but the downloads are for [{string.Join(", ", expected)}].");
    }

    /// <summary>Fetches one zip and holds its contents up against the claims.</summary>
    private static async Task DownloadProblems(CatalogClaims claims, ClaimedDownload download, string baseDir,
        List<VerifyProblem> problems, CancellationToken ct)
    {
        var at = download.Rid is null ? "source" : $"sources.{download.Rid}";
        void Add(string field, string message) => problems.Add(new VerifyProblem(claims.Name, field, message));

        byte[] zip;
        try
        {
            zip = await Fetch(download.Url, baseDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Add($"{at}.url", $"could not be fetched: {ex.Message}");
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        if (!string.Equals(hash, download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Add($"{at}.sha256", $"is {download.Sha256}, but the zip hashes to {hash}.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("cxagent-verify-").FullName;
        try
        {
            string? escaping;
            try
            {
                escaping = SafeZip.ExtractTo(zip, folder);
            }
            catch (InvalidDataException ex)
            {
                Add(at, $"is not a zip: {ex.Message}");
                return;
            }

            if (escaping is not null)
            {
                Add(at, $"holds an entry that would write outside its folder ({escaping}); install refuses it.");
                return;
            }

            ContentProblems(claims, download, folder, problems);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static void ContentProblems(CatalogClaims claims, ClaimedDownload download, string folder, List<VerifyProblem> problems)
    {
        void Add(string field, string message) => problems.Add(new VerifyProblem(claims.Name, field, message));

        var sidecars = Directory.GetFiles(folder, "*.plugin.json");
        if (sidecars.Length != 1)
        {
            Add("zip", $"holds {sidecars.Length} sidecars; a plugin zip holds one.");
            return;
        }

        // THE LOADER FINDS THE SIDECAR FROM THE BINARY'S NAME: weather.dll needs weather.plugin.json.
        var expected = Path.ChangeExtension(download.File, null) + ".plugin.json";
        if (Path.GetFileName(sidecars[0]) != expected)
        {
            Add("file", $"is {download.File}, whose sidecar would be {expected}, but the zip holds {Path.GetFileName(sidecars[0])}; the loader would not find it.");
            return;
        }

        var parsed = PluginManifest.Parse(File.ReadAllText(sidecars[0]));
        if (parsed.Manifest is not { } sidecar)
        {
            Add("zip", $"its sidecar does not parse: {string.Join("; ", parsed.Errors)}");
            return;
        }

        void Same(string field, string claimed, string actual)
        {
            if (claimed != actual) Add(field, $"says {claimed}, the zip's sidecar says {actual}.");
        }

        Same("name", claims.Name, sidecar.Name);
        Same("version", claims.Version, sidecar.Version);
        Same("compatibility.pluginContract", claims.PluginContract.ToString(), (sidecar.Contract ?? 0).ToString());
        Same("spawns", claims.Spawns.ToString().ToLowerInvariant(), sidecar.Spawns.ToString().ToLowerInvariant());
        if (sidecar.Kind is { } kind) Same("kind", claims.Kind, kind.ToString().ToLowerInvariant());

        var actualTools = sidecar.Tools.ToDictionary(t => t.Name, t => t.Gated switch
        {
            PluginGating.Never => "false",
            PluginGating.Always => "true",
            _ => "dynamic",
        });
        foreach (var tool in claims.Tools)
        {
            if (!actualTools.TryGetValue(tool.Name, out var gated)) Add("tools", $"lists '{tool.Name}', which the zip's sidecar does not declare.");
            else if (gated != tool.Gated) Add("tools", $"says '{tool.Name}' is gated {tool.Gated}, the sidecar says {gated}.");
        }

        foreach (var name in actualTools.Keys.Where(n => claims.Tools.All(t => t.Name != n)))
            Add("tools", $"leaves out '{name}', which the zip's sidecar declares.");

        var binary = Path.Combine(folder, download.File);
        if (!File.Exists(binary))
        {
            Add("file", $"names {download.File}, which is not in the zip.");
            return;
        }

        // READ, NOT LOADED: the header says whether it is a .NET assembly, and nothing in it runs.
        var isAssembly = PluginResolver.IsDotNetAssembly(binary);
        if (claims.Kind == "managed" && !isAssembly) Add("kind", $"says managed, but {download.File} is not a .NET assembly.");
        if (claims.Kind == "native" && isAssembly) Add("kind", $"says native, but {download.File} is a .NET assembly.");
    }

    // HTTPS, OR A FILE ON DISK — a path or file:// — so an author verifies before uploading. Plain http
    // is refused before this is reached.
    private static async Task<byte[]> Fetch(string url, string baseDir, CancellationToken ct)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

        var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
        return await File.ReadAllBytesAsync(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path), ct).ConfigureAwait(false);
    }
}
