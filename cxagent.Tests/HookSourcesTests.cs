using System.Text.Json;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Where hooks come from: the user's config, and the repository's two files — read once, each tagged with
/// its source, and a broken file costing only itself.
/// </summary>
public class HookSourcesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-hook-src").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Repo()
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, ".cxagent"));
        return repo;
    }

    private static void Write(string repo, string file, string hooksBlock) =>
        File.WriteAllText(Path.Combine(repo, ".cxagent", file), $$"""{ "hooks": {{hooksBlock}} }""");

    private static HooksSettings User(string block) => new(JsonDocument.Parse(block).RootElement.Clone());

    private const string OneStop = """{ "Stop": [ { "hooks": [ { "type": "command", "command": "x" } ] } ] }""";

    [Fact]
    public void Reads_user_project_and_local()
    {
        var repo = Repo();
        Write(repo, "hooks.json", OneStop);
        Write(repo, "hooks.local.json", OneStop);
        var s = HookSources.Read(User(OneStop), repo);
        Assert.Empty(s.Problems);
        Assert.Equal([HookSource.User, HookSource.Project, HookSource.Local], s.Hooks.Select(h => h.Source));
        Assert.Equal(Path.GetFullPath(repo), s.RepoRoot);
    }

    [Fact]
    public void Missing_files_are_fine()
    {
        var s = HookSources.Read(HooksSettings.None, Repo());
        Assert.Empty(s.Hooks);
        Assert.Empty(s.Problems);
    }

    [Fact]
    public void Malformed_project_file_is_a_problem_not_a_crash()
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, ".cxagent", "hooks.json"), "{ not json");
        var s = HookSources.Read(User(OneStop), repo);
        var p = Assert.Single(s.Problems);
        Assert.Equal(HookSource.Project, p.Source);
        Assert.EndsWith("hooks.json", p.File);
        Assert.Equal(HookSource.User, Assert.Single(s.Hooks).Source);
    }

    [Fact]
    public void Project_file_without_hooks_object_is_a_problem()
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, ".cxagent", "hooks.json"), """{ "Stop": [] }""");
        Assert.Single(HookSources.Read(HooksSettings.None, repo).Problems);
    }

    [Fact]
    public void Repo_root_is_the_top_level()
    {
        var repo = Repo();
        Write(repo, "hooks.json", OneStop);
        var deep = Path.Combine(repo, "src", "lib");
        Directory.CreateDirectory(deep);
        var s = HookSources.Read(HooksSettings.None, deep);
        Assert.Equal(HookSource.Project, Assert.Single(s.Hooks).Source);
    }

    [Fact]
    public void Outside_a_repository_there_are_no_project_hooks()
    {
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(Path.Combine(plain, ".cxagent"));
        Write(plain, "hooks.json", OneStop);
        var s = HookSources.Read(HooksSettings.None, plain);
        Assert.Null(s.RepoRoot);
        Assert.Empty(s.Hooks);
    }

    [Fact]
    public void ChangedSince_reports_an_edit()
    {
        var repo = Repo();
        Write(repo, "hooks.json", OneStop);
        var s = HookSources.Read(HooksSettings.None, repo);
        Assert.Empty(HookSources.ChangedSince(s));
        Write(repo, "hooks.local.json", OneStop);
        Assert.Equal(["hooks.local.json"], HookSources.ChangedSince(s).Select(Path.GetFileName));
    }

    private const string Providers = """
        "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://localhost:1/v1" } },
        "defaultProvider": "local"
        """;

    [Fact]
    public void Config_hooks_block_reaches_ResolvedConfig()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"),
            $$"""{ {{Providers}}, "hooks": { "defaultModel": "local", "Stop": [ { "hooks": [ { "type": "command", "command": "x" } ] } ] } }""");
        var settings = ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
        Assert.Equal("local", settings.Hooks?.DefaultModel);
        var resolved = ConfigResolver.Resolve(new AppPaths(_dir), new Dictionary<string, string>(), useMock: false);
        Assert.NotNull(resolved.Hooks.Block);
        Assert.True(resolved.Hooks.Block!.Value.TryGetProperty("Stop", out _));
    }

    [Fact]
    public void No_hooks_block_resolves_to_none()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ {{Providers}} }""");
        Assert.Null(ConfigResolver.Resolve(new AppPaths(_dir), new Dictionary<string, string>(), useMock: false).Hooks.Block);
    }

    [Fact]
    public void Non_object_hooks_warns()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ {{Providers}}, "hooks": [] }""");
        var settings = ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
        Assert.Contains(settings.Warnings, w => w.StartsWith("hooks"));
        Assert.Null(settings.Hooks);
    }
}
