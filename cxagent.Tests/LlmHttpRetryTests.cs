using System.Net;
using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class LlmHttpRetryTests : IDisposable
{
    // NOT READONLY: TestPorts may have to REPLACE this listener. A failed Start elsewhere leaves a
    // registration in the process-global map, and another class unwinding it disposes this one
    // mid-bind — a disposed HttpListener cannot be revived. See TestPorts.BindLoopback.
    private HttpListener _listener = new();
    private readonly string _prefix;
    private int _hits;

    public LlmHttpRetryTests()
    {
        // Was `20000 + Random.Shared.Next(1, 9000)` with NO retry — the exact pattern TestPorts was
        // created to replace, and the only call site that never got migrated. It drew from the same
        // narrow range as every other listener, so a collision failed EITHER this class or whichever
        // one lost the race (AnthropicProviderTests, ModelCatalogTests, ...), in a different test
        // method each run. That is the "known flaky LlmHttpRetryTests" this project has worked
        // around for weeks; it was never a Dispose problem, it was this constructor.
        _prefix = TestPorts.BindLoopback(ref _listener);
    }
    /// <summary>
    /// Stops the listener and waits for the serving task before closing — same use-after-dispose
    /// this class's own `Serve` had: `_ = Task.Run(...)` was never awaited, so `Close()` raced a
    /// loop still calling `GetContext()` and writing a response. See LoopbackServer.Dispose for the
    /// full account of why that surfaced as an "Address already in use" in OTHER test classes.
    /// </summary>
    public void Dispose()
    {
        _listener.Stop();
        try { _serving?.Wait(TimeSpan.FromSeconds(5)); } catch { /* faulted on the stopped listener */ }

        // GUARDED, exactly as LoopbackServer.Dispose is — and this class was the one call site that
        // never got the guard. Close() walks RemoveListener -> RemovePrefixInternal -> GetEPListener
        // into the PROCESS-GLOBAL HttpEndPointManager map and throws "Address already in use" when
        // that map was corrupted by some other listener's failed Start earlier in the run.
        //
        // Measured before this: 3 failures in 6 full-suite runs, EVERY one of them this class's
        // Dispose, and never reproducible in isolation — the tests themselves had already passed.
        // A teardown fault in a test helper must not fail a test that already passed.
        try { _listener.Close(); }
        // BOTH TYPES, because a listener whose Start failed can be disposed by another class
        // unwinding the process-global map — Close then throws ObjectDisposedException rather than
        // HttpListenerException. A teardown fault in a helper must not fail a test that passed.
        catch (HttpListenerException) { /* someone else's cleanup; not this test's failure */ }
        catch (ObjectDisposedException) { /* already torn down by that unwind */ }
    }

    private Task? _serving;

    // Serve `count` responses; the i-th uses statuses[i] (last value repeats).
    private void Serve(int count, params int[] statuses) => _serving = Task.Run(() =>
    {
        for (int i = 0; i < count; i++)
        {
            var ctx = _listener.GetContext();
            Interlocked.Increment(ref _hits);
            ctx.Response.StatusCode = statuses[Math.Min(i, statuses.Length - 1)];
            var bytes = System.Text.Encoding.UTF8.GetBytes("{}");
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
    });

    [Fact]
    public async Task Retries_On500_ThenSucceeds()
    {
        Serve(3, 500, 500, 200);
        using var client = new HttpClient();
        var resp = await LlmHttpRetry.SendWithRetryAsync(
            client, () => new HttpRequestMessage(HttpMethod.Get, _prefix),
            "inst", RetryPolicy.NoDelay, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, _hits);
    }

    [Fact]
    public async Task ExhaustsAttempts_Throws_LlmProviderException()
    {
        Serve(4, 503);
        using var client = new HttpClient();
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            LlmHttpRetry.SendWithRetryAsync(
                client, () => new HttpRequestMessage(HttpMethod.Get, _prefix),
                "inst", RetryPolicy.NoDelay, CancellationToken.None));
        Assert.Equal("inst", ex.InstanceName);
        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal(4, _hits);   // MaxAttempts
    }

    [Fact]
    public async Task DoesNotRetry_On400_Throws_Immediately()
    {
        Serve(1, 400);
        using var client = new HttpClient();
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            LlmHttpRetry.SendWithRetryAsync(
                client, () => new HttpRequestMessage(HttpMethod.Get, _prefix),
                "inst", RetryPolicy.NoDelay, CancellationToken.None));
        Assert.Equal(400, ex.HttpStatus);
        Assert.Equal(1, _hits);   // no retry on 4xx (except 429)
    }
}
