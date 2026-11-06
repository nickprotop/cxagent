using System.Net;
using System.Net.NetworkInformation;

namespace CxAgent.Web.Http;

/// <summary>
/// Which names the server answers to when nobody has to log in.
///
/// <para>DNS REBINDING. A hostile page can make its own domain resolve to 127.0.0.1 and then reach this server as its
/// own origin: Origin and Host agree, and with no login nothing else stands in the way. A Host that can only mean this
/// machine — loopback, the address it is bound to, its own name — is the one thing that page cannot send. With a login
/// required, any Host is served: the attacker's page has no cookie.</para>
/// </summary>
public static class HostCheck
{
    /// <summary>The names a browser may use to reach a server bound to <paramref name="bind"/>, plus <paramref name="extra"/>.</summary>
    public static IReadOnlySet<string> Known(IPAddress bind, IReadOnlyList<string>? extra)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", Environment.MachineName };
        if (bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any))
        {
            // EVERY ADDRESS THIS MACHINE HAS, since a wildcard bind answers on all of them.
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        names.Add(a.Address.ToString());
            }
            catch (NetworkInformationException) { }
        }
        else
        {
            names.Add(bind.ToString());
        }
        foreach (var name in extra ?? []) names.Add(name);
        return names;
    }

    /// <summary>Whether a <c>Host</c> header names one of <paramref name="known"/>, ignoring its port.</summary>
    public static bool IsKnown(string? host, IReadOnlySet<string> known)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string name;
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0) return false;
            name = host[1..close];
        }
        else
        {
            var colon = host.LastIndexOf(':');
            name = colon < 0 ? host : host[..colon];
        }
        return known.Contains(name);
    }
}
