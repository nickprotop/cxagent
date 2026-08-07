using System.Net;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class HttpJobPluginTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;

    public HttpJobPluginTests()
    {
        // Bind to a free loopback port for a real, network-free HTTP test. Uses the shared retrying
        // binder — picking a random port with no retry (as this did) collided with the LoopbackServer
        // instances other test classes start in parallel, which is what made this class intermittently
        // fail. See TestPorts.
        _prefix = TestPorts.BindLoopback(_listener);
    }
    // The catch is defect D9's symptom fix — see the long note in LoopbackServer.Dispose. Close()
    // reaches the process-global HttpEndPointManager and can throw over another listener's failed
    // Start; a teardown fault in a helper must not fail a test that already passed.
    public void Dispose()
    {
        _listener.Stop();
        try { _listener.Close(); }
        catch (HttpListenerException) { /* someone else's cleanup; not this test's failure */ }
    }

    private Task ServeOnce(int status, string body) => Task.Run(() =>
    {
        var ctxHttp = _listener.GetContext();
        ctxHttp.Response.StatusCode = status;
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctxHttp.Response.OutputStream.Write(bytes);
        ctxHttp.Response.Close();
    });

    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));

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
