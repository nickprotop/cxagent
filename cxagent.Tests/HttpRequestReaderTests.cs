using System.Net;
using System.Text;
using CxAgent.Web.Http;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The web server's request reader: what it accepts, and the status it refuses everything else with.</summary>
public sealed class HttpRequestReaderTests
{
    private static HttpRequestReader Reader(string raw, HttpLimits? limits = null) =>
        new(new MemoryStream(Encoding.ASCII.GetBytes(raw)), IPAddress.Loopback, limits ?? new HttpLimits());

    private static async Task<int> RefusalOf(string raw, HttpLimits? limits = null)
    {
        var refused = await Assert.ThrowsAsync<HttpRefused>(() => Reader(raw, limits).ReadAsync(CancellationToken.None));
        return refused.Status;
    }

    [Fact]
    public async Task Reads_get_with_headers_case_insensitively()
    {
        var r = await Reader("GET /a/b?x=1 HTTP/1.1\r\nHost: h\r\nX-Thing:  v \r\n\r\n").ReadAsync(CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal("GET", r!.Method);
        Assert.Equal("/a/b", r.Path);
        Assert.Equal("x=1", r.Query);
        Assert.Equal("v", r.Headers["x-thing"]);
        Assert.Equal("h", r.Headers["HOST"]);
        Assert.Empty(r.Body);
    }

    [Fact]
    public async Task Reads_post_body_by_content_length()
    {
        var r = await Reader("POST /login HTTP/1.1\r\nHost: h\r\nContent-Length: 9\r\n\r\nuser=nick").ReadAsync(CancellationToken.None);
        Assert.Equal("user=nick", Encoding.ASCII.GetString(r!.Body));
    }

    [Fact]
    public async Task Chunked_is_refused_501() =>
        Assert.Equal(501, await RefusalOf("POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"));

    [Fact]
    public async Task Oversized_headers_431() =>
        Assert.Equal(431, await RefusalOf("GET / HTTP/1.1\r\nHost: h\r\nX: " + new string('a', 200) + "\r\n\r\n",
            new HttpLimits(MaxHeaderBytes: 100)));

    [Fact]
    public async Task Oversized_body_413() =>
        Assert.Equal(413, await RefusalOf("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 5000\r\n\r\n"));

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET nopath HTTP/1.1\r\nHost: h\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost h\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: h\r\n folded\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\nab")]
    public async Task Malformed_request_is_400(string raw) => Assert.Equal(400, await RefusalOf(raw));

    [Fact]
    public async Task Http_1_0_is_505() =>
        Assert.Equal(505, await RefusalOf("GET / HTTP/1.0\r\n\r\n"));

    [Fact]
    public async Task Clean_close_before_a_request_is_null() =>
        Assert.Null(await Reader("").ReadAsync(CancellationToken.None));

    [Fact]
    public async Task Close_in_the_middle_of_a_request_is_400() =>
        Assert.Equal(400, await RefusalOf("GET / HTTP/1.1\r\nHost:"));

    [Fact]
    public async Task Two_requests_on_one_keep_alive_connection()
    {
        var reader = Reader("POST /a HTTP/1.1\r\nHost: h\r\nContent-Length: 2\r\n\r\nhiGET /b HTTP/1.1\r\nHost: h\r\n\r\n");
        var first = await reader.ReadAsync(CancellationToken.None);
        var second = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal("/a", first!.Path);
        Assert.Equal("hi", Encoding.ASCII.GetString(first.Body));
        Assert.Equal("/b", second!.Path);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Slow_client_times_out()
    {
        // A STREAM THAT NEVER ANSWERS: the reader must give up on its own clock, not wait for the caller's.
        var never = new BlockingStream();
        var reader = new HttpRequestReader(never, IPAddress.Loopback, new HttpLimits { ReadTimeout = TimeSpan.FromMilliseconds(200) });
        var refused = await Assert.ThrowsAsync<HttpRefused>(() => reader.ReadAsync(CancellationToken.None));
        Assert.Equal(408, refused.Status);
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
}
