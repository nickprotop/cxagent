using System.Collections.Concurrent;

namespace CxAgent.Core.Plugins.Builtin;

/// <summary>What a file looked like when it was read, and what it was.</summary>
/// <param name="Text">The content, BOM stripped — what a caller matches against.</param>
/// <param name="Existed">False when the file was not there; <paramref name="Text"/> is then empty.</param>
/// <param name="HadBom">Whether the bytes on disk began with a UTF-8 BOM.</param>
/// <param name="UsesCrlf">Whether the file's own line endings are CRLF.</param>
public sealed record FileSnapshot(string Text, bool Existed, bool HadBom, bool UsesCrlf);

/// <summary>Raised when a file changed between the read a caller worked from and its write.</summary>
public sealed class StaleContentException(string path) : Exception(
    $"{path} changed on disk since it was read, so this edit was computed from content that no "
    + "longer exists. Nothing was written. Read the file again and redo the edit against what it "
    + "says now.")
{
    public string Path { get; } = path;
}

/// <summary>
/// The one place that writes a file, and the only place that knows how this app writes one.
///
/// <para>IT EXISTS BECAUSE THE CONVENTIONS ARE INVISIBLE TO THE CALLER — and to the model. A file
/// carries a BOM or does not, uses CRLF or does not, and neither survives a round trip through
/// <c>ReadAllTextAsync</c>: the model reproduces text it was shown and cannot see what it is missing.
/// So every write has to restore what the FILE had, from the bytes on disk, and doing that correctly
/// at each call site is how one of them ends up not doing it. Measured: the BOM fix went into
/// <c>replace</c> and not <c>write</c>, so which tool the model happened to pick decided whether the
/// repo picked up a spurious diff on every touched file.</para>
///
/// <para>THREE GUARANTEES, and they are the reason this is a type rather than three helpers:</para>
/// <list type="number">
///   <item><b>Conventions survive.</b> BOM and line endings come from what is on disk, never from
///   the content being written. A file that does not exist yet has neither to keep.</item>
///   <item><b>One writer per file.</b> Sub-agents run concurrently and share one plugin instance, so
///   two of them editing one file both read, both match, and the second overwrites the first with an
///   edit computed from a version that no longer existed — silently, both reporting success. Proven:
///   with the lock removed, the concurrent-replace test fails two runs in three.</item>
///   <item><b>Stale edits are refused.</b> A lock stops two agents inside this process. It does
///   nothing about the file changing between a read and a write for any other reason — the user's
///   editor, a build step, a git checkout. <see cref="WriteIfUnchangedAsync"/> compares what is there
///   now against what the caller read, and refuses rather than clobbering.</item>
/// </list>
///
/// <para>STATIC, because the thing being serialised is a PATH — a process-wide fact. An instance per
/// plugin would hand each one its own lock table and serialise nothing.</para>
/// </summary>
public static class FileMutation
{
    /// <summary>
    /// One lock per resolved path.
    ///
    /// <para>PER PATH, not one global: unrelated files should still be written in parallel, and a
    /// single mutex would serialise every file operation in the session to no purpose.</para>
    ///
    /// <para>Never evicted. An entry is one SemaphoreSlim against a path string; a session touches
    /// hundreds of files, not millions, and reclaiming them would need reference counting to avoid
    /// evicting a lock somebody is holding — real complexity to save a few kilobytes.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.Ordinal);

    private static SemaphoreSlim LockFor(string path) =>
        Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    /// <summary>This path's lock, for a caller that must hold it across several operations — a
    /// read-modify-write is not safe with the lock taken around only the write.</summary>
    public static SemaphoreSlim LockHandleFor(string path) => LockFor(path);

    /// <summary>Runs <paramref name="work"/> with this path's lock held. Public so a caller doing a
    /// read-modify-write can hold it across the WHOLE operation — a lock taken only around the write
    /// leaves exactly the race worth closing.</summary>
    public static async Task<T> WithLockAsync<T>(string path, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = LockFor(path);
        await gate.WaitAsync(ct);
        try { return await work(); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Reads a file along with the conventions a later write has to restore.
    ///
    /// <para>The BOM is read from the BYTES: <c>ReadAllTextAsync</c> strips it silently, so the text
    /// alone cannot say whether there was one.</para>
    /// </summary>
    public static async Task<FileSnapshot> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new FileSnapshot("", Existed: false, HadBom: false, UsesCrlf: false);

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = System.Text.Encoding.UTF8.GetString(hadBom ? bytes[3..] : bytes);

        return new FileSnapshot(text, Existed: true, hadBom, text.Contains("\r\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes <paramref name="content"/>, restoring the conventions in <paramref name="on"/>.
    ///
    /// <para>Takes the snapshot rather than re-reading, so a caller that has already read the file
    /// does not read it twice — and, more importantly, so the conventions written are the ones from
    /// the SAME read the content was computed against.</para>
    /// </summary>
    public static async Task WriteAsync(string path, string content, FileSnapshot on,
        CancellationToken ct)
    {
        EnsureParentDirectory(path);

        // THE FILE DECIDES, NOT THE CONTENT. A model reproducing a file it read sends bare \n
        // whatever the file uses, because a tool result cannot show it otherwise. Splicing that into
        // a CRLF file leaves it MIXED: every line the agent touched differs from every line it did
        // not, in a way no diff viewer explains. A file that does not exist yet has no convention to
        // keep, so its content goes through untouched.
        if (on.UsesCrlf)
            content = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace("\n", "\r\n", StringComparison.Ordinal);

        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: on.HadBom);
        await File.WriteAllTextAsync(path, content, encoding, ct);
    }

    /// <summary>
    /// Creates a file, failing if one is already there. Returns false when it existed.
    ///
    /// <para>DISTINCT FROM WRITE, because "make me a new file" and "make the file say this" are
    /// different intents that <c>write</c> cannot tell apart. A model creating <c>plans/x.md</c> and
    /// silently replacing a plan somebody was reading gets told afterwards, by the created/overwrote
    /// line — useful, and too late.</para>
    ///
    /// <para>ATOMIC, via CreateNew: the check and the create are one syscall, so nothing can appear
    /// at the path between them. An Exists() test followed by a write is the classic version of this
    /// and is exactly what a second process defeats — which matters here because cxagent's own
    /// sessions are separate processes over one working directory, and a kernel/session split would
    /// make that the ordinary case rather than the two-windows case.</para>
    ///
    /// <para>NOT ADVERTISED AS A TOOL. It is a narrower <c>write</c>, not a new capability, and a
    /// ninth tool costs schema bytes in every request of every session — including the ones that
    /// never create a file. Reachable as the <c>create</c> action for planned jobs and internal
    /// callers; the model gets <c>write_file</c>, whose description already says to read before
    /// overwriting, and whose result now says which of the two it did.</para>
    /// </summary>
    public static async Task<bool> CreateNewAsync(string path, string content, CancellationToken ct)
    {
        EnsureParentDirectory(path);
        try
        {
            // CreateNew throws IOException if the path exists — the atomicity this method is for.
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(content.AsMemory(), ct);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    /// <summary>
    /// Writes only if the file still holds what the caller read. Throws
    /// <see cref="StaleContentException"/> otherwise, having written nothing.
    ///
    /// <para>THE LOCK IS NOT ENOUGH ON ITS OWN. It serialises agents inside this process; it says
    /// nothing about the user's editor, a formatter, a build step or a git checkout touching the file
    /// between the read an edit was computed from and the write that applies it. Applying a stale
    /// edit succeeds, reports success, and silently reverts whatever happened in between — the worst
    /// shape of failure, because nothing anywhere records it.</para>
    ///
    /// <para>COMPARED AS TEXT, not bytes: the snapshot's Text is BOM-stripped and this reads the same
    /// way, so a file whose only change was a BOM being added does not read as an unrelated edit.</para>
    /// </summary>
    public static async Task WriteIfUnchangedAsync(string path, string content, FileSnapshot expected,
        CancellationToken ct)
    {
        var current = await ReadAsync(path, ct);

        if (current.Existed != expected.Existed
            || !string.Equals(current.Text, expected.Text, StringComparison.Ordinal))
            throw new StaleContentException(path);

        await WriteAsync(path, content, expected, ct);
    }

    /// <summary>
    /// Creates the directory a write is about to land in, when it does not already exist.
    ///
    /// <para>WITHOUT THIS, WRITING TO A NEW SUBDIRECTORY THROWS. Observed, not theorised: an agent
    /// wrote a plan to <c>./plans/x.md</c>, the write failed with DirectoryNotFoundException, it ran
    /// <c>mkdir -p plans</c> on the next turn to fix it, and then never retried the write.</para>
    /// </summary>
    public static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);
    }
}
