using System.Security.Cryptography;
using System.Text;

namespace CxAgent.Core.Plugins;

/// <summary>
/// A content hash over a plugin's WHOLE LOAD SET, not a filename. The hash covers everything loaded,
/// not one file. A managed plugin with dependency assemblies is a directory, and hashing only its
/// entry point leaves a swapped dependency changing the code without changing the identity — the
/// grant would carry over to something the user never approved.
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
    /// must not matter, since identity is a content hash, not a filename — does not change the
    /// identity.</para>
    /// </summary>
    /// <param name="loadSetDirectory">The directory holding everything this plugin loads — its entry
    /// point, its sidecar, and any dependency assembly beside them.</param>
    /// <returns>A lowercase hex SHA-256 digest over the ordered set.</returns>
    /// <exception cref="LoadSetRefusedException">The load set cannot be identified — see <see cref="InspectLoadSet"/>.</exception>
    public static string HashLoadSet(string loadSetDirectory) =>
        InspectLoadSet(loadSetDirectory) is { Hash: { } hash } ? hash
            : throw new LoadSetRefusedException(InspectLoadSet(loadSetDirectory).Refusal!);

    /// <summary>
    /// The load set's hash, or why it has none.
    ///
    /// <para>NO LINK IS FOLLOWED. A link out of the load set brings in code the hash never saw, so what a person
    /// approves would not be what runs: such a load set is refused, naming the link. A loop of links is refused
    /// rather than walked until it throws. A link that stays inside is hashed as a link — by the text it holds —
    /// so the bytes it points at are hashed once, under their own name. A load-set folder that is itself a link
    /// is resolved first, and hashes as the folder it is.</para>
    /// </summary>
    public static LoadSetIdentity InspectLoadSet(string loadSetDirectory)
    {
        var root = Permissions.PathResolver.RealFolder(loadSetDirectory);
        Permissions.WalkResult walk;
        try { walk = Permissions.PathResolver.Walk(new Permissions.WalkRequest(root, "*", Within: [])); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadSetIdentity(null, $"its folder could not be read: {ex.Message}");
        }

        string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        if (walk.Unfollowed.FirstOrDefault(l => l.Cycle) is { } loop)
            return new LoadSetIdentity(null, $"its folder holds a link that loops back on itself: {Relative(loop.Path)}");
        if (walk.Unfollowed.FirstOrDefault(l => !l.InsideStart) is { } outward)
            return new LoadSetIdentity(null,
                $"its folder links outside itself: {Relative(outward.Path)} -> {outward.Target ?? "(unresolvable)"}");
        if (walk.Unreadable.FirstOrDefault() is { } unreadable)
            return new LoadSetIdentity(null, $"part of its folder could not be read: {Relative(unreadable.Path)}");

        // ORDERED BY RELATIVE PATH, ORDINAL — not by enumeration order, which the BCL documents as unspecified. An
        // unstable order would make the hash unstable too: the same bytes on the same machine could hash two
        // different ways on two runs, and "did this plugin change" would have no fixed answer to compare against.
        var entries = walk.Files.Select(path => new HashedEntry(Relative(path), path, IsLink: false))
            .Concat(walk.Unfollowed.Select(link => new HashedEntry(Relative(link.Path), link.Path, IsLink: true)))
            .OrderBy(e => e.Relative, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);

        foreach (var entry in entries)
        {
            // THE PATH GOES IN FIRST, length-prefixed rather than delimited. A delimiter (say, a
            // newline) would let a crafted filename absorb the boundary between two entries and
            // produce a collision between two different load sets; a fixed-width length prefix cannot
            // be forged by anything a filename is allowed to contain.
            var nameBytes = Encoding.UTF8.GetBytes(entry.Relative);
            stream.Write(BitConverter.GetBytes(nameBytes.Length));
            stream.Write(nameBytes);

            // A LINK BY ITS TEXT, marked as one so it cannot collide with a file holding the same bytes.
            if (entry.IsLink)
            {
                var target = Encoding.UTF8.GetBytes("link:" + (new FileInfo(entry.Path).LinkTarget ?? ""));
                stream.Write(BitConverter.GetBytes(-(long)target.Length));
                stream.Write(target);
                continue;
            }

            using var file = File.OpenRead(entry.Path);
            stream.Write(BitConverter.GetBytes(file.Length));
            file.CopyTo(stream);
        }

        stream.FlushFinalBlock();
        return new LoadSetIdentity(Convert.ToHexString(sha.Hash!).ToLowerInvariant(), null);
    }

    private sealed record HashedEntry(string Relative, string Path, bool IsLink);

    /// <summary>
    /// What a load set holds, by the walk its hash takes: its files, and its links as links — none followed — so a
    /// caller that removes a plugin removes the link and leaves whatever it points at.
    /// </summary>
    public static LoadSetFiles ListLoadSet(string loadSetDirectory)
    {
        var walk = Permissions.PathResolver.Walk(new Permissions.WalkRequest(loadSetDirectory, "*", Within: []));
        return new LoadSetFiles(walk.Files, [.. walk.Unfollowed.Select(l => l.Path)]);
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
    /// <para>AT ANY DEPTH, because <see cref="HashLoadSet"/> walks the whole folder. A nested plugin sits inside a loose one's load set,
    /// and after one-plugin-one-directory ships that is the ordinary way a second plugin arrives —
    /// so a top-level check would miss the case this exists to report.</para>
    /// </summary>
    public static IReadOnlyList<string> SharesLoadSetWith(string loadSetDirectory, string pluginName)
    {
        if (!Directory.Exists(loadSetDirectory)) return [];

        var others = new List<string>();

        // THE SAME WALK THE HASH TAKES — no link followed — so this reports the plugins the hash covers.
        foreach (var sidecar in Permissions.PathResolver.Walk(new Permissions.WalkRequest(
                         loadSetDirectory, "*.plugin.json", Within: [])).Files
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string? name;
            try
            {
                name = PluginResolver.ReadSidecar(sidecar).Json is { } json ? PluginManifest.Parse(json).Manifest?.Name : null;
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

/// <summary>A load set's identity — its hash — or why it has none.</summary>
public sealed record LoadSetIdentity(string? Hash, string? Refusal);

/// <summary>A load set's files and its links, as spelled under the folder it was listed from.</summary>
public sealed record LoadSetFiles(IReadOnlyList<string> Files, IReadOnlyList<string> Links);

/// <summary>A load set that cannot be identified, so cannot be approved — its message says why.</summary>
public sealed class LoadSetRefusedException(string reason) : IOException(reason);
