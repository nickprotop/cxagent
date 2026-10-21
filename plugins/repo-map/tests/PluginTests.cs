using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Plugins.RepoMap;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

internal sealed class FakeContext(string folder) : IPluginContext
{
    public List<int> Children { get; } = [];
    public string WorkingDirectory => folder;
    public JsonElement Settings => default;
    public int HostContract => 3;
    public string HostVersion => "0.0.0";
    public IPluginLogger Logger { get; } = new NullLogger();
    public CancellationToken Lifetime => CancellationToken.None;
    /// <summary>Whether each registered process was still running when it was registered — -1 for one that was not.</summary>
    public List<int> Running { get; } = [];
    public void RegisterChildProcess(int processId)
    {
        Children.Add(processId);
        try { using var p = System.Diagnostics.Process.GetProcessById(processId); Running.Add(processId); }
        catch (ArgumentException) { Running.Add(-1); }
    }

    private sealed class NullLogger : IPluginLogger { public void Log(string message) { } }
}

internal sealed class FakeJobContext(string? folder) : IJobContext
{
    public void ReportProgress(double percent, string? message = null) { }
    public string? Requester => null;
    public string? WorkingDirectory => folder;
    public void Log(string line) { }
    public void Log(JobLogLevel level, string line) { }
    public void ReportTextDelta(string delta) { }
}

public class PluginTests
{
    private static async Task<(JobResult Result, FakeContext Context)> Call(string session, string? caller, params (string Key, object? Value)[] args)
    {
        var plugin = new RepoMapPlugin();
        var context = new FakeContext(session);
        await plugin.Load(context, CancellationToken.None);
        var call = new JobParameters(args.ToDictionary(a => a.Key, a => a.Value));
        return (await plugin.Invoke("repo_map", call, new FakeJobContext(caller), CancellationToken.None), context);
    }

    private static string Content(JobResult result) => Assert.IsType<string>(result.Output["content"]);

    /// <summary>
    /// THE CALLER'S FOLDER, NOT THE SESSION'S. A sub-agent in a worktree asks for "." and means the
    /// worktree; mapping the session's folder instead would show it files it is not working on.
    /// </summary>
    [Fact]
    public async Task ADot_IsTheCallersFolder()
    {
        using var tree = new TempTree()
            .Write("Main.cs", "class MainOnly {}")
            .Write("wt/Child.cs", "class ChildOnly {}");

        var (result, _) = await Call(tree.Root, Path.Combine(tree.Root, "wt"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("ChildOnly", Content(result));
        Assert.DoesNotContain("MainOnly", Content(result));
    }

    /// <summary>NOTHING OUTSIDE THE CALLER'S FOLDER — this tool asks no permission, so it must not list what a file tool would have to ask about.</summary>
    [Fact]
    public async Task AFolderOutsideTheCallers_IsRefused()
    {
        using var tree = new TempTree().Write("in/A.cs", "class A {}").Write("out/B.cs", "class B {}");

        var (result, _) = await Call(tree.Root, Path.Combine(tree.Root, "in"), ("path", "../out"));

        Assert.False(result.Success);
        Assert.Contains("outside", result.ErrorMessage);
    }

    /// <summary>A LINK INSIDE THE FOLDER THAT POINTS OUT OF IT is judged by where it lands.</summary>
    [Fact]
    public async Task ASymlinkOutOfTheFolder_IsRefused()
    {
        using var outside = new TempTree().Write("Secret.cs", "class Secret {}");
        using var tree = new TempTree().Write("A.cs", "class A {}");
        Directory.CreateSymbolicLink(Path.Combine(tree.Root, "link"), outside.Root);

        var (result, _) = await Call(tree.Root, null, ("path", "link"));

        Assert.False(result.Success);
        Assert.Contains("outside", result.ErrorMessage);
    }

    /// <summary>
    /// A TRACKED FILE UNDER A FOLDER THAT IS NOW A LINK is judged by where it lands. git lists it
    /// from its index without looking at the disk, so the root's check alone would let it through.
    /// </summary>
    [Fact]
    public async Task ATrackedFileBehindALinkedFolder_IsNotRead()
    {
        using var outside = new TempTree().Write("secret.py", "def leaked_secret(): pass");
        using var tree = new TempTree().Write("main.py", "def main(): pass").Write("docs/secret.py", "def original(): pass");
        tree.Git("init", "-q").Git("add", "-A");
        Directory.Delete(Path.Combine(tree.Root, "docs"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(tree.Root, "docs"), outside.Root);

        var (result, _) = await Call(tree.Root, null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain("leaked_secret", Content(result));
        Assert.Contains("def main()", Content(result));
    }

    [Fact]
    public async Task AMissingFolder_IsAFailedCallTheModelCanFix()
    {
        using var tree = new TempTree();
        var (result, _) = await Call(tree.Root, null, ("path", "nope"));

        Assert.False(result.Success);
        Assert.Contains("no folder at 'nope'", result.ErrorMessage);
    }

    /// <summary>
    /// A REGISTERED git IS ONE STILL RUNNING. A process registered after it exited has nothing to
    /// reap, and the host reports it to the user as unrecordable.
    /// </summary>
    [Fact]
    public async Task OnlyARunningGit_IsRegistered()
    {
        using var tree = new TempTree().Write("A.cs", "class A {}");
        tree.Git("init", "-q");

        var (result, context) = await Call(tree.Root, null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(-1, context.Running);
    }

    [Fact]
    public async Task MaxTokens_IsClampedNotRefused()
    {
        using var tree = new TempTree().Write("A.cs", "class A {}");
        var (result, _) = await Call(tree.Root, null, ("max_tokens", 5), ("focus", "A.cs"));

        Assert.True(result.Success, result.ErrorMessage);
    }

    /// <summary>THE MANIFEST LOAD RETURNS IS THE SIDECAR, so the two cannot disagree.</summary>
    [Fact]
    public async Task Load_ReturnsTheSidecarsOneUngatedTool()
    {
        var manifest = await new RepoMapPlugin().Load(new FakeContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Equal("repo-map", manifest.Name);
        var tool = Assert.Single(manifest.Tools);
        Assert.Equal("repo_map", tool.Name);
        Assert.Equal(PluginGating.Never, tool.Gated);
        Assert.True(manifest.Spawns);
    }
}
