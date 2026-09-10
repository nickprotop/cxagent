using Xunit;

namespace CxAgent.Plugins.Triggers.Tests;

/// <summary>Waiting on a process, and what the wake carries when it ends.</summary>
public class ProcessWatchTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_command_that_succeeds_reports_its_code_and_output()
    {
        var outcome = await ProcessWatch.Run("echo hello", Generous, _ => { },
            CancellationToken.None);

        Assert.Equal(0, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
        Assert.Contains("hello", outcome.Output);
    }

    /// <summary>
    /// IT FIRES ON ANY EXIT, WHATEVER THE CODE. Firing only on success would sleep through the
    /// interesting half — a CI watch exits non-zero exactly when waking matters.
    /// </summary>
    [Fact]
    public async Task A_command_that_fails_still_reports_rather_than_erroring()
    {
        var outcome = await ProcessWatch.Run("exit 3", Generous, _ => { }, CancellationToken.None);

        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
    }

    /// <summary>
    /// ONE MERGED STREAM, as a person watching the terminal would see it: a tool that writes progress
    /// to one and its verdict to the other reads as nonsense when the two are concatenated at exit.
    /// </summary>
    [Fact]
    public async Task Stdout_and_stderr_arrive_in_one_stream()
    {
        var outcome = await ProcessWatch.Run("echo out; echo err 1>&2", Generous, _ => { },
            CancellationToken.None);

        Assert.Contains("out", outcome.Output);
        Assert.Contains("err", outcome.Output);
    }

    /// <summary>
    /// A TIMEOUT IS NOT AN EXIT, SO IT BORROWS NO CODE. Inventing -1 or GNU timeout's 124 would put a
    /// number in the wake that means nothing and could collide with a real one.
    /// </summary>
    [Fact]
    public async Task A_timeout_reports_itself_and_carries_no_exit_code()
    {
        var outcome = await ProcessWatch.Run("sleep 30", TimeSpan.FromMilliseconds(300),
            _ => { }, CancellationToken.None);

        Assert.True(outcome.TimedOut);
        Assert.Null(outcome.ExitCode);
    }

    /// <summary>
    /// THE OUTER TOKEN IS A DIFFERENT PATH FROM THE TIMEOUT, and the two must not be confused: when
    /// ct itself is cancelled (the session ending, not the wait expiring), timedOut is false and the
    /// code still reads ExitCode after killing the process — which throws InvalidOperationException
    /// if the kill has not been waited out. A watch that could not learn how its command ended must
    /// still hand back an outcome, not an unhandled throw on a task nobody awaits.
    /// </summary>
    [Fact]
    public async Task Cancelling_through_ct_rather_than_the_timeout_still_returns_an_outcome()
    {
        using var cts = new CancellationTokenSource();
        var run = ProcessWatch.Run("sleep 30", Generous, _ => { }, cts.Token);

        cts.Cancel();

        // NO THROW IS THE ASSERTION: awaiting run must not raise InvalidOperationException from an
        // ExitCode read before the kill was waited out. timedOut is false on this path — it is ct
        // that fired, not the deadline — and the killed process still has a real exit code once its
        // exit has actually been awaited, which is what makes the read safe rather than what the
        // outcome's numeric value happens to be.
        var outcome = await run;

        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task The_pid_is_registered_so_a_crashed_host_does_not_strand_it()
    {
        var seen = new List<int>();

        await ProcessWatch.Run("echo x", Generous, seen.Add, CancellationToken.None);

        Assert.Single(seen);
        Assert.True(seen[0] > 0);
    }

    /// <summary>
    /// THE TAIL SURVIVES, NOT THE HEAD. For a process someone deliberately waited on the ending IS
    /// the point — the failure, the summary, the exit. Keeping the head would preserve the banner and
    /// discard the error.
    /// </summary>
    [Fact]
    public async Task Output_past_the_cap_keeps_the_tail_and_names_the_file()
    {
        var outcome = await ProcessWatch.Run(
            $"seq 1 200000", TimeSpan.FromSeconds(60), _ => { }, CancellationToken.None);

        Assert.NotNull(outcome.Spill);
        Assert.True(File.Exists(outcome.Spill!.Path));
        Assert.True(outcome.Spill.TotalBytes > ProcessWatch.InlineCap);
        Assert.True(outcome.Output.Length <= ProcessWatch.InlineCap + 200,
            $"inline output was {outcome.Output.Length} characters");
        Assert.Contains("200000", outcome.Output);

        File.Delete(outcome.Spill.Path);
    }

    /// <summary>
    /// THE RESULT IS APPENDED, ALWAYS, AND THE PROMPT IS JUST THE PROMPT. A {output} placeholder was
    /// the first design and its failure mode is silent: a model that forgets it loses the output
    /// entirely, discovered only when a useless wake arrives hours later.
    /// </summary>
    [Fact]
    public void Composing_appends_the_code_then_the_output_under_the_prompt()
    {
        var composed = ProcessWatch.Compose("Look at what CI reported.",
            new WatchOutcome(1, false, "build failed", Spill: null));

        var lines = composed.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("Look at what CI reported.", lines[0]);
        Assert.Contains("exit 1", composed);
        Assert.Contains("build failed", composed);
        Assert.True(composed.IndexOf("exit 1", StringComparison.Ordinal)
                    < composed.IndexOf("build failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Composing_a_timeout_says_so_instead_of_an_exit_code()
    {
        var composed = ProcessWatch.Compose("Check on it.",
            new WatchOutcome(null, true, "still going", Spill: null));

        Assert.Contains("timed out", composed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exit ", composed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND IT SAYS SO EITHER WAY — the size it was and where the whole thing went. A wake that
    /// silently dropped half a log misleads about what happened, and a path nobody was told about is
    /// a file nobody reads.
    /// </summary>
    [Fact]
    public void A_spilled_composition_names_the_file_and_the_size()
    {
        var composed = ProcessWatch.Compose("Look.",
            new WatchOutcome(1, false, "the tail",
                new Spill("/tmp/cxagent-trigger-3.log", 4_404_019)));

        Assert.Contains("/tmp/cxagent-trigger-3.log", composed);
        Assert.Contains("4.2 MB", composed);
    }
}
