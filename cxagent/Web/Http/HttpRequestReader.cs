using System.Net;
using System.Text;

namespace CxAgent.Web.Http;

/// <summary>One HTTP request as the server received it. Header names compare case-insensitively.</summary>
/// <param name="Method">GET, POST, …</param>
/// <param name="Path">The path, without the query.</param>
/// <param name="Query">What followed <c>?</c>, or empty.</param>
/// <param name="Headers">Each header once; a repeated one keeps its last value, except those refused outright.</param>
/// <param name="Body">The body, read by <c>Content-Length</c>.</param>
/// <param name="Remote">Who sent it.</param>
public sealed record HttpRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    IPAddress Remote)
{
    /// <summary>A header's value, or null when the request did not send it.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>How much a request may be before it is refused.</summary>
/// <param name="MaxHeaderBytes">The request line and every header together.</param>
/// <param name="MaxBodyBytes">The body. This server's only bodies are a login form.</param>
public sealed record HttpLimits(int MaxHeaderBytes = 16384, int MaxBodyBytes = 4096)
{
    /// <summary>How long one request may take to arrive, from its first byte to its last.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>A request the reader will not take, and the status that says why.</summary>
public sealed class HttpRefused(int status, string reason) : Exception(reason)
{
    /// <summary>The HTTP status to answer with before closing.</summary>
    public int Status { get; } = status;
}

/// <summary>
/// Reads HTTP/1.1 requests off one connection, one after another.
///
/// <para>STRICT ON PURPOSE. This parser is written here rather than borrowed, so it accepts the one shape the
/// server needs and refuses the rest: bodies by <c>Content-Length</c> only (chunked is a second framing, and two
/// framings disagreeing is how a proxy in front and this server come to see different requests), no folded headers,
/// no duplicate lengths, HTTP/1.1 only. Every refusal is a status, never a guess.</para>
///
/// <para>BUFFERED ACROSS REQUESTS. A keep-alive connection can carry the next request in the same read as the end of
/// this one; the bytes after a request belong to the next call.</para>
/// </summary>
public sealed class HttpRequestReader(Stream stream, IPAddress remote, HttpLimits limits)
{
    private static readonly byte[] EndOfHeaders = "\r\n\r\n"u8.ToArray();

    private static readonly HashSet<string> Singular = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "origin", "cookie", "upgrade", "sec-websocket-key",
    };

    private readonly byte[] _buffer = new byte[Math.Max(limits.MaxHeaderBytes, 4096) + 4096];

    /// <summary>
    /// Whether a connection that goes quiet between requests is simply closed. On for the server: an idle keep-alive
    /// connection timing out is not a slow request, and answering it with an unasked-for 408 confuses clients.
    /// </summary>
    public bool IdleIsClose { get; init; }
    private int _start;
    private int _end;

    /// <summary>The next request; null when the connection closed cleanly before one began.</summary>
    /// <exception cref="HttpRefused">The request is malformed, too large, or too slow.</exception>
    public async Task<HttpRequest?> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limits.ReadTimeout);
        try
        {
            return await ReadCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (IdleIsClose && _end == _start) return null;
            throw new HttpRefused(408, "The request took too long to arrive.");
        }
    }

    private async Task<HttpRequest?> ReadCoreAsync(CancellationToken ct)
    {
        int headerEnd;
        while ((headerEnd = IndexOf(EndOfHeaders)) < 0)
        {
            if (_end - _start >= limits.MaxHeaderBytes) throw new HttpRefused(431, "The request's headers are too large.");
            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                if (_end == _start) return null;
                throw new HttpRefused(400, "The connection closed in the middle of a request.");
            }
        }
        if (headerEnd - _start > limits.MaxHeaderBytes) throw new HttpRefused(431, "The request's headers are too large.");

        // LATIN-1, NOT UTF-8: header bytes map one to one, and anything outside ASCII is refused below anyway.
        var head = Encoding.Latin1.GetString(_buffer, _start, headerEnd - _start);
        _start = headerEnd + EndOfHeaders.Length;

        var lines = head.Split("\r\n");
        var (method, path, query) = RequestLine(lines[0]);
        var headers = Headers(lines.AsSpan(1));

        if (headers.ContainsKey("transfer-encoding")) throw new HttpRefused(501, "Chunked bodies are not accepted.");

        // A REQUEST NAMES ONE HOST. Without one there is nothing to check it against; a proxy routing on one Host while
        // this server checks another is a classic way for the two to see different requests.
        if (!headers.ContainsKey("host")) throw new HttpRefused(400, "Host is required.");

        var length = 0;
        if (headers.TryGetValue("content-length", out var declared)
            && (!int.TryParse(declared, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length)))
            throw new HttpRefused(400, "Content-Length is not a number.");
        if (length > limits.MaxBodyBytes) throw new HttpRefused(413, "The request's body is too large.");

        var body = new byte[length];
        var have = 0;
        while (have < length)
        {
            if (_end == _start && !await FillAsync(ct).ConfigureAwait(false))
                throw new HttpRefused(400, "The connection closed in the middle of a request.");
            var take = Math.Min(length - have, _end - _start);
            Array.Copy(_buffer, _start, body, have, take);
            _start += take;
            have += take;
        }

        return new HttpRequest(method, path, query, headers, body, remote);
    }

    private static (string Method, string Path, string Query) RequestLine(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiLetterUpper))
            throw new HttpRefused(400, "Malformed request line.");
        if (parts[2] != "HTTP/1.1")
            throw parts[2].StartsWith("HTTP/", StringComparison.Ordinal)
                ? new HttpRefused(505, "Only HTTP/1.1 is served.")
                : new HttpRefused(400, "Malformed request line.");

        var target = parts[1];
        if (!target.StartsWith('/') || target.Any(c => c <= ' ' || c > '~')) throw new HttpRefused(400, "Malformed request target.");
        var q = target.IndexOf('?');
        return q < 0 ? (parts[0], target, "") : (parts[0], target[..q], target[(q + 1)..]);
    }

    private static Dictionary<string, string> Headers(ReadOnlySpan<string> lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            // A FOLDED CONTINUATION LINE is obsolete, and two parsers disagreeing about it is a smuggling vector.
            if (line.Length == 0 || line[0] is ' ' or '\t') throw new HttpRefused(400, "Malformed header.");
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].All(IsTokenChar)) throw new HttpRefused(400, "Malformed header.");
            var name = line[..colon];
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(c => c > '~' || (c < ' ' && c != '\t'))) throw new HttpRefused(400, "Malformed header.");

            // TWO OF A HEADER THAT DECIDES SOMETHING are two answers — where the request ends, which host, which page,
            // which login. Refuse rather than pick one, since whatever sits in front may have picked the other.
            if (headers.ContainsKey(name) && Singular.Contains(name))
                throw new HttpRefused(400, $"{name} given twice.");
            headers[name] = value;
        }
        return headers;
    }

    // RFC 9110's token characters — what a header name may be made of.
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private int IndexOf(byte[] needle)
    {
        var at = _buffer.AsSpan(_start, _end - _start).IndexOf(needle);
        return at < 0 ? -1 : _start + at;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        // MOVE WHAT IS LEFT TO THE FRONT so the buffer's tail is free; a request never needs more than its limits.
        if (_start > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) return true;
        var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read == 0) return false;
        _end += read;
        return true;
    }
}
