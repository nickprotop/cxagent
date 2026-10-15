using System.Text.Json.Nodes;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>/think --save: one key on one provider, everything else in the file left alone.</summary>
public class ThinkingConfigWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "thinking-config-" + Guid.NewGuid().ToString("N"));

    public ThinkingConfigWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string Config => Path.Combine(_dir, "config.json");

    private JsonNode Read() => JsonNode.Parse(File.ReadAllText(Config))!;

    [Fact]
    public void Save_SetsTheInstancesThinking_AndKeepsTheRest()
    {
        File.WriteAllText(Config, """{"theme":"amber","providers":{"local":{"kind":"openai-compatible","model":"q"},"claude":{"kind":"anthropic","model":"c"}}}""");

        Assert.True(ThinkingConfigWriter.TrySave(Config, "claude", "high"));

        var root = Read();
        Assert.Equal("high", (string?)root["providers"]!["claude"]!["thinking"]);
        Assert.Null(root["providers"]!["local"]!["thinking"]);
        Assert.Equal("amber", (string?)root["theme"]);
        Assert.Equal("c", (string?)root["providers"]!["claude"]!["model"]);
    }

    [Fact]
    public void SavingTheDefault_RemovesTheKey()
    {
        File.WriteAllText(Config, """{"providers":{"claude":{"kind":"anthropic","model":"c","thinking":"high"}}}""");

        Assert.True(ThinkingConfigWriter.TrySave(Config, "claude", null));
        Assert.Null(Read()["providers"]!["claude"]!["thinking"]);
    }

    [Fact]
    public void AnInstanceConfigDoesNotName_IsNotSaved()
    {
        File.WriteAllText(Config, """{"providers":{"claude":{"kind":"anthropic","model":"c"}}}""");
        Assert.False(ThinkingConfigWriter.TrySave(Config, "gone", "high"));
        Assert.False(ThinkingConfigWriter.TrySave(Config, null, "high"));
    }
}
