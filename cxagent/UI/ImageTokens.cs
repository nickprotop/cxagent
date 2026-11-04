using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CxAgent.Core.Models;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Imaging;
using StbImageWriteSharp;

namespace CxAgent.UI;

/// <summary>
/// Which pastes become an image token in the composer, and the image each one carries.
///
/// <para>A DROPPED FILE ARRIVES AS ITS PATH. A terminal cannot paste an image: dragging a file onto it
/// types the file's path, quoted or escaped the terminal's own way. So a paste that is nothing but paths
/// to images is taken as the user handing those images over, and anything else — a path inside a
/// sentence, a source file, a path that is not there — stays the text it is, which the model can read
/// with read_file if it needs to.</para>
///
/// <para>ONE PER COMPOSER, for the reason <see cref="PasteTokens"/> gives: the numbers name the images in
/// a conversation apart, and count up for the composer's life.</para>
/// </summary>
internal sealed class ImageTokens
{
    /// <summary>The token's tag, telling an image apart from a paste.</summary>
    internal const string Tag = "image";

    /// <summary>
    /// The longest edge sent. Anthropic recommends it as the size past which an image only costs more,
    /// and it holds a screenshot's text readable; at it a screenshot is under 2,000 tokens anywhere.
    /// </summary>
    internal const int MaxEdge = 1568;

    /// <summary>
    /// The most an image may weigh as sent: Anthropic refuses one over 5 MB, and that is counted after
    /// base64, which turns three bytes into four.
    /// </summary>
    internal const int MaxSentBytes = 5 * 1024 * 1024 / 4 * 3;

    /// <summary>The quality a photo too large for PNG is sent at: past it, the bytes grow faster than what is seen.</summary>
    private const int JpegQuality = 85;

    /// <summary>
    /// A file larger than this is not decoded. A decode runs on the UI thread at the moment of the paste,
    /// and no screenshot is this big; past it the paste is something else, and stays text.
    /// </summary>
    internal const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The image each token carries. BY THE TOKEN'S IDENTITY: a token is a record, so two images with
    /// the same label would be equal by value, and the composer's history keeps the token objects
    /// themselves, so a recalled line finds its images here too.
    /// </summary>
    private static readonly ConditionalWeakTable<PromptToken, AttachedImage> Images = new();

    private static readonly ConditionalWeakTable<PromptControl, ImageTokens> PerComposer = new();

    private readonly PromptControl _composer;
    private int _count;

    /// <summary>
    /// Why this composer's model cannot take an image, or null when it can or nobody knows yet. Set by the
    /// tab, which knows the session; unset, every image is taken.
    /// </summary>
    internal Func<string?> CannotSee { get; set; } = () => null;

    /// <summary>
    /// The refusal for <paramref name="provider"/>: <c>local:qwen3 can't see images</c> when it is known not
    /// to, and null otherwise.
    ///
    /// <para>NOT KNOWN YET STARTS THE QUESTION AND LETS THIS IMAGE GO. Asking on the UI thread would hold
    /// the paste for as long as the endpoint takes, and the request itself asks again before it is sent,
    /// so an image that turns out unseeable goes as its placeholder rather than failing the turn. An
    /// answer of no that comes after is handed to <paramref name="late"/>, so the user hears it with the
    /// line still in front of them rather than from a reply that never mentions the picture.</para>
    /// </summary>
    /// <param name="provider">The composer's model.</param>
    /// <param name="late">Told when the answer comes after the paste and is no; called off the UI thread.
    /// Null when nothing was pasted — a tab warming the question.</param>
    internal static string? Refusal(Core.Llm.ILlmProvider? provider, Action<string>? late = null)
    {
        if (provider is not Core.Llm.IImageCapability capability) return null;
        var cannot = $"{provider.ProviderId}:{provider.ModelId} can't see images";
        if (capability.KnownCanSeeImages is null)
        {
            _ = capability.CanSeeImagesAsync().ContinueWith(answer =>
            {
                if (answer.Result == false) late?.Invoke(cannot + "; the image will go as its placeholder");
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            return null;
        }
        return capability.KnownCanSeeImages == false ? cannot : null;
    }

    private ImageTokens(PromptControl composer) => _composer = composer;

    /// <summary>
    /// What is said when a paste that named images could not be taken as them — a format that cannot
    /// be read, a file too large. Process-wide because a composer is made before any window system
    /// exists to toast on; the composition root sets it once.
    /// </summary>
    internal static Action<string> Refused { get; set; } = _ => { };

    /// <summary>The rule for <paramref name="composer"/>, made on first use.</summary>
    internal static ImageTokens For(PromptControl composer) => PerComposer.GetValue(composer, c => new ImageTokens(c));

    /// <summary>The image a token carries, or null for any other token.</summary>
    internal static AttachedImage? Of(PromptToken? token) =>
        token is not null && Images.TryGetValue(token, out var image) ? image : null;

    /// <summary>
    /// Ctrl+G: expands the pasted token beside the caret, and never an image's.
    ///
    /// <para>THE CONTROL KEEPS ITS "WHICH TOKEN IS AT THE CARET" PRIVATE, so this lets it expand and looks
    /// at what went. An image comes back at once, the line as it was, and is handed out for the caller
    /// to show instead. Putting it back sets the segments whole, which leaves the caret at the end of
    /// the line; that is the cost of not knowing in advance.</para>
    /// </summary>
    /// <param name="composer">The composer the key was pressed in.</param>
    /// <param name="image">The image token's image, when that is what the caret was beside.</param>
    /// <returns>Whether something happened: a paste expanded, or an image found.</returns>
    internal static bool ExpandPasteAtCaret(PromptControl composer, out AttachedImage? image)
    {
        image = null;
        var before = composer.InputSegments;
        if (!composer.ExpandTokenAtCaret()) return false;

        var after = composer.InputSegments.Select(s => s.Token).OfType<PromptToken>().ToHashSet(ReferenceEqualityComparer.Instance);
        var gone = before.Select(s => s.Token).OfType<PromptToken>().FirstOrDefault(t => !after.Contains(t));
        if (Of(gone) is not { } found) return true;

        composer.SetInputSegments(before);
        image = found;
        return true;
    }

    /// <summary>
    /// The token for a paste of image paths, or null when it should paste as something else.
    ///
    /// <para>SEVERAL FILES DROPPED TOGETHER ARE SEVERAL TOKENS. The composer takes one token back from a
    /// paste filter, so every one but the last is inserted here, in order, and the last is returned for
    /// the composer to insert.</para>
    ///
    /// <para>ALL OR NOTHING. If any of the paths is not an image that can be read, none is taken, the
    /// paste stays text, and the reason is said: half a drop turned into tokens beside a path left as
    /// text reads as a drop that worked.</para>
    /// </summary>
    internal PromptToken? Paste(string pasted)
    {
        var paths = BareLines(pasted) is { } bare && bare.All(File.Exists) ? bare : SplitPaths(pasted);
        if (paths is not { Count: > 0 }) return null;
        if (!paths.All(File.Exists) || !paths.All(LooksLikeAnImage)) return null;
        if (CannotSee() is { } cannot)
        {
            Refused(cannot);
            return null;
        }

        var prepared = new List<Prepared>();
        foreach (var path in paths)
        {
            if (Prepare(File.ReadAllBytes, path, out var refusal) is { } image) prepared.Add(image);
            else
            {
                if (refusal is not null) Refused(refusal);
                return null;
            }
        }

        var tokens = prepared.Select(Token).ToList();
        foreach (var token in tokens.SkipLast(1)) _composer.InsertToken(token);
        return tokens[^1];
    }

    /// <summary>
    /// The token for an image read off the clipboard, or null with the reason.
    ///
    /// <para>THE SAME ROAD AS A FILE: the same decode, shrink and encode, the same numbering, the same
    /// refusal for a model known not to see it.</para>
    /// </summary>
    /// <param name="encoded">The clipboard's bytes, as <c>ClipboardHelper.GetImage</c> gives them.</param>
    /// <param name="refusal">Why not, for a toast.</param>
    internal PromptToken? FromBytes(byte[] encoded, out string? refusal)
    {
        if (CannotSee() is { } cannot)
        {
            refusal = cannot;
            return null;
        }
        return Encode(encoded, "the clipboard's image", out refusal) is { } prepared ? Token(prepared) : null;
    }

    /// <summary>
    /// Ctrl+V (Alt+V on Windows): the clipboard's image into the composer at the caret.
    ///
    /// <para>RETURNS WHETHER THE KEY WAS USED. With no image there, Ctrl+V is still the text paste it has
    /// always been, so the caller lets the key go on to it; Alt+V has no text paste behind it, and says
    /// there was nothing.</para>
    ///
    /// <para>TEXT WINS. Spreadsheet cells and slides copy as their text and a picture of it together, and
    /// what the user meant to paste is the text; so with any text there, Ctrl+V leaves the key to the text
    /// paste and the image is not read at all.</para>
    /// </summary>
    /// <param name="read">Reads the clipboard's image, or null.</param>
    /// <param name="sayWhenEmpty">Whether an empty clipboard is worth a toast.</param>
    /// <param name="hasText">Whether the clipboard holds text a text paste would take; null for Alt+V,
    /// which pastes only images.</param>
    internal bool PasteClipboard(Func<byte[]?> read, bool sayWhenEmpty, Func<bool>? hasText = null)
    {
        if (hasText?.Invoke() == true) return false;
        if (read() is not { Length: > 0 } bytes)
        {
            if (sayWhenEmpty) Refused("no image on the clipboard");
            return sayWhenEmpty;
        }
        if (FromBytes(bytes, out var refusal) is { } token) _composer.InsertToken(token);
        else if (refusal is not null) Refused(refusal);
        return true;
    }

    private PromptToken Token(Prepared prepared)
    {
        _count++;
        var label = string.Create(CultureInfo.InvariantCulture,
            $"[Image #{_count} {prepared.Width}×{prepared.Height}]");
        var token = new PromptToken(label, label, Tag);
        Images.AddOrUpdate(token, new AttachedImage(label, prepared.Hash, prepared.Width, prepared.Height)
        {
            Bytes = prepared.Bytes,
            OriginalWidth = prepared.Original.Width,
            OriginalHeight = prepared.Original.Height,
            MediaType = prepared.MediaType,
        });
        return token;
    }

    /// <summary>
    /// Whether a file's first bytes are an image's: PNG, JPEG, GIF or BMP.
    ///
    /// <para>ASKED BEFORE ANYTHING ELSE. Pasting a source file's path is routine here, and that paste must
    /// stay text and say nothing — not read the file on the UI thread, not refuse it as an image the
    /// model cannot see. Only a file that looks like an image is read; one that looks like one and does
    /// not decode is the case worth a toast. TGA carries no signature, so a dropped TGA stays its path.</para>
    /// </summary>
    internal static bool LooksLikeAnImage(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            using var file = File.OpenRead(path);
            var n = file.Read(head);
            var h = head[..n];
            return h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])
                || h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
                || h.StartsWith("GIF8"u8)
                || h.StartsWith("BM"u8);
        }
        catch (Exception)
        {
            // Unreadable here means unreadable to the decode too, and the path stays text either way.
            return false;
        }
    }

    /// <summary>An image ready to send: its PNG, its hash, and the size it is sent at.</summary>
    internal sealed record Prepared(byte[] Bytes, string Hash, int Width, int Height)
    {
        /// <summary>The size of what the user gave, before it was shrunk.</summary>
        public (int Width, int Height) Original { get; init; }

        /// <summary>How <see cref="Bytes"/> are encoded.</summary>
        public string MediaType { get; init; } = "image/png";
    }

    /// <summary>
    /// Reads, decodes, shrinks and encodes one image, or says why not.
    ///
    /// <para>DECODED BY CONTENT, NOT BY NAME, so a <c>.png</c> that is a JPEG is read, a screenshot saved
    /// without an extension is read, and a <c>.png</c> that is not an image at all is refused.</para>
    ///
    /// <para>RE-ENCODED EVEN WHEN NOTHING SHRANK, as PNG unless that is too large (see <see cref="Encode"/>).
    /// The bytes stored are the bytes sent, under the type they were written as, so a resume sends
    /// exactly what the model saw. The decoder keeps no alpha, so a transparent area arrives black.</para>
    /// </summary>
    /// <param name="read">Reads the file — a seam for the size check, which must run before the read.</param>
    /// <param name="path">The file.</param>
    /// <param name="refusal">Why not, in words for a toast; null when the file is simply not ours to take.</param>
    internal static Prepared? Prepare(Func<string, byte[]> read, string path, out string? refusal)
    {
        refusal = null;
        var name = Path.GetFileName(path);
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                refusal = $"{name} is over {MaxFileBytes / (1024 * 1024)} MB; pasted as its path";
                return null;
            }
            return Encode(read(path), name, out refusal);
        }
        catch (IOException e)
        {
            refusal = $"can't read {name}: {e.Message}";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            refusal = $"can't read {name}: permission denied";
            return null;
        }
    }

    /// <summary>The same from bytes already in hand — a clipboard's image.</summary>
    internal static Prepared? Encode(byte[] encoded, string name, out string? refusal)
    {
        refusal = null;
        PixelBuffer pixels;
        try
        {
            pixels = PixelBuffer.FromStream(new MemoryStream(encoded));
        }
        catch (Exception)
        {
            // Unsupported formats raise their own exception, and a truncated or corrupt file raises
            // whatever the decoder trips on; both are "not something cxagent can read".
            refusal = $"can't read {name}: not a format cxagent reads (PNG, JPEG, BMP, GIF)";
            return null;
        }

        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(pixels.Width, pixels.Height));
        var width = Math.Max(1, (int)Math.Round(pixels.Width * scale));
        var height = Math.Max(1, (int)Math.Round(pixels.Height * scale));
        var rgb = scale < 1.0 ? Shrink(pixels, width, height) : Rgb(pixels);

        using var ms = new MemoryStream();
        new ImageWriter().WritePng(rgb, width, height, ColorComponents.RedGreenBlue, ms);
        var mediaType = "image/png";

        // A PHOTO PNG CANNOT HOLD IS SENT AS JPEG. A screenshot's flat colour packs small, and stays PNG,
        // whose edges keep its text sharp; a photo's noise barely compresses, and at the longest edge
        // sent its PNG can pass what Anthropic takes. A JPEG at that size is a fraction of the limit.
        if (ms.Length > MaxSentBytes)
        {
            ms.SetLength(0);
            new ImageWriter().WriteJpg(rgb, width, height, ColorComponents.RedGreenBlue, ms, JpegQuality);
            mediaType = "image/jpeg";
        }
        var bytes = ms.ToArray();
        return new Prepared(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), width, height)
        {
            Original = (pixels.Width, pixels.Height),
            MediaType = mediaType,
        };
    }

    private static byte[] Rgb(PixelBuffer pixels)
    {
        var rgb = new byte[pixels.Width * pixels.Height * 3];
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var p = pixels.GetPixel(x, y);
                var i = (y * pixels.Width + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = (p.R, p.G, p.B);
            }
        return rgb;
    }

    /// <summary>
    /// Shrinks by averaging each target pixel's area of the source.
    ///
    /// <para>NOT <see cref="PixelBuffer.Resize"/>: it caps both sides at the library's display limit of
    /// 500 and clamps each on its own, so a 3000×2000 screenshot came back 500×500, squashed. And an area
    /// average keeps a screenshot's text legible where sampling between four neighbours drops whole
    /// strokes once the scale passes two.</para>
    /// </summary>
    private static byte[] Shrink(PixelBuffer source, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        var sx = (double)source.Width / width;
        var sy = (double)source.Height / height;
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)(y * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(source.Height, (int)((y + 1) * sy)));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)(x * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(source.Width, (int)((x + 1) * sx)));
                long r = 0, g = 0, b = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var p = source.GetPixel(xx, yy);
                        r += p.R; g += p.G; b += p.B;
                    }
                var n = (x1 - x0) * (y1 - y0);
                var i = (y * width + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
        return rgb;
    }

    /// <summary>
    /// The paths in a paste that is nothing but paths, or null when it is anything else.
    ///
    /// <para>EVERY TERMINAL'S OWN FORM: GNOME Terminal, and kitty at a shell prompt, quote a dropped path
    /// in single quotes (kitty elsewhere sends it bare, which <see cref="BareLines"/> reads);
    /// macOS Terminal and iTerm2 escape its spaces with backslashes; Windows Terminal uses double
    /// quotes; some send a <c>file://</c> URL. Several files come separated by unquoted spaces. A word
    /// that is not a path — the "look at" of "look at /tmp/a.png" — makes the whole paste text.</para>
    /// </summary>
    internal static IReadOnlyList<string>? SplitPaths(string pasted)
    {
        var text = pasted.Trim();
        if (text.Length == 0 || text.Contains('\n')) return null;

        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else word.Append(c);
            }
            else if (c is '\'' or '"') quote = c;
            // A BACKSLASH ESCAPES ONLY ON A POSIX PATH. On Windows it is the separator, and C:\x\y
            // would lose every one of them.
            else if (c == '\\' && i + 1 < text.Length && !LooksLikeWindows(text)) word.Append(text[++i]);
            else if (c == ' ')
            {
                if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
            }
            else word.Append(c);
        }
        if (quote is not null) return null;
        if (word.Length > 0) words.Add(word.ToString());

        var paths = new List<string>();
        foreach (var w in words)
        {
            if (w.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(w, UriKind.Absolute, out var uri) && uri.IsFile)
                paths.Add(uri.LocalPath);
            else if (Path.IsPathFullyQualified(w) || IsDrivePath(w) || w.StartsWith(@"\\", StringComparison.Ordinal) || w.StartsWith("~/", StringComparison.Ordinal))
                paths.Add(w.StartsWith("~/", StringComparison.Ordinal)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), w[2..])
                    : w);
            else return null;
        }
        return paths;
    }

    /// <summary>
    /// The lines of a paste that is one bare absolute path to a line, or null when it is anything else.
    ///
    /// <para>KITTY'S FORM OUTSIDE A SHELL PROMPT: a drop anywhere but a prompt kitty's shell integration
    /// marked pastes each path as it is, unquoted, one to a line. "Screenshot from 2026-11-04.png" split
    /// at its spaces is three words that are not files, so a whole line is tried as one path first, and
    /// only a line that exists is taken that way.</para>
    /// </summary>
    internal static IReadOnlyList<string>? BareLines(string pasted)
    {
        var lines = pasted.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count > 0 && lines.All(Path.IsPathFullyQualified) ? lines : null;
    }

    /// <summary>
    /// <c>C:\x</c>, which <see cref="Path.IsPathFullyQualified(string)"/> calls relative everywhere but on
    /// Windows. Taken as a path anywhere, since a paste can only name an existing file and the existence
    /// check settles the rest.
    /// </summary>
    private static bool IsDrivePath(string w) =>
        w.Length >= 3 && char.IsAsciiLetter(w[0]) && w[1] == ':' && w[2] is '\\' or '/';

    /// <summary>
    /// Whether the paste is a Windows path, whose backslashes are separators rather than escapes: a drive
    /// path (<c>C:\x</c>) or a share (<c>\\server\share</c>).
    /// </summary>
    private static bool LooksLikeWindows(string text) => text.TrimStart('"', '\'').StartsWith(@"\\", StringComparison.Ordinal) ||
        text.Length >= 3 && char.IsLetter(text.TrimStart('"', '\'')[0]) && text.TrimStart('"', '\'')[1..].StartsWith(":\\", StringComparison.Ordinal);
}
