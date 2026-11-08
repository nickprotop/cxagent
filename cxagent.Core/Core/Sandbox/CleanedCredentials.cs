using System.Xml;
using System.Xml.Linq;

namespace CxAgent.Core.Sandbox;

/// <summary>
/// Credential files a tool cannot work without, shown inside the sandbox as a copy with every credential taken out.
///
/// <para>NUGET'S USER CONFIG FIRST: hidden whole, every <c>dotnet restore</c> and build in the sandbox failed — NuGet
/// will not run without the file it was told is there — and what it holds is mostly where packages come from. Its
/// secrets are a known few sections and keys; everything else is what a build needs (user, 2026-11-08).</para>
/// </summary>
public static class CleanedCredentials
{
    /// <summary>The sections that hold nothing but secrets: feed logins, publish keys, certificate passwords.</summary>
    private static readonly string[] SecretSections = ["packageSourceCredentials", "apikeys", "clientCertificates"];

    /// <summary>
    /// A copy of the NuGet config at <paramref name="source"/> with every credential removed, written under
    /// <paramref name="intoDir"/>; answers its path, or null when the file cannot be read as a NuGet config — then it
    /// stays hidden, as anything that cannot be cleaned must.
    /// </summary>
    public static string? NuGetConfig(string source, string intoDir)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(source);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (doc.Root is not { Name.LocalName: "configuration" } root) return null;

        foreach (var section in root.Elements().Where(e => SecretSections.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)).ToList())
            section.Remove();
        // A PROXY'S LOGIN sits among ordinary settings, so it goes key by key: the proxy's address stays, its user and
        // password do not.
        foreach (var add in root.Elements("config").Elements("add")
                     .Where(a => (string?)a.Attribute("key") is { } key
                                 && (key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".user", StringComparison.OrdinalIgnoreCase)))
                     .ToList())
            add.Remove();

        try
        {
            Directory.CreateDirectory(intoDir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(intoDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(intoDir, "NuGet.Config");
            var text = doc.ToString();
            // REWRITTEN ONLY WHEN IT CHANGED: one is built per command, and a file being replaced while a running build
            // reads the bind is a race for nothing.
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
