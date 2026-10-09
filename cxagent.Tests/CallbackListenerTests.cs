using System.Net;
using System.Net.Sockets;
using CxAgent.Core.Mcp.Auth;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The loopback listener `/mcp login` opens for the browser to return to.
/// </summary>
[Collection("http-listeners")]
public class CallbackListenerTests
{
    /// <summary>A port held by something else, for as long as the returned listener lives.</summary>
    private static TcpListener Occupy(out int port)
    {
        var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        port = ((IPEndPoint)held.LocalEndpoint).Port;
        return held;
    }

    private static int Free()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A PICKED PORT TAKEN BEFORE THE BIND IS NOT A FAILED LOGIN. The free port is learned by binding
    /// it and letting go, and anything may take it in between — the listener moves to a fresh one.
    /// </summary>
    [Fact]
    public void APickedPortTakenBeforeTheBind_IsRetriedOnAFreshOne()
    {
        var held = Occupy(out var taken);
        try
        {
            var picks = new Queue<int>([taken, Free()]);
            using var listener = new CallbackListener(0, () => picks.Dequeue());

            Assert.DoesNotContain($":{taken}/", listener.RedirectUri, StringComparison.Ordinal);
            Assert.Empty(picks);   // the second pick was the one used
        }
        finally { held.Stop(); }
    }

    /// <summary>A port the caller named is theirs: a taken one fails rather than quietly moving.</summary>
    [Fact]
    public void ANamedPortThatIsTaken_StillFails()
    {
        var held = Occupy(out var taken);
        try
        {
            Assert.ThrowsAny<Exception>(() => new CallbackListener(taken, () => Free()));
        }
        finally { held.Stop(); }
    }

    /// <summary>Retrying is bounded: a machine that takes every port offered gets an error, not a hang.</summary>
    [Fact]
    public void EveryPickTaken_GivesUpWithAnError()
    {
        var held = Occupy(out var taken);
        try
        {
            Assert.ThrowsAny<Exception>(() => new CallbackListener(0, () => taken));
        }
        finally { held.Stop(); }
    }
}
