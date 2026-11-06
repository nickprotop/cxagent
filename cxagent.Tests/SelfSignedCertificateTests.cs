using System.Net;
using System.Text;
using CxAgent.Core.Storage;
using CxAgent.Web.Http;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The self-signed certificate <c>--self-signed</c> serves: made once, kept private, and actually used.</summary>
public sealed class SelfSignedCertificateTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("cert-cfg-").FullName;
    private AppPaths Paths => new(_cfg);

    public void Dispose()
    {
        try { Directory.Delete(_cfg, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void Cert_is_created_once_and_reused()
    {
        using var first = SelfSignedCertificate.LoadOrCreate(Paths, ["localhost"], [IPAddress.Loopback]);
        using var again = SelfSignedCertificate.LoadOrCreate(Paths, ["localhost"], [IPAddress.Loopback]);
        Assert.Equal(first.Thumbprint, again.Thumbprint);
        Assert.True(first.HasPrivateKey);
    }

    [Fact]
    public void A_new_address_makes_a_new_cert()
    {
        using var first = SelfSignedCertificate.LoadOrCreate(Paths, ["localhost"], [IPAddress.Loopback]);
        using var wider = SelfSignedCertificate.LoadOrCreate(Paths, ["localhost"], [IPAddress.Loopback, IPAddress.Parse("10.1.2.3")]);
        Assert.NotEqual(first.Thumbprint, wider.Thumbprint);
    }

    [Fact]
    public void Cert_file_is_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        using var _ = SelfSignedCertificate.LoadOrCreate(Paths, ["localhost"], [IPAddress.Loopback]);
        var mode = File.GetUnixFileMode(Path.Combine(_cfg, "web-cert.pfx"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task Https_server_round_trip()
    {
        using var cert = SelfSignedCertificate.LoadOrCreate(Paths, ["localhost"], [IPAddress.Loopback]);
        await using var server = new HttpServer(new HttpServerOptions(IPAddress.Loopback, 0, cert),
            x => x.RespondAsync(200, Encoding.UTF8.GetBytes("secure")));
        await server.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == cert.Thumbprint,
        };
        using var http = new HttpClient(handler);
        Assert.Equal("secure", await http.GetStringAsync($"https://localhost:{server.Bound.Port}/"));
    }
}
