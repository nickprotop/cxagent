using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE EXECUTOR ACTS ON THE ENTRY THE GATE JUDGED. Each test retargets a link INSIDE the gate's own answer —
/// after the judgment, before the act — which is the one arrangement where resolving the path a second time
/// shows: the gate approved one file, and a second resolution would touch another.
///
/// <para>OPENS ACT ON THE JUDGED TARGET (read, write, a copy's source). ENTRY ACTIONS ACT ON THE ENTRY — a delete or
/// a move removes the link itself, as it always has, never the file it points at.</para>
/// </summary>
public sealed class JudgedEntryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cx-judged-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;

    public JudgedEntryTests()
    {
        _proj = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(Path.Combine(_proj, "a"));
        Directory.CreateDirectory(Path.Combine(_proj, "b"));
        File.WriteAllText(Path.Combine(_proj, "a", "f.txt"), "A");
        File.WriteAllText(Path.Combine(_proj, "b", "f.txt"), "B");
        File.WriteAllText(Path.Combine(_proj, "a", "marker"), "in-a");
        File.WriteAllText(Path.Combine(_proj, "b", "marker"), "in-b");
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_proj, "link"), Path.Combine(_proj, "a", "f.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_proj, "dirlink"), Path.Combine(_proj, "a"));
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task ARead_ReturnsTheFileTheGateJudged()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await Run("file", new() { ["action"] = "read", ["path"] = "link" }, Retarget("link", "b/f.txt"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("A", r.Output["content"]);
    }

    [Fact]
    public async Task AWrite_LandsInTheFileTheGateJudged_AndSaysWhere()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await Run("file", new() { ["action"] = "write", ["path"] = "link", ["content"] = "new" },
            Retarget("link", "b/f.txt"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_proj, "a", "f.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_proj, "b", "f.txt")));
        Assert.Contains(Path.Combine(PathResolver.RealFolder(_proj), "a", "f.txt"), (string)r.Output["content"]!);
    }

    [Fact]
    public async Task ADelete_RemovesTheLinkItself_AndTheQuestionNamesBoth()
    {
        if (OperatingSystem.IsWindows()) return;
        var asked = new List<PermissionRequest>();
        var r = await Run("file", new() { ["action"] = "delete", ["path"] = "link" }, req => { asked.Add(req); });

        Assert.True(r.Success, r.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(_proj, "link")) || new FileInfo(Path.Combine(_proj, "link")).LinkTarget is not null);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_proj, "a", "f.txt")));
        var question = Assert.Single(asked);
        Assert.Contains("link →", question.Shown);
        Assert.EndsWith(Path.Combine("a", "f.txt"), question.Shown);
    }

    [Fact]
    public async Task AMove_MovesTheLinkItself()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await Run("file", new() { ["action"] = "move", ["path"] = "link", ["dest"] = "moved" }, _ => { });

        Assert.True(r.Success, r.ErrorMessage);
        Assert.NotNull(new FileInfo(Path.Combine(_proj, "moved")).LinkTarget);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_proj, "a", "f.txt")));
    }

    [Fact]
    public async Task ACopy_WritesTheDestinationTheGateJudged()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_proj, "src.txt"), "COPIED");
        File.CreateSymbolicLink(Path.Combine(_proj, "destlink"), Path.Combine(_proj, "a", "f.txt"));

        var r = await Run("file", new() { ["action"] = "copy", ["path"] = "src.txt", ["dest"] = "destlink" },
            Retarget("destlink", "b/f.txt"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("COPIED", File.ReadAllText(Path.Combine(_proj, "a", "f.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_proj, "b", "f.txt")));
    }

    /// <summary>
    /// REMOVING SOMETHING OUTSIDE THE FOLDER ASKS, even when it points inside: a delete or a move acts on the link
    /// itself, so the link's own folder is held to the boundary as well as its target.
    /// </summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    public void AnEntryActionOnALinkOutside_Asks_ThoughItPointsInside(string action)
    {
        if (OperatingSystem.IsWindows()) return;
        var away = Path.Combine(_dir, "away");
        Directory.CreateDirectory(away);
        var link = Path.Combine(away, "lnk");
        File.CreateSymbolicLink(link, Path.Combine(_proj, "a", "f.txt"));
        var rules = new PermissionRulesStore(new AppPaths(Path.Combine(_dir, "config")));
        rules.SetTrust(_proj, TrustState.Trusted);
        var policy = new PermissionPolicy(_proj, rules, EditMode.AcceptEdits);

        var requests = PermissionPolicy.RequestsFor("file", new JobParameters(new Dictionary<string, object?>
        {
            ["action"] = action, ["path"] = link, ["dest"] = "moved",
        }), _proj);

        Assert.False(requests.All(r => policy.IsSilentlyAllowed(r with { Policy = policy })));
    }

    [Fact]
    public async Task AShellWorkingDir_IsTheFolderTheGateJudged()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await Run("shell", new() { ["command"] = "cat marker", ["working_dir"] = "dirlink", ["timeout_seconds"] = 5 },
            Retarget("dirlink", "b", directory: true));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("in-a", r.Output["stdout"]?.ToString());
    }

    // RETARGETS A LINK THE MOMENT THE GATE IS ASKED — after the request was judged, before anything acts on it.
    private Action<PermissionRequest> Retarget(string link, string to, bool directory = false) => _ =>
    {
        var at = Path.Combine(_proj, link);
        File.Delete(at);
        if (directory) Directory.CreateSymbolicLink(at, Path.Combine(_proj, to));
        else File.CreateSymbolicLink(at, Path.Combine(_proj, to));
    };

    // UNTRUSTED, so every request reaches the gate — the only place a test can step in between.
    private async Task<JobResult> Run(string tool, Dictionary<string, object?> values, Action<PermissionRequest> onAsk)
    {
        var rules = new PermissionRulesStore(new AppPaths(Path.Combine(_dir, "config")));
        var policy = new PermissionPolicy(_proj, rules);
        var registry = JobRegistry.CreateWithBuiltins(null, new StepInGate(onAsk), policy);
        registry.TryGet(tool, out var executor);
        var context = new JobContext("judged", Guid.NewGuid().ToString("N"), new Dictionary<string, JobResult>(), null)
        {
            WorkingDirectory = _proj,
            Policy = policy,
        };
        return await executor!.ExecuteAsync(new JobParameters(values), context, CancellationToken.None);
    }

    private sealed class StepInGate(Action<PermissionRequest> onAsk) : IPermissionGate
    {
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            onAsk(request);
            return Task.FromResult(PermissionOutcome.Allow);
        }
    }
}
