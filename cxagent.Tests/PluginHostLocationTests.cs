using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Where a native plugin's host process is looked for, across the three ways cxagent is installed: a
/// single-file release with the host beside the executable, a <c>dotnet tool</c> whose executable is a
/// shim in one folder and whose files are in another, and a dev build where both are the same folder.
/// </summary>
public class PluginHostLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("plugin-host-location-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string PlaceHost(string folder)
    {
        var host = Path.Combine(folder, "plugin-host", "cxagent-plugin-host.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, "");
        return host;
    }

    /// <summary>A single-file release: install.sh puts the host beside the executable.</summary>
    [Fact]
    public void BesideTheExecutable_IsFound()
    {
        var exe = Folder("bin");
        var host = PlaceHost(exe);

        Assert.Equal(host, PluginResolver.PluginHostDllPath(exe, Folder("extracted")));
    }

    /// <summary>
    /// A dotnet tool: the executable is a shim in ~/.dotnet/tools and the package's files, the host
    /// among them, are under .store — which is where the app's own assemblies are loaded from.
    /// </summary>
    [Fact]
    public void BesideTheAppsOwnFiles_IsFound_WhenTheExecutableIsAShimElsewhere()
    {
        var store = Folder("store");
        var host = PlaceHost(store);

        Assert.Equal(host, PluginResolver.PluginHostDllPath(Folder("shim"), store));
    }

    [Fact]
    public void BesideTheExecutable_WinsWhenBothHaveOne()
    {
        var exe = Folder("bin");
        var host = PlaceHost(exe);
        PlaceHost(Folder("store"));

        Assert.Equal(host, PluginResolver.PluginHostDllPath(exe, Path.Combine(_root, "store")));
    }

    /// <summary>
    /// NEITHER HAS ONE: the answer is the executable's folder, the documented install location, so
    /// the "no plugin host executable at …" refusal names where a user would put it.
    /// </summary>
    [Fact]
    public void Missing_NamesTheExecutablesFolder()
    {
        var exe = Folder("bin");

        Assert.Equal(Path.Combine(exe, "plugin-host", "cxagent-plugin-host.dll"),
            PluginResolver.PluginHostDllPath(exe, Folder("store")));
    }

    [Fact]
    public void NoExecutablePath_FallsBackToTheAppsOwnFiles()
    {
        var store = Folder("store");

        Assert.Equal(Path.Combine(store, "plugin-host", "cxagent-plugin-host.dll"),
            PluginResolver.PluginHostDllPath(null, store));
    }
}
