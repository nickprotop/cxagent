using System.Diagnostics;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The three documents rendered onto the site, and the link check that makes rendering them safe.
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
    public void TheThreeDocumentsRenderWithTheirContent()
    {
        var dir = Directory.CreateTempSubdirectory("doc-render-").FullName;
        try
        {
            var (exit, stderr) = Render(dir);
            Assert.True(exit == 0, $"renderer failed: {stderr}");

            foreach (var name in new[] { "commands", "config", "plugins" })
            {
                var page = Path.Combine(dir, "docs", $"{name}.html");
                Assert.True(File.Exists(page), $"no rendered page at '{page}'.");
            }

            // Content, not just a file: a renderer that wrote empty shells would pass a mere
            // existence check while shipping blank documentation.
            var commands = File.ReadAllText(Path.Combine(dir, "docs", "commands.html"));
            Assert.Contains("<h1", commands);
            Assert.Contains("/plugin", commands);
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
            Directory.CreateDirectory(Path.Combine(fake, "cxagent.Core", "docs"));
            File.WriteAllText(Path.Combine(fake, "COMMANDS.md"),
                "# Commands\n\nSee [the config](CONFIG.md#no-such-heading).\n");
            File.WriteAllText(Path.Combine(fake, "CONFIG.md"), "# Configuration\n\nNothing here.\n");
            File.WriteAllText(Path.Combine(fake, "cxagent.Core", "docs", "plugins.md"),
                "# Writing a plugin\n\nNothing here.\n");
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
    /// `plugins.md` links to `tools.md`, which is not in the rendered set.
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
    /// TWO RENDERED DOCUMENTS CAN SHARE A FILENAME. `docs/screenshots/README.md` is one, and a
    /// lookup keyed on the basename made it claim every `README.md` link in the repository —
    /// including `cxagent.Core/docs/plugins.md`'s link to the repository's own README, which then
    /// failed the anchor check against the wrong document entirely.
    /// </summary>
    [Fact]
    public void ALinkToADifferentReadmeIsNotClaimedByTheRenderedOne()
    {
        var dir = Directory.CreateTempSubdirectory("doc-readme-").FullName;
        try
        {
            var (exit, stderr) = Render(dir);
            Assert.True(exit == 0, $"renderer failed: {stderr}");

            // plugins.md links to ../README.md#tools-that-arrive-at-run-time — the package README,
            // which is not rendered, so it must leave for GitHub rather than resolve to the
            // walkthrough that happens to share its filename.
            var plugins = File.ReadAllText(Path.Combine(dir, "docs", "plugins.html"));
            Assert.Contains("github.com/nickprotop/cxagent/blob/master/cxagent.Core/README.md", plugins);
            Assert.DoesNotContain("walkthrough.html#tools-that-arrive-at-run-time", plugins);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
