using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// NuGet's own config inside the sandbox: hidden whole, every restore failed (a live session's
/// "Access to the path '…/NuGet.Config' is denied"); shown as a cleaned copy, builds work and every credential stays
/// out of reach (user, 2026-11-08).
/// </summary>
public sealed class CleanedCredentialsTests : IDisposable
{
    private readonly string _base = PathResolver.RealFolder(Directory.CreateTempSubdirectory("cleaned-").FullName);
    private string Home => Path.Combine(_base, "home");
    private string Config => Path.Combine(Home, ".nuget", "NuGet", "NuGet.Config");
    private string Cleaned => Path.Combine(_base, "cleaned");

    public CleanedCredentialsTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        Directory.CreateDirectory(Path.Combine(_base, "work"));
    }

    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch (IOException) { } }

    private const string WithSecrets = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
            <add key="private" value="https://feed.example.com/v3/index.json" />
          </packageSources>
          <packageSourceCredentials>
            <private>
              <add key="Username" value="me" />
              <add key="ClearTextPassword" value="hunter2-secret" />
            </private>
          </packageSourceCredentials>
          <apikeys>
            <add key="https://api.nuget.org/v3/index.json" value="oy2-secret-api-key" />
          </apikeys>
          <config>
            <add key="http_proxy" value="http://proxy:8080" />
            <add key="http_proxy.user" value="me" />
            <add key="http_proxy.password" value="proxy-secret" />
          </config>
        </configuration>
        """;

    [Fact]
    public void The_cleaned_copy_keeps_sources_and_drops_every_credential()
    {
        File.WriteAllText(Config, WithSecrets);

        var path = CleanedCredentials.NuGetConfig(Config, Cleaned);

        var text = File.ReadAllText(path!);
        Assert.Contains("https://api.nuget.org/v3/index.json", text);
        Assert.Contains("https://feed.example.com/v3/index.json", text);
        Assert.Contains("http://proxy:8080", text);
        foreach (var secret in new[] { "hunter2-secret", "oy2-secret-api-key", "proxy-secret", "packageSourceCredentials", "apikeys", "http_proxy.user" })
            Assert.DoesNotContain(secret, text);
    }

    /// <summary>
    /// A token carried in a feed's own address goes too: a login in the URL is taken out of it, and a feed whose query
    /// carries a token, key, signature or password is left out — it fails to restore in the sandbox, as a feed with a
    /// credential section does (user, 2026-11-08).
    /// </summary>
    [Fact]
    public void A_token_in_a_feed_s_address_is_not_copied()
    {
        File.WriteAllText(Config, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="login" value="https://me:ghp_secret1@nuget.pkg.github.com/me/index.json" />
                <add key="query" value="https://feed.example.com/v3/index.json?token=qsecret2" />
                <add key="local" value="/home/me/packages" />
              </packageSources>
            </configuration>
            """);

        var text = File.ReadAllText(CleanedCredentials.NuGetConfig(Config, Cleaned)!);

        Assert.Contains("https://api.nuget.org/v3/index.json", text);
        Assert.Contains("https://nuget.pkg.github.com/me/index.json", text);
        Assert.Contains("/home/me/packages", text);
        Assert.DoesNotContain("ghp_secret1", text);
        Assert.DoesNotContain("qsecret2", text);
        Assert.DoesNotContain("key=\"query\"", text);
    }

    /// <summary>A folder for the copy that is a link to somewhere else is refused: the config then stays hidden.</summary>
    [Fact]
    public void A_linked_folder_for_the_copy_is_refused()
    {
        File.WriteAllText(Config, WithSecrets);
        var elsewhere = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Cleaned, elsewhere);

        Assert.Null(CleanedCredentials.NuGetConfig(Config, Cleaned));
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    /// <summary>The copy lives in the user's own runtime folder when there is one, not a name anyone can predict in /tmp.</summary>
    [Fact]
    public void The_copy_goes_to_the_user_s_runtime_folder()
    {
        Assert.Equal(Path.Combine("/run/user/1000", "cxagent-cleaned"), SandboxLayoutRequest.CleanedDirFor("/run/user/1000"));
        Assert.StartsWith(Path.GetTempPath(), SandboxLayoutRequest.CleanedDirFor(null));
    }

    [Fact]
    public void A_config_that_cannot_be_read_is_not_cleaned()
    {
        File.WriteAllText(Config, "<configuration><packageSources>");

        Assert.Null(CleanedCredentials.NuGetConfig(Config, Cleaned));
    }

    [Fact]
    public void The_sandbox_shows_the_cleaned_copy_over_the_real_one()
    {
        File.WriteAllText(Config, WithSecrets);
        var work = Path.Combine(_base, "work");

        var layout = SandboxLayout.For(new SandboxLayoutRequest(work, work, false, null, new SandboxHome(Home, null, null), SandboxSettings.AppDefault)
        {
            CleanedDir = Cleaned,
        });
        var args = SandboxProfile.Arguments(layout, new SandboxRun(new Dictionary<string, string>()));

        Assert.DoesNotContain(Config, layout.HiddenFiles);
        var replaced = Assert.Single(layout.ReplacedFiles);
        Assert.Equal(Config, replaced.Target);
        var at = args.ToList().IndexOf(replaced.Source);
        Assert.True(at > 0 && args[at - 1] == "--ro-bind" && args[at + 1] == Config);
    }

    [Fact]
    public void An_unreadable_config_stays_hidden()
    {
        File.WriteAllText(Config, "not xml at all");
        var work = Path.Combine(_base, "work");

        var layout = SandboxLayout.For(new SandboxLayoutRequest(work, work, false, null, new SandboxHome(Home, null, null), SandboxSettings.AppDefault)
        {
            CleanedDir = Cleaned,
        });

        Assert.Contains(Config, layout.HiddenFiles);
        Assert.Empty(layout.ReplacedFiles);
    }
}
