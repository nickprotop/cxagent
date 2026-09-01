using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A theme chosen with the picker survives a restart, because <c>theme</c> is read at startup and
/// is now written back.
/// </summary>
public class ThemePersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "theme-" + Guid.NewGuid().ToString("N"));

    public ThemePersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string Config => Path.Combine(_dir, "config.json");

    [Fact]
    public void TheChosenTheme_IsWritten()
    {
        PluginConfigWriter.SetTheme(Config, "cxagent");

        Assert.Contains("\"theme\"", File.ReadAllText(Config));
        Assert.Contains("cxagent", File.ReadAllText(Config));
    }

    [Fact]
    public void ChoosingAgain_ReplacesRatherThanAppends()
    {
        PluginConfigWriter.SetTheme(Config, "first");
        PluginConfigWriter.SetTheme(Config, "second");

        var text = File.ReadAllText(Config);
        Assert.Contains("second", text);
        Assert.DoesNotContain("first", text);
    }

    /// <summary>
    /// EVERYTHING ELSE SURVIVES. config.json is hand-edited and holds API keys; a save that
    /// serialised only what this writer models would delete a provider block on a theme change.
    /// </summary>
    [Fact]
    public void ItKeepsTheRestOfTheFile()
    {
        File.WriteAllText(Config, """
        {
          "providers": { "local": { "apiKey": "secret-value" } },
          "plugins": { "calculator": { "file": "calculator.dll" } }
        }
        """);

        PluginConfigWriter.SetTheme(Config, "cxagent");

        var text = File.ReadAllText(Config);
        Assert.Contains("secret-value", text);
        Assert.Contains("calculator.dll", text);
        Assert.Contains("cxagent", text);
    }

    [Fact]
    public void ItWorksWhenThereIsNoConfigYet()
    {
        // A first run picks a theme before anything has written the file.
        PluginConfigWriter.SetTheme(Config, "cxagent");

        Assert.True(File.Exists(Config));
        Assert.Contains("cxagent", File.ReadAllText(Config));
    }
}
