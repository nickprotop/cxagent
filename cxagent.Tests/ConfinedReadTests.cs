using CxAgent.Core.Llm;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// FILES THAT REACH THE PROMPT ARE READ FROM WHERE THEY BELONG. A cloned repository's <c>AGENTS.md</c>, command or
/// skill linked to <c>~/.ssh/id_rsa</c> would put the key in the system prompt — sent to the provider — on the first
/// turn, trusted folder or not. A project's files are read only when their real path stays in the repository; a
/// plugin's sidecar only inside its load set; the user's own global files wherever they link. Every read takes only
/// a file with a size, at most a cap of it: a device or a pipe is never opened.
///
/// <para>THE DEVICE AND PIPE CASES TEST <see cref="PathResolver.ReadConfined"/> DIRECTLY. The old read of
/// <c>/dev/zero</c> allocates until memory runs out and the old read of a FIFO never returns, so neither can be
/// shown failing first without taking the test host down with it.</para>
/// </summary>
public sealed class ConfinedReadTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "cx-confined-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _outside;
    private readonly string _config;

    public ConfinedReadTests()
    {
        _repo = Path.Combine(_top, "repo");
        _outside = Path.Combine(_top, "outside");
        _config = Path.Combine(_top, "config");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(_config);
        File.WriteAllText(Path.Combine(_outside, "key"), "-----BEGIN PRIVATE KEY-----");
    }

    public void Dispose() { try { Directory.Delete(_top, recursive: true); } catch (IOException) { } }

    [Fact]
    public void AnAgentsMdLinkedOutside_IsNotRead_AndIsReportedAsSkipped()
    {
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_repo, "AGENTS.md"), Path.Combine(_outside, "key"));

        var scan = ProjectInstructions.Scan(_repo);

        Assert.DoesNotContain(scan.Files, f => f.Text.Contains("PRIVATE KEY"));
        var skipped = Assert.Single(scan.Skipped);
        Assert.EndsWith("AGENTS.md", skipped.Path);
        Assert.Contains("outside", skipped.Reason);
    }

    [Fact]
    public void AnAgentsMdLinkedInsideTheRepository_IsStillRead()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        File.WriteAllText(Path.Combine(_repo, "docs", "AGENTS.md"), "in the repo");
        File.CreateSymbolicLink(Path.Combine(_repo, "AGENTS.md"), Path.Combine(_repo, "docs", "AGENTS.md"));

        Assert.Equal("in the repo", Assert.Single(ProjectInstructions.Scan(_repo).Files).Text);
    }

    [Fact]
    public void ASessionOpenedThroughALink_ReadsItsOwnAgentsMd()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "mine");
        var alias = Path.Combine(_top, "alias");
        Directory.CreateSymbolicLink(alias, _repo);

        Assert.Equal("mine", Assert.Single(ProjectInstructions.Scan(alias).Files).Text);
    }

    [Fact]
    public void ACommandLinkedOutside_DoesNotShadowTheUsersOwn_AndIsListedAsAProblem()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_config, "commands"));
        File.WriteAllText(Path.Combine(_config, "commands", "review.md"), "My own review.");
        var commands = Path.Combine(_repo, ".cxagent", "commands");
        Directory.CreateDirectory(commands);
        File.CreateSymbolicLink(Path.Combine(commands, "review.md"), Path.Combine(_outside, "key"));

        var found = InvocableCatalog.Find(new CatalogRoots(_repo, _config));

        var review = Assert.Single(found.Entries, e => e.Name == "review");
        Assert.Equal(InvocableScope.Config, review.Scope);
        Assert.Contains(found.Problems, p => p.Path.EndsWith("review.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkillsFolderLinkedOutside_BringsInNoSkill()
    {
        if (OperatingSystem.IsWindows()) return;
        var away = Path.Combine(_outside, "skills", "journal");
        Directory.CreateDirectory(away);
        File.WriteAllText(Path.Combine(away, "SKILL.md"), "---\nname: journal\ndescription: private journal\n---\nPIN 1234");
        Directory.CreateDirectory(Path.Combine(_repo, ".agents"));
        Directory.CreateSymbolicLink(Path.Combine(_repo, ".agents", "skills"), Path.Combine(_outside, "skills"));

        var found = InvocableCatalog.Find(new CatalogRoots(_repo, null));

        Assert.DoesNotContain(found.Entries, e => e.Name == "journal");
        Assert.Contains(found.Problems, p => p.Reason.Contains("outside"));
    }

    [Fact]
    public void ASkillLinkedFromElsewhereInTheRepository_StillReads()
    {
        if (OperatingSystem.IsWindows()) return;
        var shared = Path.Combine(_repo, "tools", "skills", "x");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "SKILL.md"), "---\nname: x\ndescription: shared\n---\nbody");
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent", "skills"));
        Directory.CreateSymbolicLink(Path.Combine(_repo, ".cxagent", "skills", "x"), shared);

        Assert.Contains(InvocableCatalog.Find(new CatalogRoots(_repo, null)).Entries, e => e.Name == "x");
    }

    [Fact]
    public void AGlobalCommandLinkedIntoADotfilesFolder_StillReads()
    {
        if (OperatingSystem.IsWindows()) return;
        var dotfiles = Path.Combine(_outside, "dotfiles");
        Directory.CreateDirectory(dotfiles);
        File.WriteAllText(Path.Combine(dotfiles, "deploy.md"), "Deploy it.");
        Directory.CreateDirectory(Path.Combine(_config, "commands"));
        File.CreateSymbolicLink(Path.Combine(_config, "commands", "deploy.md"), Path.Combine(dotfiles, "deploy.md"));

        Assert.Contains(InvocableCatalog.Find(new CatalogRoots(_repo, _config)).Entries, e => e.Name == "deploy");
    }

    [Fact]
    public void ASidecarLinkedOutsideItsLoadSet_IsNotRead()
    {
        if (OperatingSystem.IsWindows()) return;
        var set = Path.Combine(_top, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "p.dll"), "bin");
        File.WriteAllText(Path.Combine(_outside, "p.plugin.json"), """{"pluginContract":2,"name":"p","version":"1","tools":[]}""");
        File.CreateSymbolicLink(Path.Combine(set, "p.plugin.json"), Path.Combine(_outside, "p.plugin.json"));

        Assert.Null(PluginResolver.DeclaredManifest(Path.Combine(set, "p.dll")));
    }

    [Fact]
    public void ADeviceOrAPipe_IsNeverOpened()
    {
        if (OperatingSystem.IsWindows()) return;
        var fifo = Path.Combine(_top, "pipe");
        System.Diagnostics.Process.Start("mkfifo", fifo)!.WaitForExit();

        foreach (var path in new[] { "/dev/zero", fifo })
        {
            var read = PathResolver.ReadConfined(new ConfinedRead(path, Root: null, MaxBytes: 1024));
            Assert.True(string.IsNullOrEmpty(read.Text), path);
        }
    }

    [Fact]
    public void AReadStopsAtItsCap()
    {
        File.WriteAllText(Path.Combine(_repo, "big.md"), new string('x', 5000));

        var read = PathResolver.ReadConfined(new ConfinedRead(Path.Combine(_repo, "big.md"), _repo, MaxBytes: 100));

        Assert.Equal(100, read.Text!.Length);
        Assert.True(read.Truncated);
    }

    /// <summary>
    /// DECODED AS File.ReadAllText DECODES: a UTF-8 byte-order mark is stripped, UTF-16 is read by its mark, and a cap
    /// never cuts a character in half — a sidecar saved by an editor that writes a BOM still parses, and an
    /// AGENTS.md written as UTF-16 reaches the prompt as its text.
    /// </summary>
    [Fact]
    public void AByteOrderMark_IsHonoured_AndTheCapKeepsWholeCharacters()
    {
        var bom = Path.Combine(_repo, "bom.json");
        File.WriteAllText(bom, """{"name":"p"}""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var utf16 = Path.Combine(_repo, "AGENTS.md");
        File.WriteAllText(utf16, "Use tabs.", System.Text.Encoding.Unicode);
        var accents = Path.Combine(_repo, "accents.md");
        File.WriteAllText(accents, "ééé");

        Assert.Equal("""{"name":"p"}""", PathResolver.ReadConfined(new ConfinedRead(bom, _repo, 1024)).Text);
        Assert.Equal("Use tabs.", PathResolver.ReadConfined(new ConfinedRead(utf16, _repo, 1024)).Text);
        Assert.Equal("é", PathResolver.ReadConfined(new ConfinedRead(accents, _repo, MaxBytes: 3)).Text);
    }

    /// <summary>ONCE PER SESSION, ITS SUB-AGENTS INCLUDED: a child's policy is narrowed from the session's, and finds the
    /// session's memory of what was said — a child rebuilding its prompt does not say it again.</summary>
    [Fact]
    public void ASubAgent_SharesTheSessionsNotices()
    {
        var policy = new PermissionPolicy(_repo, new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(_config)));
        Directory.CreateDirectory(Path.Combine(_repo, "sub"));
        var child = policy.NarrowedTo(Path.Combine(_repo, "sub"));
        var said = new List<Message>();
        var skipped = new SkippedInstructionFile("/r/AGENTS.md", "links outside the repository");

        policy.InstructionNoticesFor(said.Add).Announce([skipped]);
        child.InstructionNoticesFor(said.Add).Announce([skipped]);

        Assert.Single(said);
    }

    [Fact]
    public void ASkippedInstructionFile_IsSaidOncePerSession()
    {
        var said = new List<Message>();
        var notices = new InstructionNotices(said.Add);
        var skipped = new SkippedInstructionFile("/r/AGENTS.md", "links outside the repository (→ /x) — not read");

        notices.Announce([skipped]);
        notices.Announce([skipped]);

        var once = Assert.Single(said);
        Assert.Equal(Severity.Warning, once.Severity);
        Assert.Contains("/r/AGENTS.md", once.Text);
    }
}
