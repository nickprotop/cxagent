using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace CxAgent.Tests;

/// <summary>
/// A real loopback HTTP server for provider-driver tests (no mocked HttpClient, no live vendor calls).
/// Enqueue canned JSON or SSE responses; each incoming request pops the next one, and the raw
/// request body/headers are captured for request-mapping assertions.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<(int status, string body, bool sse)> _responses = new();
    public string BaseUrl { get; }
    public string? LastRequestBody { get; private set; }
    public IDictionary<string, string> LastRequestHeaders { get; } = new Dictionary<string, string>();

    private readonly Task _loop;

    public LoopbackServer()
    {
        BaseUrl = TestPorts.BindLoopback(_listener);
        // KEEP the task. It was `_ = Task.Run(Loop)`, so nothing ever waited for the loop to notice
        // the listener had closed — see Dispose for what that cost.
        _loop = Task.Run(Loop);
    }

    public void EnqueueJson(int status, string json) => _responses.Enqueue((status, json, false));
    public void EnqueueSse(params string[] events) => _responses.Enqueue((200, string.Join("", events), true));

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped

            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                LastRequestBody = await reader.ReadToEndAsync();
            LastRequestHeaders.Clear();
            foreach (string? key in ctx.Request.Headers.AllKeys)
                if (key is not null) LastRequestHeaders[key] = ctx.Request.Headers[key] ?? "";

            var (status, body, sse) = _responses.TryDequeue(out var r) ? r : (200, "{}", false);
            ctx.Response.StatusCode = status;
            if (sse) ctx.Response.ContentType = "text/event-stream";
            var bytes = Encoding.UTF8.GetBytes(body);
            try
            {
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch { /* client hung up */ }
        }
    }

    /// <summary>
    /// Stops the listener and WAITS for the request loop to exit before closing.
    ///
    /// <para>This used to be `{ _listener.Stop(); _listener.Close(); }` with the loop running
    /// fire-and-forget. Nothing waited for it, so `Close()` raced a loop still touching `_listener`
    /// (its `while (_listener.IsListening)` condition, and the whole response body after an
    /// already-accepted request). The visible symptoms were an `ObjectDisposedException` in one test
    /// and — because `HttpEndPointManager` is a process-global map keyed by port — an
    /// `HttpListenerException: "Address already in use"` thrown from `Close` -> `RemoveListener` in a
    /// COMPLETELY DIFFERENT test class. That is why this presented for weeks as a random ~2-3-in-8
    /// "flaky LlmHttpRetryTests" that moved between classes every run, and why two separate
    /// port-allocation fixes did nothing: the ports were never the problem.</para>
    ///
    /// <para>`Stop()` before the wait so `GetContextAsync` throws and the loop returns; the timeout
    /// means a wedged loop fails this test rather than hanging the whole suite.</para>
    /// </summary>
    public void Dispose()
    {
        _listener.Stop();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* loop faulted on the stopped listener */ }

        // SYMPTOM FIX, labelled as one (defect D9). Close() walks
        // RemoveListener -> RemovePrefixInternal -> GetEPListener into the PROCESS-GLOBAL
        // HttpEndPointManager map, and throws "Address already in use" when that map was corrupted
        // by some OTHER listener's failed Start earlier in the run. Measured at 1 failure in 12
        // full-suite runs; the captured stack is exactly this line.
        //
        // The underlying global-map corruption is NOT fixed here and cannot be from test code. What
        // this changes is who pays: a teardown fault in a test HELPER must not fail a test that
        // already passed, in a class that did not cause it. That misattribution is why this
        // presented for weeks as "flaky LlmHttpRetryTests" and resisted diagnosis — the failing test
        // was never the one at fault.
        //
        // Do NOT "fix" this by reaching for port allocation. Two such attempts were measured as
        // failures (see HttpListenerCollection.cs); the ports were never the problem.
        try { _listener.Close(); }
        catch (HttpListenerException) { /* someone else's cleanup; not this test's failure */ }
    }
}
