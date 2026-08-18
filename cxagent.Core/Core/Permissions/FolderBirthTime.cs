using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CxAgent.Core.Permissions;

/// <summary>
/// When a directory was actually created, on Linux.
///
/// <para>WHY THIS EXISTS AT ALL. <see cref="FolderIdentity"/> needs a value that says "this is the
/// same folder" and keeps saying it while an agent writes files into it. .NET's
/// <c>DirectoryInfo.CreationTimeUtc</c> is that value on Windows and macOS. On Linux it is not:
/// there is no portable way to read a birth time through libc's <c>stat</c>, so the runtime returns
/// the CHANGE time instead — a clock that moves whenever anything inside the directory is created,
/// renamed or removed.</para>
///
/// <para>The consequence was measured, not theorised: during one drive the same folder was trusted
/// twice and <c>cd*</c> was granted three times under three different scopes, because each file the
/// agent wrote moved the clock and orphaned the previous grant. Permissions that expire the moment
/// the agent does its job are worse than no scoping at all — they train the user to click through
/// prompts.</para>
///
/// <para>THE FIRST FIX WAS A HEURISTIC AND WAS WRONG. It compared creation against last-write and
/// dropped the suffix when they matched, on the reasoning that a real birth time is usually earlier.
/// A FRESHLY CREATED FOLDER HAS THEM EQUAL ON EVERY PLATFORM — so it discarded the identity on
/// Windows and macOS for exactly the new folders where "someone recreated this path" is most live.
/// Guessing at a value is not a substitute for reading it.</para>
///
/// <para>ONE SYSCALL, ONE PLATFORM. <c>statx</c> reports birth time directly and has since Linux
/// 4.11 (2017). The objection to native interop here was that it means three platforms' worth of
/// P/Invoke — it does not: Windows and macOS already work through the BCL, and this is the single
/// call that fills the one hole.</para>
/// </summary>
internal static class FolderBirthTime
{
    private const int AT_FDCWD = -100;

    /// <summary>Whatever <c>stat</c> would do — no forced sync, no network round trip.</summary>
    private const int AT_STATX_SYNC_AS_STAT = 0x0000;

    /// <summary>Ask for (and check for) the birth time. Nothing else is requested.</summary>
    private const uint STATX_BTIME = 0x00000800;

    /// <summary>
    /// The directory's creation time, or null when it cannot be had.
    ///
    /// <para>NULL IS A REAL ANSWER, not a failure to report. A filesystem that does not record birth
    /// times says so in the returned mask; so does a kernel older than <c>statx</c>. The caller
    /// falls back to the path alone, which is what it did before any of this existed.</para>
    /// </summary>
    internal static DateTime? Of(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var rc = Statx(AT_FDCWD, path, AT_STATX_SYNC_AS_STAT, STATX_BTIME, out var buffer);

            // THE MASK IS NOT OPTIONAL TO CHECK. statx succeeds while declining to fill a field —
            // several filesystems have no birth time — and the struct is zeroed, so an unchecked
            // read yields the Unix epoch and every folder on that mount becomes the same folder.
            if (rc != 0 || (buffer.Mask & STATX_BTIME) == 0) return null;

            return DateTimeOffset.FromUnixTimeSeconds(buffer.Btime.Seconds).UtcDateTime;
        }
        catch (Exception)
        {
            // A missing or unusable libc is not worth a crash in a permission lookup. Same contract
            // as the caller: no identity means the path alone, never a wrong identity.
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    [DllImport("libc", EntryPoint = "statx", SetLastError = true,
        CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Statx(int dirfd, string path, int flags, uint mask, out StatxBuffer buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        internal int Reserved;
    }

    /// <summary>
    /// The kernel's <c>struct statx</c>, as far as the birth time and no further.
    ///
    /// <para>PADDED TO THE FULL 256 BYTES DELIBERATELY. Only the fields up to <c>btime</c> are read,
    /// but the kernel writes the whole structure into the buffer it is given — a struct that stopped
    /// at the last field this code cares about would be written past its end, which is a stack
    /// corruption that would not look like one.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatxBuffer
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint HardLinks;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Spare;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal StatxTimestamp Atime;
        internal StatxTimestamp Btime;
    }
}
