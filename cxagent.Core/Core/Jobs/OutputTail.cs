using System.Text;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Jobs;

/// <summary>
/// Reading a background command's output file while the command is still writing it.
///
/// <para>NOTHING HERE TRUSTS THE FILE. A command prints what it likes: colour escapes, a window title,
/// a progress bar redrawn with carriage returns, bytes that are not UTF-8. What reaches the screen is
/// text with all of that resolved — and markup characters left alone, because escaping is the
/// renderer's job, and doing it here would paste backslashes into any reader that is not markup.</para>
///
/// <para>OPENED WITH ReadWrite | Delete SHARING because the job still holds the file for writing, and
/// the session store may prune the directory under an open tab.</para>
/// </summary>
public static class OutputTail
{
    /// <summary>How much of the file's end <see cref="LastLine"/> reads. A last line longer than this
    /// shows its end, which for a panel row cut to thirty columns is no loss.</summary>
    public const int LastLineWindow = 4096;

    // CSI (colour, cursor movement), OSC (window title) terminated by BEL or ST, and the two-byte
    // escapes. Anything else that is a control character goes in Clean's loop.
    private static readonly Regex Escapes = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])",
        RegexOptions.Compiled);

    /// <summary>Text with terminal escapes, control characters and byte-order marks removed, tabs as
    /// four spaces.</summary>
    public static string Clean(string text)
    {
        var stripped = Escapes.Replace(text, "");
        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            // THE BYTE-ORDER MARK TOO, which is a format character rather than a control one and so
            // passes IsControl: the output file is written with one, and a job that has printed
            // nothing yet showed it as a stray glyph where its last line goes.
            if (c == '\t') sb.Append("    ");
            else if (c != '\uFEFF' && !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// What a line shows after its carriage returns have done their work: the last segment that is
    /// not blank.
    ///
    /// <para>NOT BLANK, rather than simply last, because a progress bar commonly ends each redraw with
    /// the \r rather than starting with it — the literal last segment is then empty.</para>
    /// </summary>
    public static string LatestRedraw(string line)
    {
        var segments = line.Split('\r');
        for (var i = segments.Length - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(segments[i])) return segments[i];
        return "";
    }

    /// <summary>
    /// A command as one line: its first non-blank line, with an ellipsis when more follow.
    ///
    /// <para>A MODEL WRITES MULTI-LINE COMMANDS — a for loop over three lines is ordinary — and a panel
    /// row, a picker entry and a tab title each have exactly one line to give it. A raw newline there
    /// breaks the layout rather than wrapping politely.</para>
    /// </summary>
    public static string OneLine(string command)
    {
        var lines = command.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return "";
        return lines.Count == 1 ? lines[0] : lines[0] + " …";
    }

    /// <summary>The last line with anything on it, cleaned; null when there is none, or no file.</summary>
    public static string? LastLine(string? path) => LastLines(path, 1).FirstOrDefault();

    /// <summary>
    /// The last <paramref name="count"/> lines with anything on them, cleaned, oldest first; empty when
    /// there are none, or no file.
    ///
    /// <para>BLANK LINES ARE SKIPPED, not counted: a build that ends with two empty lines would
    /// otherwise show two blank rows where its result belongs.</para>
    /// </summary>
    public static IReadOnlyList<string> LastLines(string? path, int count)
    {
        if (path is null || count <= 0) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - LastLineWindow);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var text = Encoding.UTF8.GetString(buffer, 0, read);

            var found = new List<string>();
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0 && found.Count < count; i--)
            {
                var shown = Clean(LatestRedraw(lines[i])).Trim();
                if (shown.Length > 0) found.Add(shown);
            }
            found.Reverse();
            return found;
        }
        catch (Exception) { return []; }   // missing, unreadable, pruned: no rows, never a throw
    }
}

/// <summary>
/// Reads what a growing file gained since the last read.
///
/// <para>STATEFUL ON PURPOSE. The offset means a tab following a long build reads each byte once
/// rather than the whole file every second, and the decoder carries a multi-byte character that one
/// read split, which a fresh decode per chunk would turn into two replacement marks.</para>
/// </summary>
public sealed class OutputFollower(string path)
{
    /// <summary>
    /// Where a first read starts from the end of a large file, and the most one read takes.
    ///
    /// <para>THE TAIL, because the end is where a command's answer is — a failing build's error and
    /// its summary are last — and a two-gigabyte log loaded whole would stall the frame loop that
    /// reads it.</para>
    /// </summary>
    public const int MaxInitialBytes = 256 * 1024;

    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _offset = -1;

    /// <summary>Whether the first read skipped the start of the file, so a reader can say so.</summary>
    public bool StartedFromTail { get; private set; }

    /// <summary>The text appended since the last call; "" when nothing was; null when the file
    /// cannot be read.</summary>
    public string? ReadNew()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (_offset < 0)
            {
                _offset = Math.Max(0, stream.Length - MaxInitialBytes);
                StartedFromTail = _offset > 0;
            }

            // SHORTER THAN WHAT WAS READ means the file was rewritten, not appended to. Reading on
            // from the old offset would wait forever for bytes that will never come there.
            if (stream.Length < _offset)
            {
                _offset = 0;
                _decoder = Encoding.UTF8.GetDecoder();
            }

            var available = stream.Length - _offset;
            if (available <= 0) return "";

            stream.Seek(_offset, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(available, MaxInitialBytes)];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            _offset += read;

            var chars = new char[_decoder.GetCharCount(bytes, 0, read, flush: false)];
            _decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            return new string(chars);
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// A command's output as screen lines, built up chunk by chunk.
///
/// <para>A CHUNK ENDS WHEREVER A READ DID, not at a newline, so the last line stays open and the next
/// chunk continues it. Each line is shown as its <see cref="OutputTail.LatestRedraw"/>, cleaned.</para>
/// </summary>
public sealed class OutputLines
{
    /// <summary>The most lines kept. Past it the oldest go: a markup control re-laid out over a million
    /// lines every second is a frozen screen, and the whole file is a path away.</summary>
    public const int MaxLines = 5000;

    /// <summary>The longest an unterminated line may grow — a progress bar that redraws with \r for an
    /// hour never sends a \n, and would otherwise grow without bound.</summary>
    private const int MaxOpenLine = 16 * 1024;

    private readonly List<string> _raw = [""];

    public void Append(string chunk)
    {
        var parts = chunk.Split('\n');
        _raw[^1] += parts[0];
        for (var i = 1; i < parts.Length; i++) _raw.Add(parts[i]);

        if (_raw[^1].Length > MaxOpenLine) _raw[^1] = _raw[^1][^MaxOpenLine..];

        // ONE MORE THAN THE CAP, because the last entry is the open line, which may be empty.
        if (_raw.Count > MaxLines + 1) _raw.RemoveRange(0, _raw.Count - (MaxLines + 1));
    }

    /// <summary>The lines to show. An empty open line — the file ended with a newline — is not one.</summary>
    public IReadOnlyList<string> Display()
    {
        var count = _raw[^1].Length == 0 ? _raw.Count - 1 : _raw.Count;
        var start = Math.Max(0, count - MaxLines);
        return [.. _raw.Skip(start).Take(count - start)
                       .Select(r => OutputTail.Clean(OutputTail.LatestRedraw(r)))];
    }
}
