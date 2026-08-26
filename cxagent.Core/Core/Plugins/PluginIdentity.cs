using System.Security.Cryptography;
using System.Text;

namespace CxAgent.Core.Plugins;

/// <summary>
/// A content hash over a plugin's WHOLE LOAD SET — see the plugin design, "Identity is a content hash, not
/// a filename": "The hash covers everything loaded, not one file. A managed plugin with dependency
/// assemblies is a directory, and hashing only its entry point leaves a swapped dependency changing
/// the code without changing the identity — the grant would carry over to something the user never
/// approved."
///
/// <para>NOT THE ENTRY-POINT FILE ALONE. A managed plugin's directory holds its .dll, its sidecar
/// manifest and whatever dependency assemblies it needs; every one of those bytes is code or
/// declared behaviour that ran under the grant, so every one of them has to move the hash or a
/// swapped file is a swapped plugin the user never saw.</para>
///
/// <para>DETERMINISTIC ACROSS MACHINES: files are hashed in a fixed order (relative path, ordinal)
/// rather than whatever order the filesystem happens to enumerate them in, which is not guaranteed
/// stable even on one machine between two runs, let alone between two operating systems.</para>
/// </summary>
public static class PluginIdentity
{
    /// <summary>
    /// Hashes every regular file under <paramref name="loadSetDirectory"/>, recursively, as one
    /// identity — the plugin's directory, not any single file in it.
    ///
    /// <para>PATH AND CONTENT BOTH FEED THE HASH. Content alone would let a dependency renamed to
    /// another dependency's name pass unnoticed if the bytes happened to collide in position after
    /// sorting; folding the relative path (forward-slash normalised, so the same tree hashes the same
    /// on Windows and Linux) into the digest for each file removes that. The path is relative to
    /// <paramref name="loadSetDirectory"/> so moving the whole plugin to a different folder — which
    /// the plugin design's "Identity is a content hash, not a filename" says must NOT matter — does not
    /// change the identity.</para>
    /// </summary>
    /// <param name="loadSetDirectory">The directory holding everything this plugin loads — its entry
    /// point, its sidecar, and any dependency assembly beside them.</param>
    /// <returns>A lowercase hex SHA-256 digest over the ordered set.</returns>
    public static string HashLoadSet(string loadSetDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loadSetDirectory));

        // ORDERED BY RELATIVE PATH, ORDINAL — not by DirectoryInfo enumeration order, which the BCL
        // documents as unspecified. An unstable order would make the hash unstable too: the same
        // bytes on the same machine could hash two different ways on two runs, and "did this plugin
        // change" would have no fixed answer to compare against.
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);

        foreach (var (path, relative) in files)
        {
            // THE PATH GOES IN FIRST, length-prefixed rather than delimited. A delimiter (say, a
            // newline) would let a crafted filename absorb the boundary between two entries and
            // produce a collision between two different load sets; a fixed-width length prefix cannot
            // be forged by anything a filename is allowed to contain.
            var nameBytes = Encoding.UTF8.GetBytes(relative);
            stream.Write(BitConverter.GetBytes(nameBytes.Length));
            stream.Write(nameBytes);

            using var file = File.OpenRead(path);
            stream.Write(BitConverter.GetBytes(file.Length));
            file.CopyTo(stream);
        }

        stream.FlushFinalBlock();
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// The names of other plugins whose sidecars sit inside <paramref name="loadSetDirectory"/> —
    /// empty when this plugin has the directory to itself.
    ///
    /// <para>WHAT THIS COSTS A USER, which is why it is worth saying. A plugin's identity is a hash
    /// over its whole load set, and the load gate stores an "always" grant against that hash. When
    /// the load set holds another plugin's files, installing or updating that other plugin moves
    /// this one's hash: its gate asks again, citing a change the user did not make to it, and the
    /// standing grant stops applying.</para>
    ///
    /// <para>AT ANY DEPTH, because <see cref="HashLoadSet"/> walks with
    /// <see cref="SearchOption.AllDirectories"/>. A nested plugin sits inside a loose one's load set,
    /// and after one-plugin-one-directory ships that is the ordinary way a second plugin arrives —
    /// so a top-level check would miss the case this exists to report.</para>
    /// </summary>
    public static IReadOnlyList<string> SharesLoadSetWith(string loadSetDirectory, string pluginName)
    {
        if (!Directory.Exists(loadSetDirectory)) return [];

        var others = new List<string>();

        foreach (var sidecar in Directory
                     .EnumerateFiles(loadSetDirectory, "*.plugin.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string? name;
            try
            {
                name = PluginManifest.Parse(File.ReadAllText(sidecar)).Manifest?.Name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // AN UNREADABLE SIDECAR IS NOT A FINDING. This runs on the load path to produce a
                // warning; a permissions problem must not turn a courtesy into a failure.
                continue;
            }

            if (!string.IsNullOrEmpty(name) && !string.Equals(name, pluginName, StringComparison.Ordinal))
                others.Add(name);
        }

        return others;
    }
}
