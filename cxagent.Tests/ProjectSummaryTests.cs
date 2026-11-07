using CxAgent.Core.Hooks;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>F8's Project tab without the screen: what a repository brings to the agent working in it.</summary>
public class ProjectSummaryTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("cxagent-project-summary").FullName;
    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private ProjectSummary Summary(TrustState trust = TrustState.Trusted) =>
        ProjectSummary.For(new ProjectFacts(_repo, trust,
            InvocableCatalog.Find(new CatalogRoots(_repo, null)),
            HookSources.Read(HooksSettings.None, _repo)));

    [Fact]
    public void Summary_counts_what_the_repo_brings()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Write("AGENTS.md", "# rules");
        Write(".cxagent/skills/deploy/SKILL.md", "---\nname: deploy\ndescription: Use when deploying\n---\nbody");
        Write(".cxagent/commands/review.md", "review the diff");
        Write(".cxagent/commands/ship.md", "ship it");
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent", "plugins", "lsp"));
        Write(".cxagent/hooks.json", """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] } }""");
        Write(".cxagent/hooks.local.json", """{ "hooks": { "PreToolUse": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] } }""");

        var s = Summary();
        Assert.Equal(Path.GetFullPath(_repo), s.RepoRoot);
        Assert.Equal(TrustState.Trusted, s.Trust);
        Assert.Equal(1, s.Item("Instruction files").Count);
        Assert.Equal(1, s.Item("Skills").Count);
        Assert.Equal(2, s.Item("Commands").Count);
        Assert.Equal(1, s.Item("Plugins").Count);
        Assert.Equal(2, s.Item("Hooks").Count);
        Assert.Contains("review", s.Item("Commands").Names);
    }

    [Fact]
    public void Untrusted_hooks_say_they_do_not_run()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Write(".cxagent/hooks.json", """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] } }""");
        Assert.Contains("not trusted", Summary(TrustState.Unknown).Item("Hooks").State);
    }

    [Fact]
    public void Summary_outside_repo_is_empty()
    {
        var s = Summary();
        Assert.Null(s.RepoRoot);
        Assert.All(s.Items, i => Assert.Equal(0, i.Count));
    }

    [Fact]
    public void Memory_lists_this_projects_notes_and_where_they_live_not_the_system_ones()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        var config = Directory.CreateTempSubdirectory("cxagent-project-summary-cfg").FullName;
        try
        {
            var store = new CxAgent.Core.Memory.MemoryStore(config, _repo);
            store.Write(new("deploy-target", "d", CxAgent.Core.Memory.MemoryScope.Project, "b"));
            store.Write(new("build-command", "d", CxAgent.Core.Memory.MemoryScope.Project, "b"));
            store.Write(new("prefers-tabs", "d", CxAgent.Core.Memory.MemoryScope.System, "b"));

            var memory = ProjectSummary.For(new ProjectFacts(_repo, TrustState.Trusted,
                InvocableCatalog.Find(new CatalogRoots(_repo, null)), HookSources.Read(HooksSettings.None, _repo)) { Memory = store })
                .Item("Memory");

            Assert.Equal(2, memory.Count);
            Assert.Equal(["build-command", "deploy-target"], memory.Names);
            Assert.Equal(store.ProjectFolder, memory.Folder);
            Assert.Null(memory.State);
        }
        finally { Directory.Delete(config, recursive: true); }
    }

    [Fact]
    public void Memory_says_off_when_it_is_off()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Assert.Equal("off", Summary().Item("Memory").State);
    }
}
