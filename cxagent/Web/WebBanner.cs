using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CxAgent.Core.Web;

namespace CxAgent.Web;

/// <summary>What <c>cxagent web</c>'s startup screen says.</summary>
/// <param name="Version">cxagent's version.</param>
/// <param name="Url">The address it serves at.</param>
/// <param name="Network">Other addresses it can be reached at — the machine's own, when it listens on every interface.</param>
/// <param name="Settings">The web settings in effect.</param>
/// <param name="Folders">The folders a browser may open sessions under.</param>
/// <param name="Model">The model new sessions start on.</param>
/// <param name="Warnings">What it said about how it serves.</param>
public sealed record WebBannerFacts(string Version, Uri Url, IReadOnlyList<Uri> Network, WebSettings Settings,
    IReadOnlyList<string> Folders, string? Model, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>cxagent web</c>'s startup screen: where to point a browser, who may come in, what they reach, and how to stop it —
/// the facts someone starting a server needs before they walk away from it.
///
/// <para>LINES FIRST, COLOUR SECOND: <see cref="Lines"/> is the text, the same in a log file; <see cref="Write"/> adds
/// colour only when it is going to a terminal.</para>
/// </summary>
public static class WebBanner
{
    /// <summary>The screen, as lines of plain text.</summary>
    public static IReadOnlyList<(string Text, BannerTone Tone)> Lines(WebBannerFacts f, string stopHint)
    {
        var s = f.Settings;
        var lines = new List<(string, BannerTone)>
        {
            ($"cxagent web {f.Version}", BannerTone.Title),
            ("", BannerTone.Plain),
            ($"  ➜  Local    {f.Url}", BannerTone.Address),
        };
        foreach (var url in f.Network) lines.Add(($"  ➜  Network  {url}", BannerTone.Address));
        lines.Add(("", BannerTone.Plain));

        var login = s.Login switch
        {
            LoginMode.Off => "off — anyone who reaches the port gets in",
            LoginMode.Always => "required",
            _ => f.Network.Count > 0 || !IPAddress.TryParse(s.Bind, out var b) || !IPAddress.IsLoopback(b) ? "required" : "not needed on this machine",
        };
        var users = s.Users.Count == 0 ? "no users" : "users: " + string.Join(", ", s.Users.Keys.Order(StringComparer.Ordinal));
        lines.Add(($"  login     {login} · {users}", s.Login == LoginMode.Off ? BannerTone.Warning : BannerTone.Plain));
        lines.Add(($"  folders   {string.Join(", ", f.Folders.Select(Home))}", BannerTone.Plain));
        if (f.Model is { } model) lines.Add(($"  model     {model}", BannerTone.Plain));
        var a = s.Allow;
        lines.Add(($"  browser   plugins {OnOff(a.Plugins)} · open anywhere {OnOff(a.OpenAnywhere)} · shell {OnOff(a.Shell)} · config {OnOff(a.Config)}", BannerTone.Plain));
        lines.Add(("", BannerTone.Plain));
        foreach (var w in f.Warnings) lines.Add(($"  ! {w}", BannerTone.Warning));
        if (f.Warnings.Count > 0) lines.Add(("", BannerTone.Plain));
        lines.Add(($"  {stopHint}", BannerTone.Muted));
        return lines;
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Home(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    /// <summary>Writes the screen: coloured on a terminal, plain anywhere else.</summary>
    public static void Write(IReadOnlyList<(string Text, BannerTone Tone)> lines)
    {
        var colour = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        foreach (var (text, tone) in lines)
        {
            if (!colour || tone == BannerTone.Plain) { Console.WriteLine(text); continue; }
            var code = tone switch
            {
                BannerTone.Title => "\u001b[1;38;5;209m",
                BannerTone.Address => "\u001b[1;36m",
                BannerTone.Warning => "\u001b[33m",
                _ => "\u001b[2m",
            };
            Console.WriteLine($"{code}{text}\u001b[0m");
        }
    }

    /// <summary>
    /// The machine's own addresses at <paramref name="port"/>, when it listens on every interface — what a phone on the
    /// same network types. Up interfaces only, loopback and link-local left out.
    /// </summary>
    public static IReadOnlyList<Uri> NetworkAddresses(string bind, string scheme, int port)
    {
        if (bind is not ("0.0.0.0" or "::" or "[::]")) return [];
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && !IsBridge(n.Name))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .Select(ip => new Uri($"{scheme}://{ip}:{port}/"))];
        }
        catch (NetworkInformationException) { return []; }
    }

    // CONTAINERS' AND VIRTUAL MACHINES' BRIDGES: addresses of this machine nobody on the network can reach it at.
    private static bool IsBridge(string name) =>
        name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("br-", StringComparison.Ordinal)
        || name.StartsWith("veth", StringComparison.Ordinal) || name.StartsWith("virbr", StringComparison.Ordinal)
        || name.StartsWith("vEthernet", StringComparison.Ordinal);
}

/// <summary>How a line of the startup screen is coloured.</summary>
public enum BannerTone
{
    /// <summary>As it is.</summary>
    Plain,

    /// <summary>The name and version.</summary>
    Title,

    /// <summary>An address to open.</summary>
    Address,

    /// <summary>Something to act on.</summary>
    Warning,

    /// <summary>A hint.</summary>
    Muted,
}
