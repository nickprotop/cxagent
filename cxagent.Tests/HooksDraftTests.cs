using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using CxAgent.Core.Storage;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// F8's hooks editor without the screen: a draft over all three files, written back only where it changed,
/// keeping what it does not edit exactly as it was.
/// </summary>
public class HooksDraftTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("cxagent-hooks-draft-cfg").FullName;
    private readonly string _repo = Directory.CreateTempSubdirectory("cxagent-hooks-draft-repo").FullName;

    public HooksDraftTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent"));
        // A CONFIG WITH A PROVIDER, as F8 always has: the loader judges every write to it.
        File.WriteAllText(Path.Combine(_cfg, "config.json"),
            """{ "providers": { "x": { "kind": "ollama", "model": "m" } } }""");
    }

    public void Dispose()
    {
        Directory.Delete(_cfg, recursive: true);
        Directory.Delete(_repo, recursive: true);
    }

    private string ConfigPath => Path.Combine(_cfg, "config.json");
    private string ProjectPath => Path.Combine(_repo, ".cxagent", "hooks.json");
    private string LocalPath => Path.Combine(_repo, ".cxagent", "hooks.local.json");
    private static readonly Dictionary<string, string> Env = new() { ["TOKEN"] = "secret-value" };

    private HooksDraft Load(string? root = "repo") =>
        HooksFile.Load(new AppPaths(_cfg), Env, root is null ? Path.Combine(_cfg) : _repo);

    private IReadOnlyList<string> Save(HooksDraft d) => HooksFile.Save(d, new AppPaths(_cfg), Env);

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    [Fact]
    public void Draft_add_edit_remove_roundtrip()
    {
        var d = Load();
        var h = d.Add(HookSource.Project);
        h.Event = "PreToolUse";
        h.Matcher = "run_shell";
        h.Command = "./guard.sh";
        h.Name = "guard";
        Assert.True(d.IsDirty);
        Assert.Empty(Save(d));

        var block = Read(ProjectPath)["hooks"]!;
        Assert.Equal("./guard.sh", (string?)block["PreToolUse"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal("run_shell", (string?)block["PreToolUse"]![0]!["matcher"]);

        var again = Load();
        Assert.False(again.IsDirty);
        var loaded = Assert.Single(again.Entries);
        loaded.TimeoutSeconds = 5;
        Assert.Empty(Save(again));
        Assert.Equal(5, (int)Read(ProjectPath)["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["timeout"]!);

        var third = Load();
        third.Remove(third.Entries[0]);
        Assert.Empty(Save(third));
        Assert.Empty(Load().Entries);
    }

    [Fact]
    public void Move_to_project_writes_both_files()
    {
        File.WriteAllText(LocalPath, """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] } }""");
        var d = Load();
        d.Entries[0].Source = HookSource.Project;
        Assert.Empty(Save(d));
        Assert.Empty(Read(LocalPath)["hooks"]!.AsObject());
        Assert.Equal("true", (string?)Read(ProjectPath)["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void Unsupported_hook_survives_a_save_unchanged()
    {
        File.WriteAllText(ProjectPath, """
            { "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "http", "url": "https://h.example/x", "headers": { "A": "b" } } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "true", "statusMessage": "checking" } ] } ] } }
            """);
        var d = Load();
        d.Entries.Single(e => e.Event == "Stop").TimeoutSeconds = 9;
        Assert.Empty(Save(d));
        var block = Read(ProjectPath)["hooks"]!;
        var http = block["PreToolUse"]![0]!["hooks"]![0]!;
        Assert.Equal("https://h.example/x", (string?)http["url"]);
        Assert.Equal("b", (string?)http["headers"]!["A"]);
        Assert.Equal("checking", (string?)block["Stop"]![0]!["hooks"]![0]!["statusMessage"]);
    }

    [Fact]
    public void Config_placeholders_are_not_expanded()
    {
        File.WriteAllText(ConfigPath, """
            { "providers": { "x": { "kind": "ollama", "model": "m", "apiKey": "{env:TOKEN}" } },
              "hooks": { "defaultModel": "x" } }
            """);
        var d = Load();
        var h = d.Add(HookSource.User);
        h.Event = "Stop";
        h.Command = "true";
        Assert.Empty(Save(d));
        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("{env:TOKEN}", text);
        Assert.DoesNotContain("secret-value", text);
        var hooks = Read(ConfigPath)["hooks"]!;
        Assert.Equal("x", (string?)hooks["defaultModel"]);
        Assert.Equal("true", (string?)hooks["Stop"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void Outside_repo_project_sources_disabled()
    {
        var d = HooksFile.Load(new AppPaths(_cfg), Env, _cfg);
        Assert.Null(d.RepoRoot);
        Assert.False(d.CanSaveTo(HookSource.Project));
        Assert.True(d.CanSaveTo(HookSource.User));
    }

    [Fact]
    public void Unreadable_project_file_is_not_overwritten()
    {
        File.WriteAllText(ProjectPath, "{ broken");
        var d = Load();
        var h = d.Add(HookSource.Project);
        h.Event = "Stop";
        h.Command = "true";
        var errors = Save(d);
        Assert.NotEmpty(errors);
        Assert.Equal("{ broken", File.ReadAllText(ProjectPath));
    }

    [Fact]
    public void Untouched_files_are_not_written()
    {
        File.WriteAllText(ProjectPath, """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "true" } ] } ] } }""");
        var before = File.GetLastWriteTimeUtc(ProjectPath);
        var d = Load();
        var h = d.Add(HookSource.User);
        h.Event = "Stop";
        h.Command = "true";
        Thread.Sleep(20);
        var errors = Save(d);
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
        Assert.Equal(before, File.GetLastWriteTimeUtc(ProjectPath));
    }
}
