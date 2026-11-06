using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CxAgent.Core.Storage;

namespace CxAgent.Web.Http;

/// <summary>
/// The certificate <c>--self-signed</c> serves: made once and kept in the config folder.
///
/// <para>KEPT, NOT MADE PER RUN. A browser's "not trusted" warning is accepted once per certificate; a fresh one each
/// start would mean the warning on every start, on every device, which teaches people to click through it.</para>
///
/// <para>REMADE ONLY WHEN IT NO LONGER FITS — expired, about to, or not naming an address the server now listens on —
/// because then the browser would refuse it anyway.</para>
/// </summary>
public static class SelfSignedCertificate
{
    private const string FileName = "web-cert.pfx";

    /// <summary>The kept certificate when it still covers these names and addresses; otherwise a new one, kept.</summary>
    public static X509Certificate2 LoadOrCreate(AppPaths paths, IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses)
    {
        var file = Path.Combine(paths.ConfigDir, FileName);
        if (File.Exists(file))
        {
            try
            {
                var kept = X509CertificateLoader.LoadPkcs12FromFile(file, password: null);
                if (Fits(kept, names, addresses)) return kept;
                kept.Dispose();
            }
            catch (CryptographicException)
            {
                // UNREADABLE — replaced below, as a missing one would be.
            }
        }

        var made = Create(names, addresses);
        Directory.CreateDirectory(paths.ConfigDir);
        var bytes = made.Export(X509ContentType.Pkcs12);
        made.Dispose();

        // OWNER-ONLY FROM THE START: the file holds the private key, and a mode set after writing leaves a moment
        // when it is readable by anyone.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(file, options)) stream.Write(bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // LOADED BACK FROM THE BYTES: a key made in memory is not usable by the TLS stack on every platform until it
        // has been through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(bytes, password: null);
    }

    private static X509Certificate2 Create(IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=cxagent", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase)) san.AddDnsName(name);
        foreach (var address in addresses.Distinct()) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        // 825 DAYS: the longest validity browsers accept for a TLS server certificate.
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(825));
    }

    private static bool Fits(X509Certificate2 cert, IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses)
    {
        if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(7)) return false;
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return false;
        var dns = san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ips = san.EnumerateIPAddresses().ToHashSet();
        return names.All(dns.Contains) && addresses.All(ips.Contains);
    }
}
