using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>classifierTimeoutSeconds</c>, which exists because a local classifier shares its model with the
/// agent: measured against a 35B local model, a classification takes 150ms idle and 13.5s with two
/// sub-agents generating, so no single default fits both a hosted and a local setup.
/// </summary>
public class ClassifierTimeoutConfigTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("cxagent-classifier-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProviderSettings Load(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), json);
        return ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
    }

    private const string Providers = """
        "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://localhost:1/v1" } },
        "defaultProvider": "local", "classifier": "local"
        """;

    // THE KEY IS READ. Config is parsed by hand with TryGetProperty, so a property added to the
    // settings record binds nothing until the loader asks for it — a key that looks configured and
    // changes nothing is exactly the failure this catches.
    [Fact]
    public void TheTimeoutIsRead()
    {
        var settings = Load($$"""{ {{Providers}}, "classifierTimeoutSeconds": 45 }""");

        Assert.Equal(45, settings.ClassifierTimeoutSeconds);
        Assert.DoesNotContain(settings.Warnings, w => w.Contains("classifierTimeout"));
    }

    // ABSENT MEANS THE DEFAULT, and says nothing about it: most setups never touch this.
    [Fact]
    public void AbsentIsNull()
    {
        var settings = Load($$"""{ {{Providers}} }""");

        Assert.Null(settings.ClassifierTimeoutSeconds);
    }

    // ZERO WOULD TIME OUT EVERY CLASSIFICATION INSTANTLY. Because the classifier fails closed to ASK,
    // that reads as auto mode working normally rather than one that never decides — so it is refused
    // at load, where a person can still see it.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveValueIsRefused(int seconds)
    {
        var settings = Load($$"""{ {{Providers}}, "classifierTimeoutSeconds": {{seconds}} }""");

        Assert.Null(settings.ClassifierTimeoutSeconds);
        Assert.Contains(settings.Warnings, w => w.Contains("classifierTimeoutSeconds"));
    }

    [Fact]
    public void ANonNumberIsRefused()
    {
        var settings = Load($$"""{ {{Providers}}, "classifierTimeoutSeconds": "thirty" }""");

        Assert.Null(settings.ClassifierTimeoutSeconds);
        Assert.Contains(settings.Warnings, w => w.Contains("classifierTimeoutSeconds"));
    }
}
