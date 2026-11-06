using System.Collections.Frozen;

namespace CxAgent.Web.Http;

/// <summary>
/// The page's files, embedded in the binary under <c>web/</c> and served from memory.
///
/// <para>A FIXED SET, READ ONCE. A request path is only ever a key into this table, never joined to a folder, so no
/// path a browser sends can reach a file the binary did not ship.</para>
/// </summary>
public static class StaticAssets
{
    private const string Prefix = "web/";

    private static readonly FrozenDictionary<string, byte[]> Files = Load();

    /// <summary>The file a request path names, and its content type. <c>/</c> is <c>index.html</c>.</summary>
    public static bool TryGet(string path, out ReadOnlyMemory<byte> body, out string contentType)
    {
        var name = path == "/" ? "index.html" : path.TrimStart('/');
        if (Files.TryGetValue(name, out var bytes))
        {
            body = bytes;
            contentType = TypeOf(name);
            return true;
        }
        body = default;
        contentType = "";
        return false;
    }

    /// <summary>Every served file name — for tests that check what shipped.</summary>
    public static IReadOnlyCollection<string> Names => Files.Keys;

    private static FrozenDictionary<string, byte[]> Load()
    {
        var assembly = typeof(StaticAssets).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);

            // THE RESOURCE NAME USES THE BUILD MACHINE'S SEPARATOR for its folder part; a URL always uses '/'.
            files[resource[Prefix.Length..].Replace('\\', '/')] = copy.ToArray();
        }
        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string TypeOf(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };
}
