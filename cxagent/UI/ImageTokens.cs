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
        if (SplitPaths(pasted) is not { Count: > 0 } paths) return null;
        if (!paths.All(File.Exists)) return null;

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

    private PromptToken Token(Prepared prepared)
    {
        _count++;
        var label = string.Create(CultureInfo.InvariantCulture,
            $"[Image #{_count} {prepared.Width}×{prepared.Height}]");
        var token = new PromptToken(label, label, Tag);
        Images.AddOrUpdate(token, new AttachedImage(label, prepared.Hash, prepared.Width, prepared.Height)
        {
            Png = prepared.Png,
        });
        return token;
    }

    /// <summary>An image ready to send: its PNG, its hash, and the size it is sent at.</summary>
    internal sealed record Prepared(byte[] Png, string Hash, int Width, int Height);

    /// <summary>
    /// Reads, decodes, shrinks and encodes one image, or says why not.
    ///
    /// <para>DECODED BY CONTENT, NOT BY NAME, so a <c>.png</c> that is a JPEG is read, a screenshot saved
    /// without an extension is read, and a <c>.png</c> that is not an image at all is refused.</para>
    ///
    /// <para>RE-ENCODED AS PNG EVEN WHEN NOTHING SHRANK. The wire builders send every image as
    /// <c>image/png</c>, and the bytes stored are the bytes sent, so a resume sends exactly what the model
    /// saw. The decoder keeps no alpha, so a transparent area arrives black.</para>
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
        var png = ms.ToArray();
        return new Prepared(png, Convert.ToHexStringLower(SHA256.HashData(png)), width, height);
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
    /// <para>EVERY TERMINAL'S OWN FORM: GNOME Terminal and kitty quote a dropped path in single quotes;
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
            else if (Path.IsPathFullyQualified(w) || IsDrivePath(w) || w.StartsWith("~/", StringComparison.Ordinal))
                paths.Add(w.StartsWith("~/", StringComparison.Ordinal)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), w[2..])
                    : w);
            else return null;
        }
        return paths;
    }

    /// <summary>
    /// <c>C:\x</c>, which <see cref="Path.IsPathFullyQualified(string)"/> calls relative everywhere but on
    /// Windows. Taken as a path anywhere, since a paste can only name an existing file and the existence
    /// check settles the rest.
    /// </summary>
    private static bool IsDrivePath(string w) =>
        w.Length >= 3 && char.IsAsciiLetter(w[0]) && w[1] == ':' && w[2] is '\\' or '/';

    private static bool LooksLikeWindows(string text) =>
        text.Length >= 3 && char.IsLetter(text.TrimStart('"', '\'')[0]) && text.TrimStart('"', '\'')[1..].StartsWith(":\\", StringComparison.Ordinal);
}
