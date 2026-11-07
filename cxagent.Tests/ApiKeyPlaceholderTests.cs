using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A provider's <c>apiKey</c> takes <c>{env:NAME}</c> and <c>{file:path}</c>, as the config form says it does — so a
/// key need not sit in config.json, and a placeholder is never sent to an endpoint as if it were the key.
/// </summary>
public sealed class ApiKeyPlaceholderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-apikey").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProviderSettings Parse(string apiKey, Dictionary<string, string> env) =>
        ProviderConfigLoader.Parse($$"""
            { "providers": { "local": { "kind": "openai-compatible", "model": "m", "baseUrl": "http://localhost:1/v1",
                                        "apiKey": "{{apiKey}}" } },
              "defaultProvider": "local" }
            """, env);

    [Fact]
    public void An_env_placeholder_is_read_from_the_environment()
    {
        var settings = Parse("{env:MY_PROVIDER_KEY}", new() { ["MY_PROVIDER_KEY"] = "sk-from-env" });
        Assert.Equal("sk-from-env", settings.Providers["local"].ApiKey);
    }

    [Fact]
    public void A_file_placeholder_is_read_from_the_file()
    {
        var path = Path.Combine(_dir, "key.txt");
        File.WriteAllText(path, "sk-from-file\n");
        var settings = Parse("{file:" + path.Replace("\\", "\\\\") + "}", []);
        Assert.Equal("sk-from-file", settings.Providers["local"].ApiKey);
    }

    [Fact]
    public void An_unset_variable_warns_and_sends_no_key_rather_than_the_placeholder()
    {
        var settings = Parse("{env:NOT_SET_ANYWHERE_XYZ}", []);
        Assert.Null(settings.Providers["local"].ApiKey);
        Assert.Contains(settings.Warnings, w => w.Contains("local", StringComparison.Ordinal) && w.Contains("NOT_SET_ANYWHERE_XYZ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_literal_key_is_used_as_written() =>
        Assert.Equal("sk-literal", Parse("sk-literal", []).Providers["local"].ApiKey);

    [Fact]
    public void The_override_variable_still_wins()
    {
        var settings = Parse("{env:MY_PROVIDER_KEY}", new() { ["MY_PROVIDER_KEY"] = "sk-from-env", ["CXAGENT_PROVIDER_LOCAL_APIKEY"] = "sk-override" });
        Assert.Equal("sk-override", settings.Providers["local"].ApiKey);
    }
}
