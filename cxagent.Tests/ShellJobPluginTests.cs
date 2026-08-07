using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

public class ShellJobPluginTests
{
    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));

    [Fact]
    public void Validate_RejectsEmptyCommand()
    {
        var v = new ShellJobPlugin().Validate(P(("command", "")));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Validate_AcceptsNonEmptyCommand()
    {
        var v = new ShellJobPlugin().Validate(P(("command", "echo hi")));
        Assert.True(v.IsValid);
    }

    [Fact]
    public async Task Execute_EchoSucceeds_WithExitCodeZero()
    {
        var result = await new ShellJobPlugin().ExecuteAsync(
            P(("command", "echo hi")), new CollectingContext(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Execute_CapturesStdout_SoTheNextJobCanReferenceIt()
    {
        // The bag used to hold ONLY exit_code, so {{some_shell_job.stdout}} could never resolve and
        // any goal that shelled out and fed the result onward failed. Measured on a live drive of
        // "list ~/bin. what it does?": the job succeeded, the reference produced nothing, and the
        // model reported the directory EMPTY — it had six scripts.
        var result = await new ShellJobPlugin().ExecuteAsync(
            P(("command", "echo hello-from-stdout")), new CollectingContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hello-from-stdout", result.Output!["stdout"]!.ToString()!);

        // `content` mirrors stdout: it is the key JobDigest renders BARE and that {{job}} resolves to
        // as shorthand, and a shell job's substance IS its output.
        Assert.Contains("hello-from-stdout", result.Output["content"]!.ToString()!);
    }

    [Fact]
    public async Task Execute_CapturesStderr_SeparatelyFromStdout()
    {
        // Separate keys so a job can reference either — diagnostics must not be silently mixed into
        // the text a downstream job treats as the result.
        var result = await new ShellJobPlugin().ExecuteAsync(
            P(("command", "echo oops >&2")), new CollectingContext(), CancellationToken.None);

        Assert.Contains("oops", result.Output!["stderr"]!.ToString()!);
        Assert.DoesNotContain("oops", result.Output["stdout"]!.ToString()!);
    }

    [Fact]
    public async Task Execute_NonZeroExit_Fails()
    {
        var result = await new ShellJobPlugin().ExecuteAsync(
            P(("command", "exit 2")), new CollectingContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Execute_Timeout_FailsWithTimedOut()
    {
        var result = await new ShellJobPlugin().ExecuteAsync(
            P(("command", "sleep 30"), ("timeout_seconds", 1)), new CollectingContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeName_IsShell()
    {
        Assert.Equal("shell", new ShellJobPlugin().TypeName);
    }

    [Fact]
    public async Task Shell_CommandThatReadsStdinFailsFastInsteadOfHanging()
    {
        // Unredirected, the child INHERITS the TUI's stdin: `git commit` opens $EDITOR, `apt` waits
        // on y/n, and the command blocks until the timeout while stealing the user's keystrokes.
        // Closed, the read returns EOF and the command finishes in milliseconds.
        var plugin = new ShellJobPlugin();
        var p = new JobParameters(new Dictionary<string, object?>
        {
            ["command"] = "cat",              // reads stdin until EOF
            ["timeout_seconds"] = 10,
        });

        var start = DateTimeOffset.UtcNow;
        var r = await plugin.ExecuteAsync(p, new CollectingContext(), CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(r.Success, r.ErrorMessage);
        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"`cat` should hit EOF immediately, took {elapsed.TotalSeconds:N1}s");
    }

    [Fact]
    public async Task Shell_RunsWithNoTimeoutParameterSupplied()
    {
        // timeout_seconds is optional and a model almost never sends it. With no default,
        // ProcessRunner built a CancellationTokenSource that was never scheduled to fire, so a
        // blocking command waited forever with nothing able to interrupt it.
        var plugin = new ShellJobPlugin();
        var p = new JobParameters(new Dictionary<string, object?> { ["command"] = "echo hi" });

        var r = await plugin.ExecuteAsync(p, new CollectingContext(), CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
    }
}
