using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>confirmQuit</c>: whether Ctrl+Q asks "Quit cxagent?" when nothing is running. Absent means ask, and
/// only an explicit false — what ticking "Don't ask again" writes — stops it.
/// </summary>
public class QuitPreferenceTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("cxagent-quit-pref").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Config => Path.Combine(_dir, "config.json");

    [Fact]
    public void WithNoConfigYet_ItAsks() => Assert.True(QuitPreference.Asks(Config));

    [Fact]
    public void WithTheKeyAbsent_ItAsks()
    {
        File.WriteAllText(Config, """{ "theme": "cxagent" }""");
        Assert.True(QuitPreference.Asks(Config));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void TheKeyIsRead(string written, bool asks)
    {
        File.WriteAllText(Config, $$"""{ "confirmQuit": {{written}} }""");
        Assert.Equal(asks, QuitPreference.Asks(Config));
    }

    /// <summary>
    /// ANYTHING BUT A PLAIN FALSE ASKS. A quit that skips its question because of a typo is the one
    /// misreading that costs anything; asking once too often costs a keystroke.
    /// </summary>
    [Theory]
    [InlineData("""{ "confirmQuit": "no" }""")]
    [InlineData("""{ "confirmQuit": 0 }""")]
    [InlineData("""{ "confirmQuit": null }""")]
    [InlineData("""{ "confirmQuit": false""")]
    [InlineData("""[ false ]""")]
    public void AnythingElse_Asks(string config)
    {
        File.WriteAllText(Config, config);
        Assert.True(QuitPreference.Asks(Config));
    }

    [Fact]
    public void StopAsking_IsReadBack()
    {
        QuitPreference.StopAsking(Config);
        Assert.False(QuitPreference.Asks(Config));
    }

    /// <summary>EVERYTHING ELSE SURVIVES: config.json is hand-edited and holds API keys.</summary>
    [Fact]
    public void StopAsking_KeepsTheRestOfTheFile()
    {
        File.WriteAllText(Config, """{ "providers": { "local": { "apiKey": "secret-value" } } }""");

        QuitPreference.StopAsking(Config);

        Assert.Contains("secret-value", File.ReadAllText(Config));
        Assert.False(QuitPreference.Asks(Config));
    }
}
