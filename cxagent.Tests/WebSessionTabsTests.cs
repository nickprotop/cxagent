using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// On a wide screen a session's own views — its workers, colleagues, commands, files, shell, config — open in tabs
/// above it, the terminal's <c>Chat │ ◆ Charting ×</c>, instead of taking the page with only "Back to session" to say
/// the chat is still there (user, 2026-11-08). Sessions stay in the sidebar there; a narrow screen keeps its strip of
/// every open view.
/// </summary>
public sealed class WebSessionTabsTests
{
    private static string Asset(string path)
    {
        Assert.True(CxAgent.Web.Http.StaticAssets.TryGet(path, out var body, out _));
        return System.Text.Encoding.UTF8.GetString(body.Span);
    }

    [Fact]
    public void The_strip_shows_on_a_wide_screen()
    {
        var css = Asset("/app.css");
        var wide = css[css.IndexOf("@media (min-width: 1100px)", StringComparison.Ordinal)..];

        Assert.Contains(".tabs { display: flex; }", wide[..wide.IndexOf("\n}", StringComparison.Ordinal)]);
    }

    [Fact]
    public void A_wide_strip_holds_the_session_in_front_and_what_was_opened_from_it()
    {
        var js = Asset("/app.js");

        Assert.Contains("function tabsShown(", js);
        Assert.Contains("\"Chat\"", js);
    }

    [Fact]
    public void Closing_a_tab_returns_to_the_session_it_was_opened_from()
    {
        var js = Asset("/app.js");
        var drop = js[js.IndexOf("function dropView(", StringComparison.Ordinal)..];

        Assert.Contains("view.parent", drop[..drop.IndexOf("\n}", StringComparison.Ordinal)]);
    }
}
