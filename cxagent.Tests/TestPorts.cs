using System.Net;
using System.Net.Sockets;

namespace CxAgent.Tests;

/// <summary>
/// Binds an <see cref="HttpListener"/> to a free loopback port, retrying on collision.
///
/// Why this exists: <see cref="LoopbackServer"/> and <c>HttpJobPluginTests</c> each used to pick a
/// port as <c>20000 + Random.Shared.Next(1, 9000)</c> with NO retry, from the SAME range. Five test
/// classes spin listeners up in parallel, so two occasionally drew the same port and one threw
/// <see cref="HttpListenerException"/> ("Address already in use") at construction — surfacing as an
/// intermittent failure in whichever class lost the race. Three consecutive full-suite runs during the
/// P5c work produced one such failure and two clean, in DIFFERENT test methods each time; the
/// long-standing "known flaky LlmHttpRetryTests" was almost certainly the same root cause rather than a
/// separate issue.
///
/// <see cref="HttpListener"/> cannot bind port 0 (it needs a concrete prefix string), so an
/// OS-assigned port isn't available; retrying on a fresh random port is the workable fix. The window
/// is also narrowed by drawing from a wider range.
/// </summary>
internal static class TestPorts
{
    private const int MinPort = 20000;
    private const int MaxPort = 45000;   // wider than the old 9000-port span => fewer collisions
    private const int MaxAttempts = 25;

    /// <summary>
    /// Adds a free <c>http://localhost:{port}/</c> prefix to <paramref name="listener"/>, starts it, and
    /// returns the prefix. Throws only if every attempt collided, which would indicate a genuinely
    /// exhausted range rather than a race.
    /// </summary>
    public static string BindLoopback(HttpListener listener)
    {
        for (int attempt = 1; ; attempt++)
        {
            int port = Random.Shared.Next(MinPort, MaxPort);

            // Probe with a plain socket BEFORE handing the port to HttpListener.
            //
            // Retrying a failed `listener.Start()` is not clean: HttpListener registers into the
            // process-global HttpEndPointManager map (keyed by port) as part of Start, and a Start
            // that throws can leave that registration behind — `Prefixes.Clear()` does not unwind
            // it. The listener then LOOKS fine, and the damage only surfaces later when Dispose
            // calls Close -> RemoveListener -> RemovePrefixInternal -> GetEPListener and throws
            // "Address already in use" — inside whichever test class happened to tear down, which
            // is why this presented as a flake that moved between classes every run.
            //
            // A TcpListener bind is a cheap, side-effect-free way to ask "is this port free?".
            // Losing the race between this check and Start is still possible but far narrower, and
            // the surviving case is handled by the catch below.
            if (!IsFree(port)) continue;

            string prefix = $"http://localhost:{port}/";
            listener.Prefixes.Clear();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return prefix;
            }
            catch (HttpListenerException) when (attempt < MaxAttempts)
            {
                // Lost the race between the probe and Start — try another port.
            }
        }
    }

    private static bool IsFree(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try { probe.Start(); return true; }
        catch (SocketException) { return false; }
        finally { probe.Stop(); }
    }
}
