using System.Text;
using System.Text.RegularExpressions;
using CxAgent.Web.Http;
using Jint;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The page as shipped: its markdown renderer never lets model text become live HTML, and nothing on it
/// needs an inline script or style the content policy would refuse.</summary>
public sealed class WebAssetsTests
{
    private static string Asset(string name)
    {
        Assert.True(StaticAssets.TryGet("/" + name, out var body, out _), $"{name} is not embedded");
        return Encoding.UTF8.GetString(body.Span);
    }

    // THE MODULE RUN AS A SCRIPT: Jint evaluates plain script, and the module's exports are only `export function`.
    private static Engine Markdown()
    {
        var engine = new Engine();
        engine.Execute(Regex.Replace(Asset("highlight.js"), @"^\s*export\s+", "", RegexOptions.Multiline));
        engine.Execute(Regex.Replace(Regex.Replace(Asset("markdown.js"), @"^\s*import .*$", "", RegexOptions.Multiline),
            @"^\s*export\s+", "", RegexOptions.Multiline));
        return engine;
    }

    private static string Render(string text) => Markdown().Invoke("render", text).AsString();

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](data:text/html,<script>alert(1)</script>)")]
    [InlineData("`<b>`")]
    [InlineData("**<i>bold</i>**")]
    [InlineData("```html\n<script>alert(1)</script>\n```")]
    [InlineData("| a | b |\n|---|---|\n| <img src=x onerror=1> | x |")]
    [InlineData("[click](https://ok.example\" onmouseover=\"alert(1))")]
    public void Markdown_never_emits_live_html_from_model_text(string text)
    {
        var html = Render(text);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        // AN EVENT ATTRIBUTE INSIDE A REAL TAG — escaped text that merely reads "onerror=" is harmless and allowed.
        Assert.DoesNotMatch(new Regex(@"<[a-z][^>]*\son\w+\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_renders_code_fence_with_language_class()
    {
        var html = Render("```csharp\nvar x = \"a<b\";\n```");
        Assert.Contains("<pre><code class=\"lang-csharp\">", html, StringComparison.Ordinal);
        Assert.Contains("a&lt;b", html, StringComparison.Ordinal);
        Assert.Contains("tok-k", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_renders_table()
    {
        var html = Render("| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<th>a</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td>2</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_renders_the_ordinary_things()
    {
        var html = Render("# Title\n\nSome **bold**, *it*, `code` and [a link](https://example.com).\n\n- one\n- two");
        Assert.Contains("<h1>Title</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>it</em>", html, StringComparison.Ordinal);
        Assert.Contains("<code>code</code>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com\" rel=\"noopener noreferrer\" target=\"_blank\">a link</a>", html, StringComparison.Ordinal);
        Assert.Contains("<li>two</li>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/app.js", "text/javascript")]
    [InlineData("/markdown.js", "text/javascript")]
    [InlineData("/highlight.js", "text/javascript")]
    [InlineData("/app.css", "text/css")]
    [InlineData("/login.html", "text/html")]
    [InlineData("/login.js", "text/javascript")]
    public void Assets_are_embedded_and_served_with_correct_types(string path, string type)
    {
        Assert.True(StaticAssets.TryGet(path, out var body, out var contentType));
        Assert.StartsWith(type, contentType, StringComparison.Ordinal);
        Assert.True(body.Length > 0);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("login.html")]
    public void Pages_have_no_inline_script_or_style(string page)
    {
        var html = Asset(page);
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\sstyle\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\son\w+\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_app_script_never_assigns_model_text_as_html()
    {
        // innerHTML ONLY THROUGH render(): every other way text reaches the page is textContent.
        var app = Asset("app.js");
        foreach (Match m in Regex.Matches(app, @"innerHTML\s*=\s*([^;]+);"))
            Assert.Matches(new Regex(@"^(render\(|""""|''|``|html\b|safe\b)"), m.Groups[1].Value.Trim());
    }
}
