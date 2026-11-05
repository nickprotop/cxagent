using System.Diagnostics;
using System.Text.Json.Nodes;
using CxAgent.Core.Hooks;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Running one command hook: what its exit code and output mean, and that a hook which hangs or floods
/// cannot take the session down with it. Real bash; skipped where there is none.
/// </summary>
public class HookProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-hook-proc").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HookDefinition Hook(string command, int? timeout = null) =>
        new("PreToolUse", null, "command", HookSource.User) { Command = command, TimeoutSeconds = timeout };

    private Task<HookReply> Run(string command, int? timeout = null, JsonObject? input = null) =>
        HookProcess.RunAsync(new HookRun(Hook(command, timeout), input ?? new JsonObject { ["tool_name"] = "run_shell" }, _dir), CancellationToken.None);

    [Fact]
    public async Task Exit_zero_is_ok()
    {
        var r = await Run("echo hello");
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
        Assert.Equal("hello", r.Stdout.Trim());
    }

    [Fact]
    public async Task Exit_two_blocks_with_stderr()
    {
        var r = await Run("echo 'no rm here' >&2; exit 2");
        Assert.Equal(HookOutcomeKind.Block, r.Kind);
        Assert.Equal("no rm here", r.Reason);
    }

    [Fact]
    public async Task Other_exit_fails()
    {
        var r = await Run("echo oops >&2; exit 3");
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
        Assert.Contains("3", r.Reason);
        Assert.Contains("oops", r.Reason);
    }

    [Fact]
    public async Task Json_output_is_parsed()
    {
        var r = await Run("""echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"nope","updatedInput":{"command":"ls"},"additionalContext":"ctx"},"systemMessage":"hi"}'""");
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
        Assert.Equal("deny", r.Decision);
        Assert.Equal("nope", r.DecisionReason);
        Assert.Equal("ls", (string?)r.UpdatedInput!["command"]);
        Assert.Equal("ctx", r.AdditionalContext);
        Assert.Equal("hi", r.SystemMessage);
    }

    [Fact]
    public async Task Top_level_block_decision_is_read()
    {
        var r = await Run("""echo '{"decision":"block","reason":"tests fail"}'""");
        Assert.Equal("block", r.Decision);
        Assert.Equal("tests fail", r.DecisionReason);
    }

    [Fact]
    public async Task Permission_request_behavior_is_read()
    {
        var r = await Run("""echo '{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}'""");
        Assert.Equal("allow", r.Decision);
    }

    [Fact]
    public async Task Malformed_json_output_fails()
    {
        var r = await Run("echo '{ not json'");
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
    }

    [Fact]
    public async Task Input_arrives_on_stdin()
    {
        var r = await Run("cat", input: new JsonObject { ["tool_name"] = "read_file" });
        Assert.Contains("\"tool_name\":\"read_file\"", r.Stdout.Replace(" ", ""));
    }

    [Fact]
    public async Task Project_dir_env_set_in_both_spellings()
    {
        var r = await Run("echo \"$CXAGENT_PROJECT_DIR|$CLAUDE_PROJECT_DIR\"");
        Assert.Equal($"{_dir}|{_dir}", r.Stdout.Trim());
    }

    [Fact]
    public async Task Timeout_kills_the_process_tree()
    {
        var marker = Path.Combine(_dir, "child.pid");
        var watch = Stopwatch.StartNew();
        var r = await Run($"sleep 30 & echo $! > {marker}; sleep 30", timeout: 1);
        watch.Stop();
        Assert.Equal(HookOutcomeKind.Failed, r.Kind);
        Assert.Contains("timed out", r.Reason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        var pid = int.Parse(File.ReadAllText(marker).Trim());
        await Task.Delay(200);
        Assert.False(Alive(pid), $"child {pid} outlived the hook");
    }

    [Fact]
    public async Task Output_is_capped()
    {
        var r = await Run("head -c 1048576 /dev/zero | tr '\\0' 'a'");
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
        Assert.True(r.Stdout.Length <= HookProcess.OutputCap + 200, r.Stdout.Length.ToString());
        Assert.Contains("cut", r.Stdout[^100..]);
    }

    [Fact]
    public async Task Cancellation_stops_the_hook()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HookProcess.RunAsync(new HookRun(Hook("sleep 30"), [], _dir), cts.Token));
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task A_background_child_holding_the_pipe_does_not_hold_the_hook()
    {
        var watch = Stopwatch.StartNew();
        var r = await Run("sleep 30 & echo hi; exit 0", timeout: 20);
        watch.Stop();
        Assert.Equal(HookOutcomeKind.Ok, r.Kind);
        Assert.Equal("hi", r.Stdout.Trim());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Non_ascii_input_arrives_intact()
    {
        var r = await Run("cat", input: new JsonObject { ["path"] = "café/ναι.txt" });
        Assert.Contains("café/ναι.txt", r.Stdout);
    }
}
