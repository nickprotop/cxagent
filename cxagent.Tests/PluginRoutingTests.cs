using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Which loader a plugin file goes to: its sidecar's kind when it names one, the extension otherwise.</summary>
public class PluginRoutingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("routing-").FullName;
    public void Dispose() => TempDirectory.Delete(_dir);

    private string Plugin(string file, string? kind)
    {
        var path = Path.Combine(_dir, file);
        File.WriteAllText(path, "");
        var kindField = kind is null ? "" : $",\"kind\":\"{kind}\"";
        File.WriteAllText(Path.ChangeExtension(path, null) + ".plugin.json",
            $$"""{"pluginContract":4,"name":"p","version":"1.0.0","spawns":false,"tools":[]{{kindField}}}""");
        return path;
    }

    /// <summary>Review focus 5: the declared kind wins on every OS — a native .dll is how Windows ships one.</summary>
    [Theory]
    [InlineData("p.so", null, true)]
    [InlineData("p.dylib", null, true)]
    [InlineData("p.dll", null, false)]
    [InlineData("p.dll", "native", true)]
    [InlineData("p.so", "managed", false)]
    public void KindWinsOverTheExtension(string file, string? kind, bool native) =>
        Assert.Equal(native, PluginResolver.IsNative(Plugin(file, kind)));

    [Fact]
    public void NoSidecar_FallsBackToTheExtension() =>
        Assert.True(PluginResolver.IsNative(Path.Combine(_dir, "orphan.so")));

    /// <summary>A sidecar that will not parse routes by the extension; the loader then reports the sidecar.</summary>
    [Fact]
    public void AnUnreadableSidecar_FallsBackToTheExtension()
    {
        var path = Path.Combine(_dir, "broken.dll");
        File.WriteAllText(path, "");
        File.WriteAllText(Path.Combine(_dir, "broken.plugin.json"), "{ not json");

        Assert.False(PluginResolver.IsNative(path));
    }
}
