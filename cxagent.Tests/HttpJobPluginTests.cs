using System.Net;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class HttpJobPluginTests : IDisposable
{
    // NOT READONLY: TestPorts may have to REPLACE this listener. A failed Start elsewhere leaves a
    // registration in the process-global map, and another class unwinding it disposes this one
    // mid-bind — a disposed HttpListener cannot be revived. See TestPorts.BindLoopback.
    private HttpListener _listener = new();
    private readonly string _prefix;

    public HttpJobPluginTests()
    {
        // Bind to a free loopback port for a real, network-free HTTP test. Uses the shared retrying
        // binder — picking a random port with no retry (as this did) collided with the LoopbackServer
        // instances other test classes start in parallel, which is what made this class intermittently
        // fail. See TestPorts.
        _prefix = TestPorts.BindLoopback(ref _listener);
    }
    // The catch is defect D9's symptom fix — see the long note in LoopbackServer.Dispose. Close()
    // reaches the process-global HttpEndPointManager and can throw over another listener's failed
    // Start; a teardown fault in a helper must not fail a test that already passed.
    public void Dispose()
    {
        _listener.Stop();
        try { _listener.Close(); }
        // BOTH TYPES, because a listener whose Start failed can be disposed by another class
        // unwinding the process-global map — Close then throws ObjectDisposedException rather than
        // HttpListenerException. A teardown fault in a helper must not fail a test that passed.
        catch (HttpListenerException) { /* someone else's cleanup; not this test's failure */ }
        catch (ObjectDisposedException) { /* already torn down by that unwind */ }
    }

    private Task ServeOnce(int status, string body) => Task.Run(() =>
    {
        var ctxHttp = _listener.GetContext();
        ctxHttp.Response.StatusCode = status;
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctxHttp.Response.OutputStream.Write(bytes);
        ctxHttp.Response.Close();
    });

    /// <summary>Serves one response with a declared content type — conversion keys on it.</summary>
    /// <summary>
    /// Serves one request, and does not return until it is READY to serve it.
    ///
    /// <para>THE SECOND RACE IN THIS FILE, and the one that survived the port-binding fix. Task.Run
    /// schedules the handler; it does not run it. The caller then issues its request immediately,
    /// and on a loaded machine the client can reach the listener before that thread has called
    /// GetContext at all — the request is accepted by the OS backlog but nothing is waiting to read
    /// it, so the plugin's timeout fires and the assertion sees an empty body.
    ///
    /// <para>Whether it worked was thread scheduling: rare enough to look like a port collision,
    /// which is why fixing the binder appeared to fix this too and did not. Measured at two failures
    /// in ten full-suite runs AFTER that fix.</para>
    ///
    /// <para>The gate is signalled from inside the handler, immediately before the blocking
    /// GetContext call, so awaiting it means the thread is scheduled and about to listen.</para>
    /// </summary>
    private Task ServeOnceAs(string contentType, string body)
    {
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serve = Task.Run(() =>
        {
            listening.SetResult();
            var ctxHttp = _listener.GetContext();
            ctxHttp.Response.StatusCode = 200;
            ctxHttp.Response.ContentType = contentType;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            ctxHttp.Response.OutputStream.Write(bytes);
            ctxHttp.Response.Close();
        });

        listening.Task.Wait();
        return serve;
    }

    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));

    /// <summary>
    /// as_text is what makes web_fetch worth having: raw HTML is nearly all markup, and a tool
    /// result is re-sent on every later turn — ten raw page fetches measured at 200k of context.
    /// </summary>
    [Fact]
    public async Task Execute_WithAsText_ConvertsAnHtmlResponseToReadableText()
    {
        var html = "<html><head><style>.a{color:red}</style></head><body>"
                 + "<nav><a href=/x>Home</a></nav>"
                 + "<script>window.track('noise');</script>"
                 + "<h1>The Heading</h1><p>The paragraph.</p></body></html>";

        var serve = ServeOnceAs("text/html; charset=utf-8", html);
        var r = await new HttpJobPlugin().ExecuteAsync(
            P(("url", _prefix), ("as_text", true)), new CollectingContext(), CancellationToken.None);
        await serve;

        var text = (string)r.Output["body"]!;
        Assert.True(r.Success);
        Assert.Contains("# The Heading", text, StringComparison.Ordinal);
        Assert.Contains("The paragraph.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("window.track", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Home", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONTENT TYPE DECIDES, not the flag alone. A server that answers with JSON gets passed
    /// through untouched even when as_text was set — the caller wanted the resource, and running a
    /// JSON body through an HTML converter would corrupt exactly what they asked for.
    /// </summary>
    [Fact]
    public async Task Execute_WithAsText_LeavesNonHtmlUntouched()
    {
        const string json = """{"items":[{"id":1,"name":"<b>not markup</b>"}]}""";

        var serve = ServeOnceAs("application/json", json);
        var r = await new HttpJobPlugin().ExecuteAsync(
            P(("url", _prefix), ("as_text", true)), new CollectingContext(), CancellationToken.None);
        await serve;

        Assert.Equal(json, (string)r.Output["body"]!);
    }

    /// <summary>Without the flag, http_request hands back exactly what the server sent.</summary>
    [Fact]
    public async Task Execute_WithoutAsText_ReturnsRawHtml()
    {
        const string html = "<body><p>Raw.</p></body>";

        var serve = ServeOnceAs("text/html", html);
        var r = await new HttpJobPlugin().ExecuteAsync(
            P(("url", _prefix)), new CollectingContext(), CancellationToken.None);
        await serve;

        Assert.Equal(html, (string)r.Output["body"]!);
    }

    [Fact]
    public async Task Execute_200_SucceedsWithBody()
    {
        var serve = ServeOnce(200, "pong");
        var r = await new HttpJobPlugin().ExecuteAsync(
            P(("url", _prefix)), new CollectingContext(), CancellationToken.None);
        await serve;
        Assert.True(r.Success);
        Assert.Equal(200, Convert.ToInt32(r.Output["status"]));
        Assert.Contains("pong", (string)r.Output["body"]!);
    }

    [Fact]
    public async Task Execute_StatusMismatch_ExhaustsRetries_Fails()
    {
        // Server always returns 500; expect 200 with 1 retry (2 attempts) at 0s interval.
        var t1 = ServeOnce(500, "err"); var t2 = ServeOnce(500, "err");
        var r = await new HttpJobPlugin().ExecuteAsync(
            P(("url", _prefix), ("expect_status", 200), ("max_retries", 1), ("retry_interval_seconds", 0)),
            new CollectingContext(), CancellationToken.None);
        await Task.WhenAll(t1, t2);
        Assert.False(r.Success);
    }

    [Fact]
    public void Validate_RejectsBadUrl()
    {
        var v = new HttpJobPlugin().Validate(P(("url", "not a url")));
        Assert.False(v.IsValid);
    }
}
