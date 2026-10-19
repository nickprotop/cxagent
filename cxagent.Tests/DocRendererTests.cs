using System.Diagnostics;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The documents rendered onto the site — the user's references and the plugin SDK — and the link check
/// that makes rendering them safe.
///
/// <para>A BROKEN LINK INSIDE YOUR OWN SITE IS INVISIBLE until someone clicks it, which is why the
/// renderer fails the build rather than emitting a 404. These tests exercise the real repo's real
/// documents: a fixture would prove the renderer works on markdown nobody ships.</para>
/// </summary>
public class DocRendererTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "plugins", "plugins.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"plugins/plugins.json not found walking up from '{AppContext.BaseDirectory}'.");
    }

    private static (int Exit, string Stderr) Render(string outDir, string? root = null)
    {
        var repo = RepoRoot();
        var psi = new ProcessStartInfo("python3")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Path.Combine(repo, "site", "build", "render_docs.py"));
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root ?? repo);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outDir);

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stderr);
    }

    [Fact]
    public void EveryRenderedDocumentRendersWithItsContent()
    {
        var dir = Directory.CreateTempSubdirectory("doc-render-").FullName;
        try
        {
            var (exit, stderr) = Render(dir);
            Assert.True(exit == 0, $"renderer failed: {stderr}");

            foreach (var name in new[] { "commands", "config", "plugins", "sdk", "plugin-cli", "plugin-testing",
                                         "plugin-c-sdk", "plugin-publishing", "plugin-abi" })
            {
                var page = Path.Combine(dir, "docs", $"{name}.html");
                Assert.True(File.Exists(page), $"no rendered page at '{page}'.");
            }

            // Content, not just a file: a renderer that wrote empty shells would pass a mere
            // existence check while shipping blank documentation.
            var commands = File.ReadAllText(Path.Combine(dir, "docs", "commands.html"));
            Assert.Contains("<h1", commands);
            Assert.Contains("/plugin", commands);

            // THE SDK RENDERS FROM sdk/, its tutorial images copied as assets beside it.
            var sdk = File.ReadAllText(Path.Combine(dir, "docs", "sdk.html"));
            Assert.Contains("cxagent plugin new", sdk);
            Assert.Contains("assets/", sdk);

            var cli = File.ReadAllText(Path.Combine(dir, "docs", "plugin-cli.html"));
            foreach (var verb in new[] { "plugin new", "plugin check", "plugin call", "plugin pack", "plugin verify" })
                Assert.Contains(verb, cli);
            // /plugin IS DOCUMENTED IN COMMANDS, not only linked from its nav.
            Assert.Contains("/plugin browse", commands);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// THE CHECK THAT MAKES THIS SAFE. A link into a document that is not rendered must become a
    /// GitHub URL, and one that resolves to nothing at all must stop the build.
    /// </summary>
    [Fact]
    public void ALinkThatResolvesToNothingFailsTheBuild()
    {
        var repo = RepoRoot();
        var fake = Directory.CreateTempSubdirectory("doc-broken-").FullName;
        var outDir = Directory.CreateTempSubdirectory("doc-broken-out-").FullName;
        try
        {
            // A minimal tree the renderer accepts, with one link pointing at nothing.
            Directory.CreateDirectory(Path.Combine(fake, "cxagent.Core", "sdk", "docs"));
            Directory.CreateDirectory(Path.Combine(fake, "cxagent.Core", "sdk", "c"));
            File.WriteAllText(Path.Combine(fake, "COMMANDS.md"),
                "# Commands\n\nSee [the config](CONFIG.md#no-such-heading).\n");
            File.WriteAllText(Path.Combine(fake, "CONFIG.md"), "# Configuration\n\nNothing here.\n");
            File.WriteAllText(Path.Combine(fake, "SCRIPTING.md"), "# Scripting\n\nNothing here.\n");
            foreach (var rel in new[] { "sdk/README.md", "sdk/docs/plugins.md", "sdk/docs/cli.md", "sdk/docs/testing.md",
                         "sdk/docs/c-sdk.md", "sdk/docs/publishing.md", "sdk/c/ABI.md" })
                File.WriteAllText(Path.Combine(fake, "cxagent.Core", rel), "# X\n\nNothing here.\n");
            // EVERY DOCUMENT IN RENDERED MUST EXIST, or the renderer fails on the missing file
            // before it reaches the broken link this test is about.
            Directory.CreateDirectory(Path.Combine(fake, "docs", "screenshots"));
            File.WriteAllText(Path.Combine(fake, "docs", "screenshots", "README.md"),
                "# cxagent, in use\n\nNothing here.\n");

            var (exit, stderr) = Render(outDir, root: fake);

            Assert.Equal(1, exit);
            Assert.Contains("no-such-heading", stderr);
        }
        finally
        {
            Directory.Delete(fake, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    /// <summary>
    /// A link into a document the site does not render leaves for GitHub rather than 404ing.
    /// `sdk/docs/plugins.md` links to `cxagent.Core/docs/tools.md`, which is not in the rendered set.
    /// </summary>
    [Fact]
    public void ALinkToAnUnrenderedDocumentLeavesForGitHub()
    {
        var dir = Directory.CreateTempSubdirectory("doc-github-").FullName;
        try
        {
            Render(dir);
            var plugins = File.ReadAllText(Path.Combine(dir, "docs", "plugins.html"));
            Assert.Contains("github.com/nickprotop/cxagent/blob/master/cxagent.Core/docs/tools.md",
                plugins);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// These documents separate their sections with a rule. Rendered as text it is three hyphens
    /// sitting in the prose, which reads as a mistake in the source rather than as a divider.
    /// </summary>
    [Fact]
    public void AThematicBreakBecomesARule()
    {
        var dir = Directory.CreateTempSubdirectory("doc-hr-").FullName;
        try
        {
            Render(dir);
            var page = File.ReadAllText(Path.Combine(dir, "docs", "commands.html"));

            Assert.Contains("<hr>", page);
            Assert.DoesNotContain("<p>---</p>", page);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// IMAGES ARE COPIED, NOT LINKED OUT. A .png resolved to a GitHub blob URL renders GitHub's page
    /// around the image rather than the image, so the walkthrough's seventeen captures would each
    /// show a framed web page.
    /// </summary>
    [Fact]
    public void ImagesAreRenderedAndTheirFilesCopied()
    {
        var dir = Directory.CreateTempSubdirectory("doc-img-").FullName;
        try
        {
            var (exit, stderr) = Render(dir);
            Assert.True(exit == 0, $"renderer failed: {stderr}");

            var page = File.ReadAllText(Path.Combine(dir, "docs", "walkthrough.html"));
            Assert.Contains("<img src=\"assets/01-trust.png\"", page);
            Assert.DoesNotContain("github.com/nickprotop/cxagent/blob/master/docs/screenshots/01-trust.png",
                page);

            Assert.True(File.Exists(Path.Combine(dir, "docs", "assets", "01-trust.png")),
                "the capture was referenced but never copied beside the page.");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// RENDERED DOCUMENTS SHARE A FILENAME. `docs/screenshots/README.md` and `cxagent.Core/sdk/README.md`
    /// are both rendered, and a lookup keyed on the basename would make one claim every `README.md`
    /// link in the repository — including `cxagent.Core/sdk/docs/plugins.md`'s link to the package's own
    /// README — and fail the anchor check against the wrong document.
    /// </summary>
    [Fact]
    public void ALinkToADifferentReadmeIsNotClaimedByTheRenderedOne()
    {
        var dir = Directory.CreateTempSubdirectory("doc-readme-").FullName;
        try
        {
            var (exit, stderr) = Render(dir);
            Assert.True(exit == 0, $"renderer failed: {stderr}");

            // plugins.md links to ../../README.md#tools-that-arrive-at-run-time — the package README,
            // which is not rendered, so it must leave for GitHub rather than resolve to the
            // walkthrough that happens to share its filename.
            var plugins = File.ReadAllText(Path.Combine(dir, "docs", "plugins.html"));
            Assert.Contains("github.com/nickprotop/cxagent/blob/master/cxagent.Core/README.md", plugins);
            Assert.DoesNotContain("walkthrough.html#tools-that-arrive-at-run-time", plugins);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Renders <paramref name="markdown"/> as COMMANDS.md in a fake tree and returns commands.html. The tree
    /// holds every path the renderer may list — old and new — so it does not depend on which ones it does.
    /// </summary>
    private static string RenderCommands(string markdown)
    {
        var fake = Directory.CreateTempSubdirectory("doc-construct-").FullName;
        var outDir = Directory.CreateTempSubdirectory("doc-construct-out-").FullName;
        try
        {
            foreach (var rel in new[] { "CONFIG.md", "SCRIPTING.md", "docs/screenshots/README.md",
                         "cxagent.Core/docs/plugins.md", "cxagent.Core/sdk/README.md",
                         "cxagent.Core/sdk/docs/plugins.md", "cxagent.Core/sdk/docs/testing.md",
                         "cxagent.Core/sdk/docs/c-sdk.md", "cxagent.Core/sdk/docs/publishing.md",
                         "cxagent.Core/sdk/docs/cli.md", "cxagent.Core/sdk/c/ABI.md" })
            {
                var path = Path.Combine(fake, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "# X\n\nNothing here.\n");
            }
            File.WriteAllText(Path.Combine(fake, "COMMANDS.md"), markdown);

            var (exit, stderr) = Render(outDir, root: fake);
            Assert.True(exit == 0, $"renderer failed: {stderr}");
            return File.ReadAllText(Path.Combine(outDir, "docs", "commands.html"));
        }
        finally
        {
            Directory.Delete(fake, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    /// <summary>A `\|` in a table cell is a literal pipe — a flag's `allow\|deny` — not a column break.</summary>
    [Fact]
    public void AnEscapedPipe_StaysInItsCell()
    {
        var html = RenderCommands("# C\n\n| Flag | What |\n|---|---|\n| `allow\\|deny` | the answer |\n");
        var row = html[html.IndexOf("allow", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Contains("allow|deny", row);
        Assert.Equal(1, row.Split("<td").Length - 1);   // after the flag's own cell, one more
        Assert.DoesNotContain("\\", row);
    }

    /// <summary>A bullet wrapped onto an indented line is one item, as GitHub renders it.</summary>
    [Fact]
    public void AWrappedBullet_IsOneItem()
    {
        var html = RenderCommands("# C\n\n- first line\n  continues here\n- second\n");
        Assert.Contains("first line continues here", html);
        Assert.Equal(2, html.Split("<li>").Length - 1);
        Assert.DoesNotContain("<p>", html[html.IndexOf("<ul>", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void ANestedBullet_IsAListInsideTheItem()
    {
        var html = RenderCommands("# C\n\n- outer\n  - inner\n- next\n");
        Assert.Matches(@"<li>outer\s*<ul>\s*<li>inner</li>\s*</ul>\s*</li>\s*<li>next</li>", html);
    }

    [Fact]
    public void ANumberedList_IsAnOrderedList()
    {
        var html = RenderCommands("# C\n\nSteps:\n\n1. Upload\n2. Put\n3. Submit\n");
        Assert.Matches(@"<ol>\s*<li>Upload</li>\s*<li>Put</li>\s*<li>Submit</li>\s*</ol>", html);
    }

    /// <summary>An indented paragraph after a blank line belongs to the item above it, and the list goes on.</summary>
    [Fact]
    public void AParagraphInsideAnItem_StaysInIt()
    {
        var html = RenderCommands("# C\n\n- **pack** reads from:\n  1. its options;\n  2. a file.\n\n  Anything unset is marked.\n- **verify** checks it.\n");
        Assert.Matches(@"<li><strong>pack</strong> reads from:<ol>.*</ol><p>Anything unset is marked\.</p></li><li><strong>verify</strong>", html);
        Assert.Equal(1, html.Split("<ul>").Length - 1);
    }
}
