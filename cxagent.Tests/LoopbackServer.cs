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
    // NOT READONLY: TestPorts may have to REPLACE this listener. A failed Start elsewhere leaves a
    // registration in the process-global map, and another class unwinding it disposes this one
    // mid-bind — a disposed HttpListener cannot be revived. See TestPorts.BindLoopback.
    private HttpListener _listener = new();
    private readonly ConcurrentQueue<(int status, string body, bool sse)> _responses = new();
    public string BaseUrl { get; }
    public string? LastRequestBody { get; private set; }
    public IDictionary<string, string> LastRequestHeaders { get; } = new Dictionary<string, string>();

    private readonly Task _loop;

    public LoopbackServer()
    {
        BaseUrl = TestPorts.BindLoopback(ref _listener);
        // KEEP the task. It was `_ = Task.Run(Loop)`, so nothing ever waited for the loop to notice
        // the listener had closed — see Dispose for what that cost.
        _loop = Task.Run(Loop);
    }

    public void EnqueueJson(int status, string json) => _responses.Enqueue((status, json, false));
    public void EnqueueSse(params string[] events) => _responses.Enqueue((200, string.Join("", events), true));

    // PROBES ARE ANSWERED OFF-QUEUE. A capability probe (Anthropic's GET /models/{id}, llama.cpp's
    // GET /props) precedes the request a test is about, and a test enqueues only the answers it
    // asserts on — if the probe took the next queue slot it would eat the /messages response and
    // every older test would see the wrong body. So a probe neither dequeues nor overwrites
    // LastRequest*, which keep describing the request the test sent.
    private readonly ConcurrentDictionary<string, (int status, string body)> _routes = new();

    /// <summary>
    /// Whether an unrouted GET to <c>/models/{id}</c> or <c>/props</c> is answered 404 off-queue.
    /// A test whose subject IS such a request (the context-window probe) turns this off so the
    /// request takes its enqueued answer and is captured like any other.
    /// </summary>
    public bool AnswerProbesWith404 { get; init; } = true;

    /// <summary>Answers requests whose path ends with <paramref name="path"/> with a fixed response, never dequeued.</summary>
    /// <param name="path">The path suffix to match, e.g. <c>/models/claude-x</c>.</param>
    /// <param name="status">The HTTP status to answer.</param>
    /// <param name="body">The response body.</param>
    public void Route(string path, int status, string body) => _routes[path] = (status, body);

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            (int status, string body, bool sse) answer;
            var routed = _routes.FirstOrDefault(kv => path.EndsWith(kv.Key, StringComparison.Ordinal));
            if (routed.Key is not null)
                answer = (routed.Value.status, routed.Value.body, false);
            else if (AnswerProbesWith404 && ctx.Request.HttpMethod == "GET" && (path.Contains("/models/") || path.EndsWith("/props")))
                answer = (404, "{}", false);
            else
            {
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    LastRequestBody = await reader.ReadToEndAsync();
                LastRequestHeaders.Clear();
                foreach (string? key in ctx.Request.Headers.AllKeys)
                    if (key is not null) LastRequestHeaders[key] = ctx.Request.Headers[key] ?? "";
                answer = _responses.TryDequeue(out var r) ? r : (200, "{}", false);
            }

            var (status, body, sse) = answer;
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
    /// <para>A bare `{ _listener.Stop(); _listener.Close(); }` with the loop running fire-and-forget
    /// does not work: nothing waits for it, so `Close()` races a loop still touching `_listener` (its
    /// `while (_listener.IsListening)` condition, and the whole response body after an already-accepted
    /// request). The visible symptoms are an `ObjectDisposedException` in one test and — because
    /// `HttpEndPointManager` is a process-global map keyed by port — an `HttpListenerException:
    /// "Address already in use"` thrown from `Close` -> `RemoveListener` in a COMPLETELY DIFFERENT
    /// test class. That is how this presents: a random ~2-3-in-8 "flaky LlmHttpRetryTests" that moves
    /// between classes every run, immune to port-allocation fixes because the ports are not the
    /// problem.</para>
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
        // BOTH TYPES, because a listener whose Start failed can be disposed by another class
        // unwinding the process-global map — Close then throws ObjectDisposedException rather than
        // HttpListenerException. A teardown fault in a helper must not fail a test that passed.
        catch (HttpListenerException) { /* someone else's cleanup; not this test's failure */ }
        catch (ObjectDisposedException) { /* already torn down by that unwind */ }
    }
}
