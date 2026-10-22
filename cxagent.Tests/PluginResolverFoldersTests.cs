using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The two built-in plugin folders, named by what they are — not by where they fall in the search
/// order, which puts the global folder before the project's, so "the last one" is the project's.
/// </summary>
public class PluginResolverFoldersTests
{
    [Fact]
    public void TheGlobalFolder_IsTheConfigDirsPlugins_WhereverItFallsInTheSearchOrder()
    {
        var config = Path.Combine(Path.GetTempPath(), "cfg");
        var project = Path.Combine(Path.GetTempPath(), "proj");
        var folders = PluginResolver.SearchFolders([], project, config);

        Assert.Equal(Path.Combine(config, "plugins"), PluginResolver.GlobalFolder(config));
        Assert.Equal(Path.GetFullPath(Path.Combine(project, ".cxagent", "plugins")),
                     Path.GetFullPath(PluginResolver.ProjectFolder(project)));
        Assert.Contains(PluginResolver.GlobalFolder(config), folders);
        Assert.NotEqual(PluginResolver.GlobalFolder(config), folders[^1]);
    }
}
