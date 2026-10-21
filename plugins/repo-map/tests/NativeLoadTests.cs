using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

/// <summary>
/// The plugin finds its natives beside itself, wherever it is installed.
///
/// <para>A COPY, IN ITS OWN LOAD CONTEXT. In this test process AppContext.BaseDirectory is the test's
/// bin folder, which holds natives too, so loading the plugin from there would pass whether the
/// vendored loader looked in the plugin's folder or not. A copy in a temp folder, loaded the way
/// cxagent loads a plugin from its install folder, passes only if the libraries mapped into the
/// process are the copy's.</para>
/// </summary>
public class NativeLoadTests
{
    [Fact]
    public void ThePluginLoadsItsNativesFromItsOwnFolder()
    {
        // /proc/self/maps is how this test sees which file was loaded; only linux-x64 is tested (decision 8).
        if (!OperatingSystem.IsLinux()) return;

        var built = Path.GetDirectoryName(typeof(RepoMapPlugin).Assembly.Location)!;
        using var temp = new TempTree();
        File.Copy(Path.Combine(built, "repo-map.dll"), Path.Combine(temp.Root, "repo-map.dll"));
        var natives = Path.Combine("runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native");
        Directory.CreateDirectory(Path.Combine(temp.Root, natives));
        foreach (var file in Directory.GetFiles(Path.Combine(built, natives)))
            File.Copy(file, Path.Combine(temp.Root, natives, Path.GetFileName(file)));

        var context = new AssemblyLoadContext("repo-map-copy");
        var copy = context.LoadFromAssemblyPath(Path.Combine(temp.Root, "repo-map.dll"));
        var probe = copy.GetType("CxAgent.Plugins.RepoMap.Parsing.Natives")!
            .GetMethod("Probe", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Null(probe.Invoke(null, null));
        Assert.Contains(Path.Combine(temp.Root, natives), File.ReadAllText("/proc/self/maps"));
    }

    [Fact]
    public void TheNativesLoadHere() => Assert.Null(Parsing.Natives.Problem);
}
