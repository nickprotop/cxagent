using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The startup notice for plugins that loaded: one line for the lot, not a System block per plugin.
/// </summary>
public class PluginsLoadedNoticeTests
{
    [Fact]
    public void NothingLoadedSaysNothing() =>
        Assert.Null(Session.FormatPluginsLoaded([]));

    [Fact]
    public void OnePluginKeepsTheSentenceItAlwaysHad() =>
        Assert.Equal("plugin 'calculator' loaded — 1 tool(s) offered.",
            Session.FormatPluginsLoaded([("calculator", 1)]));

    [Fact]
    public void SeveralPluginsAreOneLine_NamingEachWithItsToolCount()
    {
        var text = Session.FormatPluginsLoaded(
            [("calculator", 1), ("clone-finder", 1), ("csharp-lsp", 3), ("triggers", 4)]);

        Assert.Equal("4 plugins loaded — calculator (1 tool), clone-finder (1 tool), csharp-lsp (3 tools), "
            + "triggers (4 tools).", text);
        Assert.DoesNotContain('\n', text!);
    }
}
