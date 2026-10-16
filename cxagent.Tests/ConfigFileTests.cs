using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using Xunit;

namespace CxAgent.Tests;

public class ConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-cfgfile-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "config.json");
    private static readonly Dictionary<string, string> NoEnv = new();

    public ConfigFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private void Write(string json) => File.WriteAllText(PathOf, json);

    private const string Valid = """
        { "providers": { "local": { "kind": "ollama", "model": "m", "x-mine": true } },
          "defaultProvider": "local", "futureKey": 1 }
        """;

    [Fact]
    public void ASavedEdit_KeepsEverythingElse()
    {
        Write(Valid);
        var result = ConfigFile.TryApply(PathOf, NoEnv, root =>
            ProviderEdits.Update(root, "local", new InstanceFields { Model = "m2" }));

        Assert.True(result.Saved);
        var after = JsonNode.Parse(File.ReadAllText(PathOf))!;
        Assert.Equal("m2", (string?)after["providers"]!["local"]!["model"]);
        Assert.True((bool?)after["providers"]!["local"]!["x-mine"]);
        Assert.Equal(1, (int?)after["futureKey"]);
        Assert.Equal("m2", result.Settings!.Providers["local"].Model);
    }

    [Fact]
    public void AnEditTheLoaderRejects_WritesNothing()
    {
        Write(Valid);
        var result = ConfigFile.TryApply(PathOf, NoEnv, root => root["defaultProvider"] = "gone");

        Assert.False(result.Saved);
        Assert.Contains(result.Errors, e => e.Contains("gone"));
        Assert.Equal(Valid, File.ReadAllText(PathOf));
    }

    [Fact]
    public void AnUnparseableFile_IsRefused_AndLeftAlone()
    {
        Write("{ broken");
        var result = ConfigFile.TryApply(PathOf, NoEnv, root => root["theme"] = "x");

        Assert.False(result.Saved);
        Assert.Contains("not valid JSON", result.Errors[0]);
        Assert.Equal("{ broken", File.ReadAllText(PathOf));
    }

    [Fact]
    public void AnEditThatThrows_IsReported_NotWritten()
    {
        Write(Valid);
        var result = ConfigFile.TryApply(PathOf, NoEnv, root => ProviderEdits.Remove(root, "nope"));
        Assert.False(result.Saved);
        Assert.Contains("nope", result.Errors[0]);
    }

    [Fact]
    public void NoFileYet_StartsFromAnEmptyDocument()
    {
        var result = ConfigFile.TryApply(PathOf, NoEnv, root =>
            ProviderEdits.Add(root, "local", new InstanceFields { Kind = "ollama", Model = "m" }));
        Assert.True(result.Saved);
        Assert.Equal("local", result.Settings!.DefaultProvider);
    }

    [Fact]
    public void TheFile_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        Write(Valid);
        ConfigFile.TryApply(PathOf, NoEnv, root => root["theme"] = "x");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PathOf));
    }

    // ---- /think --save: one key on one instance, everything else left alone -----------------------

    [Fact]
    public void SaveThinkingDefault_SetsTheInstancesThinking_AndKeepsTheRest()
    {
        Write("""{"theme":"amber","providers":{"local":{"kind":"openai-compatible","model":"q","baseUrl":"http://x/v1"},"claude":{"kind":"anthropic","model":"c"}},"defaultProvider":"local"}""");

        Assert.True(ConfigFile.SaveThinkingDefault(PathOf, NoEnv, "claude", "high"));

        var root = JsonNode.Parse(File.ReadAllText(PathOf))!;
        Assert.Equal("high", (string?)root["providers"]!["claude"]!["thinking"]);
        Assert.Null(root["providers"]!["local"]!["thinking"]);
        Assert.Equal("amber", (string?)root["theme"]);
    }

    [Fact]
    public void SaveThinkingDefault_TheModelsDefault_RemovesTheKey()
    {
        Write("""{"providers":{"claude":{"kind":"anthropic","model":"c","thinking":"high"}},"defaultProvider":"claude"}""");

        Assert.True(ConfigFile.SaveThinkingDefault(PathOf, NoEnv, "claude", null));
        Assert.Null(JsonNode.Parse(File.ReadAllText(PathOf))!["providers"]!["claude"]!["thinking"]);
    }

    [Fact]
    public void SaveThinkingDefault_AnInstanceTheFileDoesNotName_IsNotSaved()
    {
        Write("""{"providers":{"claude":{"kind":"anthropic","model":"c"}},"defaultProvider":"claude"}""");
        Assert.False(ConfigFile.SaveThinkingDefault(PathOf, NoEnv, "gone", "high"));
        Assert.False(ConfigFile.SaveThinkingDefault(PathOf, NoEnv, null, "high"));
    }
}
