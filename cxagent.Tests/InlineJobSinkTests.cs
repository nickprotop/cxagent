using System.Globalization;
using CxAgent.Core.Agents;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// USER-REPORTED: "why jobs aren't updated the text receiving from the model?"
///
/// <para>A <c>ToolUpdated</c> that only calls <c>SetStatus</c> leaves the message BODY as whatever
/// <c>Title(job)</c> produced when the job first appeared, i.e. the job's NAME. Every executor writes
/// its real output to <c>Output["content"]</c> (LlmAgentJobPlugin's worker transcript,
/// ShellJobExecutor's stdout), and if the only reader is IntrospectionTools — the tool the
/// ORCHESTRATOR uses — the user cannot see what their own workers said.</para>
///
/// <para>Every behaviour below is invisible from the executor side, so nothing else in the suite
/// notices when it breaks; the sink is only observable through these.</para>
/// </summary>
public class InlineJobSinkTests
{
    private static Job JobWith(JobState state, Dictionary<string, object?>? output = null,
        string? error = null, double seconds = 1.0) =>
        new()
        {
            Id = "j1",
            AgentId = "g1",
            JobType = "llm_agent",
            DisplayName = "read the RFC files",
            State = state,
            Result = new JobResult
            {
                Success = state == JobState.Succeeded,
                Output = output ?? new(),
                ErrorMessage = error,
                Duration = TimeSpan.FromSeconds(seconds),
            },
        };

    // --- Terminal escapes must not reach the transcript ------------------------------------------

    // A live session ran a binary it had just built (`cxgpu --gpu-usage --color`) and the 24-bit
    // colour codes came back in the tool result and smeared the UI. The library's per-rune sanitizer
    // replaced the ESC and left the rest as literal text, so the transcript filled with
    // "[38;2;78;205;196m" — the guard held and the output was still unreadable.
    [Fact]
    public void BodyFor_Display_StripsTerminalEscapesFromCommandOutput()
    {
        const string esc = "\u001b";
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = $"{esc}[38;2;78;205;196m 45.2%{esc}[0m",
        });

        var body = InlineJobSink.BodyFor(job, forDisplay: true);

        Assert.Equal(" 45.2%", body);
        Assert.DoesNotContain("38;2;78", body!);
    }

    // AND THE MEASURING PATH IS NOT THE RENDERER. forDisplay:false feeds row sizing and the
    // introspection tools; stripping there would make the two disagree about what came back.
    [Fact]
    public void BodyFor_NotForDisplay_LeavesTheOutputAlone()
    {
        const string esc = "\u001b";
        var raw = $"{esc}[31mred{esc}[0m";
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?> { ["content"] = raw });

        Assert.Equal(raw, InlineJobSink.BodyFor(job, forDisplay: false));
    }

    // --- The reported bug: the model's text must reach the body ---------------------------------

    [Fact]
    public void BodyFor_ASucceededJob_IsTheModelsOutput_NotTheJobName()
    {
        // The whole report. Before the fix this content existed on the job and was never rendered.
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "Found 4 files. Rfc2047.cs defines DecodePhrase/DecodeText.",
        });

        var body = InlineJobSink.BodyFor(job);

        Assert.NotNull(body);
        Assert.Contains("Rfc2047.cs", body);
        Assert.DoesNotContain("read the RFC files", body!);   // the NAME belongs in the header, not here
    }

    [Fact]
    public void BodyFor_SuppressesTheEnvelopeKeys_TheOrchestratorNeedsButAHumanDoesNot()
    {
        // Deliberately NOT JobDigest.RenderOutput, which flattens every key as "key: value" — that
        // shape exists for the ORCHESTRATOR (it needs exit_code and truncated to decide what to do
        // next). A human wants the answer; the envelope is already on the status row.
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "hello from the shell",
            ["exit_code"] = 0,
            ["truncated"] = false,
        });

        var body = InlineJobSink.BodyFor(job);

        Assert.Equal("hello from the shell", body);
        Assert.DoesNotContain("exit_code", body!);
    }

    [Fact]
    public void BodyFor_AFailedJob_IsTheERROR_BecauseThatIsWhatTheUserActsOn()
    {
        // The status row truncates a stack trace or multi-line stderr into unreadability, and the
        // error is the whole reason the user is looking at a failed job.
        var job = JobWith(JobState.Failed, error: "sh: line 1: frobnicate: command not found");

        var body = InlineJobSink.BodyFor(job);

        Assert.Contains("command not found", body);
    }

    [Fact]
    public void BodyFor_NothingToShow_ReturnsNull_SoTheTitleSurvives()
    {
        // Returning "" would blank the message and lose the job's name from the screen entirely.
        Assert.Null(InlineJobSink.BodyFor(JobWith(JobState.Succeeded)));
        Assert.Null(InlineJobSink.BodyFor(JobWith(JobState.Succeeded,
            new Dictionary<string, object?> { ["content"] = "   " })));
    }

    [Fact]
    public void Clip_NoLongerTruncates_TheWholeOutputReachesTheBody()
    {
        // The row is COLLAPSED by default, so length costs nothing until the user opens it -- and a
        // body capped at 4,000 chars leaves the full text only in the log file, which nobody reads.
        var huge = new string('x', 50_000);

        var result = InlineJobSink.Clip(huge);

        Assert.Equal(huge.Length, result.Length);
        Assert.DoesNotContain("showing first", result);
    }

    [Fact]
    public void Clip_LeavesAShortBodyExactlyAlone()
    {
        Assert.Equal("short", InlineJobSink.Clip("short"));
    }

    // --- Compact rows: driven by "is there anything to read", not by executor type ------------------

    private static Job TypedJob(string jobType, JobState state,
        Dictionary<string, object?>? output = null) =>
        new()
        {
            Id = "j1", AgentId = "g1", JobType = jobType, DisplayName = "step", State = state,
            Result = new JobResult
            {
                Success = state == JobState.Succeeded, Output = output ?? new(), Duration = TimeSpan.Zero,
            },
        };

    [Fact]
    public void AJobThatProducedNOTHING_RendersCompact_WhateverItsType()
    {
        // From a live screenshot: five rows reading "Read HeuristicEngine.c...  expand..." at 0.0s,
        // each offering to expand into nothing. The FIRST attempt at this compacted by executor type
        // and could not see them -- the orchestrator plans an llm_agent for EVERYTHING, so "Read
        // HeuristicEngine.cs" is a WORKER that calls read_file internally, not a `file` job.
        Assert.True(InlineJobSink.IsCompactRowForTest(TypedJob("llm_agent", JobState.Succeeded)));
        Assert.True(InlineJobSink.IsCompactRowForTest(TypedJob("file", JobState.Succeeded)));
    }

    [Fact]
    public void AWORKERSSubstantialOutput_KeepsItsFullBlock()
    {
        // Supersedes AJobWithSUBSTANTIALOutput_KeepsItsFullBlock_WhateverItsType. "Whatever its
        // type" was wrong, and the user found it by looking at a real transcript: a TOOL reading
        // Base64Decoder.cs pasted 12KB of source into the conversation behind an "expand...".
        // Bulky TOOL output is an echo of something already on disk and is now summarised; bulky
        // WORKER output is the prose it composed, and keeps its block. The type IS the axis here --
        // just not the axis my earlier rule used it for.
        var job = TypedJob("llm_agent", JobState.Succeeded,
            new Dictionary<string, object?>
            {
                ["content"] = "public class Calc { public int Add(int a, int b) => a + b; }\n"
                            + "// plus enough more text that folding it into a row would be unreadable",
            });

        Assert.False(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void AFAILEDTOOL_IsCompact_HeaderPlusTheReason()
    {
        // A failed job gets no block exemption: there are no Retry/Skip/Diagnose buttons to reserve
        // a footer for, so exempting it spends four lines to say one thing -- header ending
        // "failed - 0.0s", the error, a full-width rule, then "failed - 0.0s" AGAIN. Measured live on
        // a missing file.
        //
        // Use a `file` job, not an llm_agent: a worker is exempt as a WORKER whatever its state, so an
        // llm_agent here would pass without ever exercising the failure rule.
        Assert.True(InlineJobSink.IsCompactRowForTest(
            TypedJob("file", JobState.Failed, new Dictionary<string, object?>())));
    }

    [Fact]
    public void AFAILEDWORKER_KeepsItsBlock_LikeAnySucceedingWorker()
    {
        // The worker exemption is about WHOSE OUTPUT IT IS, not about success: a worker's prose is
        // the answer that was asked for either way. Pinned so dropping the failure exemption above
        // cannot quietly compact a worker too.
        var job = TypedJob("llm_agent", JobState.Failed,
            new Dictionary<string, object?>
            {
                ["content"] = "I got partway through and then hit a wall.\n"
                            + "Here is what I learned before stopping, at length.",
            });

        Assert.False(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void AFailedRowStatesTheReason_NotItsSize()
    {
        // The size summary answers "how much came back", which is right for a bulky success and
        // meaningless for an error: a missing file rendered as "1 lines, 68 chars", measuring the
        // reason instead of stating it. The FIRST line carries the cause; stack frames and stderr
        // tails stay in the expandable body.
        var job = TypedJob("file", JobState.Failed, new Dictionary<string, object?>());
        job = job with
        {
            Result = job.Result! with
            {
                ErrorMessage = "error: Could not find file '/tmp/nope.txt'.\n   at Frame.One()",
            },
        };

        var row = InlineJobSink.OneLineRowForTest(job);

        Assert.NotNull(row);
        Assert.Contains("Could not find file", row!, StringComparison.Ordinal);
        Assert.DoesNotContain("chars", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame.One", row, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedJobsStatusRow_DoesNotRepeatTheErrorTheBodyAlreadyShows()
    {
        // From a live screenshot: the same sentence printed twice, stacked --
        //     Could not find file '/home/nick/source/cxlog/package.json'.
        //     failed - 0.0s - Could not find file '/home/nick/source/cxlog/package.json'.
        // The BODY is the better home (it wraps, holds stderr, and sits directly above the
        // Retry/Skip/Diagnose buttons); the status row carries state and duration.
        var job = JobWith(JobState.Failed, error: "Could not find file 'package.json'.");

        var status = InlineJobSink.StatusTextForTest(job);

        Assert.DoesNotContain("Could not find file", status);
        Assert.Contains("failed", status);
        Assert.Contains("Could not find file", InlineJobSink.BodyFor(job));
    }

    // --- One-line tool rows (research: .superpowers/sdd/tool-ui-research.md) ----------------------

    [Theory]
    [InlineData("20")]                    // measured live: a whole tool result, 2 chars
    [InlineData("README.md exists")]      // measured live, 16 chars
    [InlineData("MIT License")]           // measured live, 11 chars
    public void AShortSingleLineResult_FoldsIntoOneRow(string content)
    {
        // Measured on a live drive against a real repo: six of seven tool bodies were under 30
        // characters, and EVERY one cost five lines -- header, body, a full-width rule, a status row
        // and a blank -- the same as an 800-character code review.
        //
        // The dominant pattern across terminal agents is a one-line call with its result folded in
        // (Claude Code's ⏺/⎿ pair; Gemini CLI's compact single-line mode). Nobody spends a bordered
        // block on a file read.
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?> { ["content"] = content });

        Assert.True(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void ALONGResult_KeepsItsExpandableBlock()
    {
        // The converse, and the reason this is a LENGTH rule rather than a type rule: a worker's
        // prose is the thing the user asked for and must not be folded into a row.
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = new string('x', 400),
        });

        Assert.False(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void AMULTILINEResult_KeepsItsBlock_EvenWhenShort()
    {
        // Multi-line output is real output regardless of character count -- folding it would either
        // lose lines or wrap unreadably into the row.
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "a.cs\nb.cs\nc.cs",
        });

        Assert.False(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void AWorkersHeader_NamesTheROLEWhenThePlanSpecifiedOne()
    {
        // "Worker · reviewer" says WHICH of the four built-ins ran -- the difference between "a
        // model looked at this" and "the reviewer looked at this". The role is a job PARAMETER, not
        // a field on Job, so it is read defensively: JobParameters.Get<T> indexes and throws on a
        // missing key, and a header must never kill a render.
        var withRole = new Job
        {
            Id = "j1", AgentId = "g1", JobType = "llm_agent", DisplayName = "review it",
            State = JobState.Succeeded,
            Parameters = new JobParameters(new Dictionary<string, object?> { ["role"] = "reviewer" }),
        };

        Assert.Equal("Worker · reviewer", InlineJobSink.AuthorForTest(withRole));
    }

    [Fact]
    public void AWorkerWithNoRole_IsJustWorker_NotAnEmptySuffix()
    {
        var noRole = new Job
        {
            Id = "j1", AgentId = "g1", JobType = "llm_agent", DisplayName = "do it",
            State = JobState.Succeeded,
        };

        Assert.Equal("Worker", InlineJobSink.AuthorForTest(noRole));
    }

    [Fact]
    public void AMechanicalStep_IsLabelledTool_WhateverElseIsTrue()
    {
        Assert.Equal("Tool", InlineJobSink.AuthorForTest(TypedJob("shell", JobState.Succeeded)));
    }

    [Fact]
    public void ATOOLThatDumpsAWholeFile_IsSUMMARISED_NotPastedIntoTheTranscript()
    {
        // The user, looking at a real transcript: "I see the tool call, with header, separator and
        // result." That row was a read of Base64Decoder.cs -- 12KB of source pasted into the
        // conversation behind an "expand...". Nobody wants a file they already have on disk echoed
        // back; they want to know it was READ.
        //
        // My first rule treated "lots of content" as "worth showing", which is exactly backwards
        // for a tool. Every project surveyed converges on the same fix: show the command and a
        // one-line summary, not the payload (Claude Code #26968 on 1,000+ line MCP dumps; Pi #3114
        // on 100+ line JSON "filling up the terminal screen").
        var wholeFile = string.Join("\n", Enumerable.Repeat("public class X { }", 300));
        var job = TypedJob("file", JobState.Succeeded,
            new Dictionary<string, object?> { ["content"] = wholeFile });

        Assert.True(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void AWORKERSLongProse_KEEPSItsBlock_BecauseItIsTheAnswer()
    {
        // The exemption that makes the tool/worker split meaningful. A worker's long output is the
        // prose it composed -- the thing the user asked for -- not an echo of something on disk.
        var review = string.Join("\n", Enumerable.Repeat("A concrete defect with a line reference.", 40));
        var job = TypedJob("llm_agent", JobState.Succeeded,
            new Dictionary<string, object?> { ["content"] = review });

        Assert.False(InlineJobSink.IsCompactRowForTest(job));
    }

    [Fact]
    public void TheSizeSummary_CountsTheRAWOutput_NotMyOwnTruncation()
    {
        // Found by driving a 5-way fan-out against real MimeKit files. Every row read "4,04x chars"
        // regardless of which file it was -- because BodyFor CLIPS to MaxBodyChars before returning,
        // and the summary counted the clipped text. A 986-line, 36,666-char file was announced as
        // "96 lines, 4,045 chars": the constant size of 4,000 chars of C# plus an elision marker.
        //
        // Worse than cosmetic. It is the ONE number telling the user how much came back, and it was
        // silently reporting a constant.
        var big = string.Join("\n", Enumerable.Repeat("public class Filler { int x; }", 900));
        var job = TypedJob("file", JobState.Succeeded,
            new Dictionary<string, object?> { ["content"] = big });

        var row = InlineJobSink.OneLineRowForTest(job);

        Assert.NotNull(row);
        Assert.Contains("900 lines", row);                    // the REAL line count
        Assert.DoesNotContain("4,000 chars", row!);           // never a clip size
    }

    [Fact]
    public void AFinishedWorkersHeader_LosesTheSpinner()
    {
        // Seen live on a five-way fan-out: five tools showing a check beside workers STILL SPINNING
        // after they had finished and collapsed. SetHeader was only called on the compact branch,
        // and a streaming worker is switched OUT of compact mode on its first delta -- so it kept
        // the spinner it was given while pending, forever.
        var job = TypedJob("llm_agent", JobState.Succeeded,
            new Dictionary<string, object?> { ["content"] = "A review with real findings." });

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.DoesNotContain("[spinner", header);
        Assert.Contains("done", header);
    }

    [Fact]
    public void ARunningJobsHeader_HASTheSpinner()
    {
        // The converse -- the guard against "fixing" the above by dropping the spinner entirely.
        Assert.Contains("[spinner",
            InlineJobSink.CompactHeaderForTest(TypedJob("llm_agent", JobState.Running)));
    }

    // --- Planner visibility ----------------------------------------------------------------------



    [Fact]
    public void CompletedRowsRecede_ButFailuresDoNot()
    {
        // A screen of twenty finished tool calls is twenty things shouting equally, and the one row
        // still running is lost among them. Muting finished work is the single mechanic that makes a
        // long session readable: completed rows drop to textMuted while active rows hold theme.text.
        var done = InlineJobSink.CompactHeaderForTest(TypedJob("file", JobState.Succeeded));
        Assert.Contains(CxAgent.UI.ColorScheme.MutedMarkup, done, StringComparison.Ordinal);

        // A FAILURE is the one finished row the user still has to act on. Muting it would hide the
        // thing most worth seeing.
        var failed = InlineJobSink.CompactHeaderForTest(TypedJob("file", JobState.Failed));
        Assert.DoesNotContain(CxAgent.UI.ColorScheme.MutedMarkup, failed, StringComparison.Ordinal);

        // A RUNNING row keeps full weight and its spinner.
        var running = InlineJobSink.CompactHeaderForTest(TypedJob("file", JobState.Running));
        Assert.DoesNotContain(CxAgent.UI.ColorScheme.MutedMarkup, running, StringComparison.Ordinal);
        Assert.Contains("[spinner", running, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisplayNameContainingBracketsIsEscaped()
    {
        // Single-agent rows are tool calls with raw JSON arguments — `read_file {"path":"/x"}` —
        // which routinely contain '['. Inside the colour scope a completed row now carries, an
        // unescaped bracket is a tag the parser tries to read, and the row renders as an EMPTY LINE
        // rather than erroring: silent, and the information is simply gone.
        var job = TypedJob("file", JobState.Succeeded) with
        {
            DisplayName = "read_file {\"path\":\"/x\"} [not-a-tag]",
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.Contains("[[not-a-tag]", header, StringComparison.Ordinal);
    }

    // ---- a finished spawn ------------------------------------------------------------------------

    /// <summary>
    /// A SPAWN'S ENVELOPE IS NOT A RESULT LINE.
    ///
    /// <para>Seen in a screenshot: a finished spawn rendered its envelope's opening tag beneath the
    /// row, so the user read an id and <c>state="completed"</c> back at a header that already said
    /// <c>done · 144.6s</c>. Two lines, one fact, and the noisier of the two was the machine's.</para>
    ///
    /// <para>Keyed on the ENVELOPE rather than on JobType: llm_agent is the row type and covers
    /// every worker, and suppressing all of them broke short tool results, which legitimately fold
    /// their whole output into this line.</para>
    /// </summary>
    [Fact]
    public void AFinishedSpawn_HasNoResultLine()
    {
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"01KZ\" state=\"completed\">\nThe repo has three layers.\n</sub_agent>",
        });

        Assert.Null(InlineJobSink.OneLineRowForTest(job));
    }

    /// <summary>A short TOOL result still folds into one line — the guard above must not reach it.</summary>
    [Fact]
    public void AShortToolResult_StillFoldsIntoOneLine()
    {
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?> { ["content"] = "20" });

        Assert.Contains("20", InlineJobSink.OneLineRowForTest(job) ?? "", StringComparison.Ordinal);
    }

    // ---- who decided, on the row (Task 8) ---------------------------------------------------------

    [Fact]
    public void AnAutoApprovedTool_BadgesTheRow()
    {
        // The two verdicts a classifier can give under MayApprove let a call through, or end it,
        // without the user ever seeing a prompt — those are the surprising outcomes, so the row
        // names which one happened. Job.DecidedBy carries it from PermissionOutcome.AutoAllow.
        //
        // ON THE JOB, NOT ON ITS RESULT, and that is not a spelling change: a JobResult exists only
        // once the call has FINISHED, so a badge sourced there cannot appear while the tool runs.
        var job = TypedJob("shell", JobState.Succeeded) with
        {
            DecidedBy = "auto",
            Result = new JobResult { Success = true, Duration = TimeSpan.Zero },
        };

        Assert.Contains("auto-approved", InlineJobSink.CompactHeaderForTest(job));
    }

    [Fact]
    public void TheBadgeIsSeparatedFromTheRestOfTheHeaderExactlyOnce()
    {
        // FOUND ON A LIVE DRIVE, not by the test above, which only asks whether the WORD appears.
        // CompactHeader appends a "  ·  " after the badge, so a Badge() that carries its own leading
        // one renders "· · auto-approved · done · 25.0s" — a doubled separator on every auto
        // decision, invisible to an assertion that only greps for the word.
        var job = TypedJob("shell", JobState.Succeeded) with
        {
            DecidedBy = "auto",
            Result = new JobResult { Success = true, Duration = TimeSpan.Zero },
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        // ONE separator before the badge and ONE after it. Asserting only that the WORD appears
        // (the test above) passes on a row rendering "·  ·  auto-approved", which is what a live
        // drive actually showed. Counting the separators is what pins the shape.
        Assert.Equal(3, header.Split('·').Length - 1);
        Assert.Contains("·  auto-approved  ·", header);
    }

    [Fact]
    public void ARunningRowSeparatesItsBadgeToo()
    {
        // THE OTHER BRANCH, and it renders from a different expression. A running row has no state or
        // duration after the badge, so it has no "  ·  " chain to slot into and must supply the
        // separator itself. Move the separator out of Badge() to fix the doubled one on FINISHED rows
        // and running rows silently read "name auto-approved" with no separator at all — reported
        // from a live drive.
        var job = TypedJob("shell", JobState.Running) with { DecidedBy = "auto" };

        Assert.Contains("·  auto-approved", InlineJobSink.CompactHeaderForTest(job));
    }

    [Fact]
    public void AnAutoDeniedTool_BadgesTheRow()
    {
        var job = TypedJob("shell", JobState.Failed) with
        {
            DecidedBy = "auto",
            Result = new JobResult
            {
                Success = false, Duration = TimeSpan.Zero,
                PermissionDenied = true, ErrorMessage = "auto review refused this.",
            },
        };

        Assert.Contains("auto-denied", InlineJobSink.CompactHeaderForTest(job));
    }

    [Fact]
    public void AReviewingJob_ShowsTheWordWhilePending()
    {
        // The gap between the row appearing and the verdict landing — the classifier can take many
        // seconds, and until this existed the row showed nothing there at all, indistinguishable
        // from a hung tool.
        var job = TypedJob("shell", JobState.Running) with { Reviewing = true };

        Assert.Contains("reviewing…", InlineJobSink.CompactHeaderForTest(job));
    }

    [Fact]
    public void AVerdictReplaces_NotAppendsTo_TheReviewingWord()
    {
        // Once DecidedBy is set the classifier HAS ruled, so "reviewing…" must be gone, not merely
        // joined by "auto-approved" — Badge() prefers DecidedBy and this is what pins that ordering.
        var job = TypedJob("shell", JobState.Running) with { Reviewing = true, DecidedBy = "auto" };

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.Contains("auto-approved", header);
        Assert.DoesNotContain("reviewing…", header);
    }

    [Fact]
    public void AnOrdinaryRunningRow_NeverShowsReviewing()
    {
        // The control against a lazy "always show reviewing" fix: a stored rule or a silent
        // in-boundary pass never sets Reviewing at all, and this pins that the row stays silent
        // about it — not merely that a badged row eventually clears it.
        var job = TypedJob("shell", JobState.Running);

        Assert.DoesNotContain("reviewing…", InlineJobSink.CompactHeaderForTest(job));
    }

    [Fact]
    public void ARunningRow_ShowsElapsedTimeAtTheEnd()
    {
        var job = TypedJob("shell", JobState.Running) with
        {
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(34),
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        // hh:mm:ss — loosely matched on seconds since the clock advances between stamping StartedAt
        // above and rendering here; 00:00:3 covers the one-second slop without pinning an exact tick.
        Assert.Matches(@"00:00:3\d$", header.TrimEnd());
    }

    [Fact]
    public void AShortFinishedDuration_KeepsTheTenthsFormat()
    {
        // UNCHANGED FORMAT, on purpose: short durations are compared at a glance between neighbouring
        // rows ("2.0s" vs "5.0s"), and that habit must survive the long-duration fix below.
        var job = TypedJob("shell", JobState.Succeeded) with
        {
            Result = new JobResult { Success = true, Duration = TimeSpan.FromSeconds(2) },
        };

        Assert.Contains("2.0s", InlineJobSink.CompactHeaderForTest(job));
    }

    [Fact]
    public void ALongFinishedDuration_RendersAsAClock()
    {
        // The defect this replaces: an eleven-minute build rendered "660.0s", unreadable without
        // doing the division in your head. Past a minute the row switches to hh:mm:ss instead.
        var job = TypedJob("shell", JobState.Succeeded) with
        {
            Result = new JobResult { Success = true, Duration = TimeSpan.FromMinutes(11) },
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.Contains("00:11:00", header);
        Assert.DoesNotContain("660.0s", header);
    }

    [Fact]
    public void AUserAnsweredPrompt_GetsNoBadge()
    {
        // The ordinary case: the user was right there and answered. Naming that would be noise,
        // not information — DecidedBy is null on every path a human, not a classifier, decided.
        var job = TypedJob("shell", JobState.Succeeded);

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.DoesNotContain("auto-approved", header);
        Assert.DoesNotContain("auto-denied", header);
    }

    [Fact]
    public void AUserDeniedPrompt_GetsNoBadge()
    {
        // DecidedBy is "user" here (PermissionOutcome.ByUser), never "auto" — a human said no in
        // front of the prompt, which is not a fact the row needs to repeat.
        var job = TypedJob("shell", JobState.Failed) with
        {
            DecidedBy = "user",
            Result = new JobResult
            {
                Success = false, Duration = TimeSpan.Zero,
                PermissionDenied = true, ErrorMessage = "permission denied by the user.",
            },
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.DoesNotContain("auto-approved", header);
        Assert.DoesNotContain("auto-denied", header);
    }

    [Fact]
    public void ASilentRuleDrivenPass_GetsNoBadge()
    {
        // Trusting a folder or a stored "Always" rule IS what silent means — badging it would
        // contradict the very promise those two mechanisms make.
        var job = TypedJob("file", JobState.Succeeded) with
        {
            DecidedBy = null,
            Result = new JobResult { Success = true, Duration = TimeSpan.Zero },
        };

        Assert.DoesNotContain("auto-approved", InlineJobSink.CompactHeaderForTest(job));
    }

    // ---- the running clock actually ticks -------------------------------------------------------

    /// <summary>
    /// Builds a headless window system and a transcript, with no <c>Run()</c> loop.
    ///
    /// <para>Nothing drains the UI-thread queue in a unit test, which is precisely why the sink's
    /// two paths under test here have synchronous <c>…Now</c> halves — see
    /// <c>InlineJobSink.RefreshRunningHeadersNow</c>.</para>
    /// </summary>
    private static (InlineJobSink Sink, ChatTranscriptControl Chat) Headless()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var chat = new ChatTranscriptControl();
        return (new InlineJobSink(system, chat), chat);
    }

    private static Job RunningRow(string id, DateTimeOffset startedAt) => new()
    {
        Id = id, AgentId = "g1", JobType = "shell", DisplayName = "run_shell",
        State = JobState.Running, CreatedAt = startedAt, StartedAt = startedAt,
    };

    /// <summary>
    /// THE TICK REWRITES A RUNNING ROW'S HEADER, AND THE NUMBER IN IT MOVES.
    ///
    /// <para>USER-REPORTED: the elapsed clock beside a running tool was frozen. The header is a pure
    /// projection of the Job, so it was always CORRECT — it was simply computed once, when the row
    /// was last pushed, and a plain shell call pushes exactly one header between its start and its
    /// result. The comment in the code claimed the inline <c>[spinner]</c> re-evaluated the whole
    /// header on its own interval; it does not. That tag is resolved out of the STORED string when
    /// the control repaints, so it animates a header nobody recomputed.</para>
    ///
    /// <para>This test does what no test did before: it puts a row on screen, waits real time, ticks,
    /// and demands a DIFFERENT header. A test written against the pure projection alone cannot fail
    /// on this bug, which is how it shipped.</para>
    /// </summary>
    [Fact]
    public async Task TheTick_RewritesARunningRowsHeader_WithAFreshClock()
    {
        var (sink, _) = Headless();

        // A second and change back, so the hh:mm:ss field is guaranteed to differ after the wait
        // below — the format has no sub-second digit, and a start "now" could otherwise tick from
        // 00:00:00 to 00:00:01 or not, depending on where in the second the test landed.
        var job = RunningRow("j1", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5));
        sink.ToolsChangedNow(new[] { job });

        var before = InlineJobSink.CompactHeaderForTest(job);

        await Task.Delay(1100);

        Assert.Equal(1, sink.RefreshRunningHeadersNow());

        var after = InlineJobSink.CompactHeaderForTest(job);
        Assert.NotEqual(before, after);

        // AND IT WENT UP, not merely changed: an "is different" assertion alone would pass on a
        // clock that reset to zero every tick, which is a different frozen clock wearing a disguise.
        Assert.Contains("00:00:05", before);
        Assert.Contains("00:00:06", after);
    }

    /// <summary>
    /// THE TICK DOES NOT RE-OPEN A ROW THE USER COLLAPSED.
    ///
    /// <para>The reason the tick is a bare SetHeader rather than a call to ToolUpdated, which
    /// force-expands the row and blanks its body on every invocation. Once a second, that would take
    /// a row the user deliberately shut and open it again, forever, and the user would have no way to
    /// win. This is the same invariant ToolProgressed exists to protect, and the tick is a second
    /// caller of it.</para>
    /// </summary>
    [Fact]
    public void TheTick_LeavesACollapsedRowCollapsed()
    {
        var (sink, chat) = Headless();

        var job = RunningRow("j1", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5));
        sink.ToolsChangedNow(new[] { job });

        var id = Assert.Single(chat.MessageIds);
        chat.SetExpanded(id, false);   // the user shuts it

        sink.RefreshRunningHeadersNow();

        Assert.False(chat.IsExpanded(id), "the tick re-opened a row the user had collapsed");
    }

    /// <summary>
    /// A FINISHED ROW IS NOT TOUCHED BY THE TICK. Its header carries a fixed duration off JobResult,
    /// so rewriting it once a second is churn for a string that cannot change — and the count is the
    /// only way to see the difference, since the header would come out identical either way.
    /// </summary>
    [Fact]
    public void TheTick_SkipsFinishedRows()
    {
        var (sink, _) = Headless();

        var done = RunningRow("j1", DateTimeOffset.UtcNow) with
        {
            State = JobState.Succeeded,
            Result = new JobResult { Success = true, Duration = TimeSpan.FromSeconds(2) },
        };
        sink.ToolsChangedNow(new[] { done });

        Assert.Equal(0, sink.RefreshRunningHeadersNow());
    }

    // ---- the clock does not count the review phase ----------------------------------------------

    /// <summary>
    /// NO CLOCK WHILE THE GATE IS STILL DECIDING.
    ///
    /// <para>Work has not started, so there is no runtime to report, and every alternative is worse:
    /// a live count would be exactly the review time that Agent's rebase just removed from the
    /// finished row's duration, putting the two numbers back into disagreement; a frozen 00:00:00
    /// reads as a hung clock, which is the complaint that started this. The badge in the same slot
    /// already says "reviewing…", so the row explains its own silence.</para>
    /// </summary>
    [Fact]
    public void ARowStillUnderReview_ShowsNoClock()
    {
        var job = RunningRow("j1", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(8)) with
        {
            Reviewing = true,
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.Contains("reviewing…", header);
        Assert.DoesNotContain("00:00:", header);
    }

    /// <summary>
    /// AND THE CLOCK APPEARS THE MOMENT THE VERDICT LANDS. The suppression above is scoped to the
    /// review window alone — it must not be a way for a row to lose its clock permanently, which
    /// would be the frozen-clock bug again by another route.
    /// </summary>
    [Fact]
    public void OnceTheVerdictLands_TheClockAppears()
    {
        var job = RunningRow("j1", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(8)) with
        {
            Reviewing = false, DecidedBy = "auto",
        };

        var header = InlineJobSink.CompactHeaderForTest(job);

        Assert.Contains("auto-approved", header);
        Assert.Contains("00:00:0", header);
    }

    // ---- the worker timetable --------------------------------------------------------------------

    /// <summary>
    /// A finished worker's body is WHAT IT DID, not what it said about doing it.
    ///
    /// <para>The prose an executor writes into <c>Output["content"]</c> is a narration, and on a
    /// long run it is enormous — the expanded row became a wall of text nobody reads. The tool calls
    /// are the record of the work: the same run in twenty lines, each one a fact.</para>
    /// </summary>
    private static ToolCallReport Call(string tool, string? target, string outcome,
        long ms = 10, int chars = 0, string agentId = "child-1") =>
        new(CallId: Guid.NewGuid().ToString(), AgentId: agentId, ToolName: tool, JobType: null,
            Outcome: outcome, DurationMs: ms, ResultChars: chars,
            StartedAt: DateTimeOffset.UnixEpoch)
        { Target = target };

    [Fact]
    public void AWorkerThatMadeNoCalls_RendersTheSummaryLineAlone()
    {
        // NOT AN EMPTY BODY. A worker that called nothing still ran, and "0 calls · 1.2s" says so;
        // an empty block behind an `expand…` says only that the affordance lied.
        var body = InlineJobSink.TimetableForTest([], TimeSpan.FromSeconds(1.2));

        Assert.Equal("0 calls · 1.2s", body);
    }

    [Fact]
    public void TheTimetable_CountsEachOutcomeCategorySeparately()
    {
        // DENIED IS NOT FAILED, and the distinction is the point of the whole summary: a wall of
        // denials means the worker was fighting the user's permission settings, a wall of failures
        // means its commands were broken. Conflating them sends someone debugging the wrong thing.
        var body = InlineJobSink.TimetableForTest(
        [
            Call("read_file", "Agent.cs", "succeeded"),
            Call("grep", "PluginType", "succeeded"),
            Call("run_shell", "dotnet build", "failed"),
            Call("write_file", "ToolBindings.cs", "denied"),
            Call("write_file", "Md.cs", "denied"),
            Call("run_shell", "sleep 10", "cancelled"),
        ], TimeSpan.FromSeconds(6.2));

        var summary = body.Split('\n')[0];

        Assert.Equal("6 calls · 4 tools · 6.2s · 1 failed · 2 denied · 1 cancelled", summary);
    }

    [Fact]
    public void TheSummaryLine_OmitsCategoriesWithNoCalls()
    {
        // A clean run must not read "16 calls · 4 tools · 6.2s · 0 failed · 0 denied" — a zero is a
        // word the reader has to check before discarding, on every row that went fine.
        var body = InlineJobSink.TimetableForTest(
        [
            Call("read_file", "Agent.cs", "succeeded"),
            Call("read_file", "Md.cs", "succeeded"),
        ], TimeSpan.FromSeconds(0.4));

        Assert.Equal("2 calls · 1 tool · 0.4s", body.Split('\n')[0]);
    }

    [Fact]
    public void TheTimetable_MarksEachOutcomeWithItsOwnGlyph()
    {
        var body = InlineJobSink.TimetableForTest(
        [
            Call("read_file", "Agent.cs", "succeeded"),
            Call("run_shell", "dotnet build", "failed"),
            Call("write_file", "Md.cs", "denied"),
            Call("run_shell", "sleep 10", "cancelled"),
        ], TimeSpan.FromSeconds(1));

        // The header row starts "| " too; the separator starts "|-" and is excluded by it.
        var rows = body.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();

        // The header, then one row per call — CHRONOLOGICAL, and every one of them.
        Assert.Equal(5, rows.Count);
        Assert.Contains("| ✓ |", rows[1], StringComparison.Ordinal);
        Assert.Contains("| ✗ |", rows[2], StringComparison.Ordinal);
        Assert.Contains("| ⊘ |", rows[3], StringComparison.Ordinal);
        Assert.Contains("| – |", rows[4], StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimetable_KeepsEveryCall_WithNoTruncation()
    {
        // THE ROW IS ALREADY COLLAPSED. Someone who expands it has asked for the detail, and there
        // is no second level to expand into — a "… 34 more" line would put the rest nowhere.
        var calls = Enumerable.Range(0, 40)
            .Select(i => Call("read_file", $"File{i}.cs", "succeeded"))
            .ToList();

        var body = InlineJobSink.TimetableForTest(calls, TimeSpan.FromSeconds(9));

        Assert.Contains("File0.cs", body, StringComparison.Ordinal);
        Assert.Contains("File39.cs", body, StringComparison.Ordinal);
        Assert.Equal(40, body.Split('\n').Count(l => l.Contains("read_file", StringComparison.Ordinal)));
    }

    [Fact]
    public void AToolNameIsACodeSpan_SoTheThemeStylesIt()
    {
        // Worker rows render with Markdown = true, so the renderer styles a code span through the
        // live theme. A colour constant here would hardcode what the theme already decides.
        var body = InlineJobSink.TimetableForTest(
            [Call("read_file", "Agent.cs", "succeeded")], TimeSpan.FromSeconds(1));

        Assert.Contains("`read_file`", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetContainingAPipe_StaysInsideItsOwnCell()
    {
        // A pipe is the column delimiter: an unescaped one splits the row into more cells than the
        // header declares and Markdig drops the overflow silently — the command disappears.
        var body = InlineJobSink.TimetableForTest(
            [Call("run_shell", "du -sh . | tail -1", "succeeded")], TimeSpan.FromSeconds(1));

        Assert.Contains(@"du -sh . \| tail -1", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnderscoreInATarget_IsEscaped_ButNotInsideTheCodeSpan()
    {
        // Md.Escape for the code span, Md.EscapeCell for the bare cell. A code span already hides a
        // pipe from the table parser, so EscapeCell's backslash there would SHOW on screen.
        var body = InlineJobSink.TimetableForTest(
            [Call("read_file", "my_file.cs", "succeeded")], TimeSpan.FromSeconds(1));

        // No backslash in the span: markdown processes no escapes there, so one would SHOW.
        Assert.Contains("`read_file`", body, StringComparison.Ordinal);
        Assert.Contains(@"my\_file.cs", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTarget_LeavesTheCellEmptyRatherThanPrintingNull()
    {
        var body = InlineJobSink.TimetableForTest(
            [Call("todowrite", null, "succeeded")], TimeSpan.FromSeconds(1));

        Assert.DoesNotContain("null", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheTimetable_FormatsBytesAndDurationsTheSameInEveryCulture()
    {
        // THIS REPO HAS SHIPPED A CULTURE BUG BEFORE (:P0 rendering "25 %"). Under fr-FR a bare
        // ":0.0" takes a comma for the decimal point, so "6.2s" becomes "6,2s" — and a comma reads
        // as a GROUP separator to anyone expecting the other.
        var calls = new List<ToolCallReport>
        {
            Call("read_file", "Agent.cs", "succeeded", ms: 4210, chars: 41_000),
        };

        var invariant = WithCulture(CultureInfo.InvariantCulture,
            () => InlineJobSink.TimetableForTest(calls, TimeSpan.FromSeconds(6.2)));
        var french = WithCulture(new CultureInfo("fr-FR"),
            () => InlineJobSink.TimetableForTest(calls, TimeSpan.FromSeconds(6.2)));

        Assert.Equal(invariant, french);
        Assert.Contains("6.2s", invariant, StringComparison.Ordinal);
    }

    private static T WithCulture<T>(CultureInfo culture, Func<T> work)
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try { return work(); }
        finally { CultureInfo.CurrentCulture = was; }
    }

    /// <summary>
    /// The accumulator is keyed by the CHILD's agent id, which the envelope is the one artefact
    /// always carrying — the same reasoning <see cref="SubAgentEnvelope.StateOf"/> documents for the
    /// state.
    /// </summary>
    [Fact]
    public void TheChildsId_IsReadBackOutOfItsEnvelope()
    {
        Assert.Equal("01KZ", SubAgentEnvelope.IdOf(
            "<sub_agent id=\"01KZ\" state=\"completed\">\nthe answer\n</sub_agent>"));
        Assert.Null(SubAgentEnvelope.IdOf("an ordinary tool result"));
        Assert.Null(SubAgentEnvelope.IdOf(null));
    }

    [Fact]
    public void AFinishedWorkersBody_IsItsTimetable_NotItsProse()
    {
        var sink = HeadlessSink();
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded"));

        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"completed\">\nI read the file and thought hard.\n</sub_agent>",
        });

        var body = sink.WorkerBodyForTest(job);

        Assert.NotNull(body);
        Assert.Contains("`read_file`", body!, StringComparison.Ordinal);
        Assert.DoesNotContain("thought hard", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProseSurvivesOnTheJob_EvenThoughItIsNoLongerRendered()
    {
        // IntrospectionTools reads Output["content"] — that is how the ORCHESTRATOR consumes a
        // worker's result. Not rendering it must not mean deleting it.
        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"completed\">\nthe answer\n</sub_agent>",
        });

        HeadlessSink().WorkerBodyForTest(job);

        Assert.Contains("the answer", job.Result!.Output!["content"]!.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFinishedWorkersCalls_AreDroppedFromTheAccumulator()
    {
        // Or a long session grows a dictionary of every call ever made.
        var sink = HeadlessSink();
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded"));

        var job = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"completed\">\nthe answer\n</sub_agent>",
        });

        sink.WorkerBodyForTest(job);
        var second = sink.WorkerBodyForTest(job);

        // The calls are gone, so the second read renders a timetable with none in it.
        Assert.StartsWith("0 calls", second!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonWorkerRow_IsUntouchedByTheTimetable()
    {
        // The timetable replaces a WORKER's prose. A shell job's stdout is not prose and not a
        // narration — it is the result, and it keeps its body.
        var job = TypedJob("shell", JobState.Succeeded,
            new Dictionary<string, object?> { ["content"] = "hello" });

        Assert.Null(HeadlessSink().WorkerBodyForTest(job));
    }

    [Fact]
    public void AFailedWorker_KeepsItsReason_RatherThanShowingATimetable()
    {
        // The timetable replaces a NARRATION. A failed worker's body is not one: it is the error the
        // user's next action depends on, and a list of the calls that ran before it broke answers a
        // question nobody is asking yet. Seen while writing this: the row rendered "0 calls · 3.0s"
        // and the reason was simply gone.
        var sink = HeadlessSink();
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded"));

        var job = JobWith(JobState.Failed, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"error\">\nboom\n</sub_agent>",
        }, error: "the child blew up");

        Assert.Null(sink.WorkerBodyForTest(job));
    }

    [Fact]
    public void ACappedWorker_KeepsTheEnvelopesWarningNote()
    {
        // "This agent hit its turn limit… NOT a completed answer" rides between the tag and the
        // text, and StripEnvelope keeps it on purpose. A table in its place would drop the one line
        // saying the answer above it is unfinished — the worst line to lose.
        var job = JobWith(JobState.Cancelled, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"capped\">\n"
                + "This agent hit its turn limit before finishing.\nhalf an answer\n</sub_agent>",
        });

        Assert.Null(HeadlessSink().WorkerBodyForTest(job));
    }

    [Fact]
    public void AFailedWorkersCalls_AreDroppedFromTheAccumulatorToo()
    {
        // The body is one question and the accumulator is another. A failed child's calls are no
        // less finished for its having failed, and a session leaks exactly the runs it has most of
        // if only the successes are swept.
        var sink = HeadlessSink();
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded"));

        var failed = JobWith(JobState.Failed, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"error\">\nboom\n</sub_agent>",
        }, error: "the child blew up");

        sink.WorkerBodyForTest(failed);

        // Nothing is left under that id — a later succeeded row for it renders an empty timetable
        // rather than the dead run's calls.
        var succeeded = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"completed\">\nthe answer\n</sub_agent>",
        });

        Assert.StartsWith("0 calls", sink.WorkerBodyForTest(succeeded)!, StringComparison.Ordinal);
    }


    // ---- the LIVE worker body ---------------------------------------------------------------------

    /// <summary>
    /// A child built for real, because the live body reads its <c>BufferedJobPanel</c> — the one
    /// thing a hand-rolled stand-in cannot have, since <see cref="SubAgent"/> requires an
    /// <see cref="Agent"/> and the panel is what the sink actually walks.
    /// </summary>
    private static SubAgent Child() =>
        new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new CxAgent.Core.Llm.MockLlmProvider(),
            Executors = CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins(),
            Ledger = new CxAgent.Core.Llm.TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        }).Create();

    /// <summary>
    /// A row for a child's own job, at whatever state the case under test needs.
    ///
    /// <para>PlanLocalId AND DisplayName BOTH, exactly as Agent.InvokeAndShowAsync fills them: the
    /// tool name and then that name followed by its arguments. The in-flight row splits the two back
    /// apart, so a helper that set only one would test a shape the app never produces.</para>
    /// </summary>
    private static Job ChildJob(string tool, string args, JobState state,
        DateTimeOffset? startedAt = null) => new()
    {
        Id = "c1", AgentId = "child", JobType = "shell", PlanLocalId = tool,
        DisplayName = args.Length == 0 ? tool : $"{tool} {args}", State = state,
        CreatedAt = startedAt ?? DateTimeOffset.UtcNow,
        StartedAt = startedAt ?? DateTimeOffset.UtcNow,
    };

    /// <summary>The parent's spawn row while the child is still going.</summary>
    private static Job RunningWorker(string? progressBody = null) => new()
    {
        Id = "j1", AgentId = "g1", JobType = "llm_agent", DisplayName = "read the RFC files",
        State = JobState.Running,
        CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
        ProgressBody = progressBody,
    };

    [Fact]
    public void ARunningWorker_ShowsTheSameTimetable_AsAFinishedOne()
    {
        // THE ROW MUST NOT CHANGE SHAPE WHEN IT SETTLES. What lands at the finish line is this same
        // table plus the `out` column and the final counts — so a reader who has been watching it
        // grow is not handed a different artefact at the one moment they stop watching.
        var sink = HeadlessSink();
        var child = Child();
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", chars: 4_000,
            agentId: child.Agent.Id));

        var body = sink.RunningWorkerBodyForTest(RunningWorker());

        Assert.NotNull(body);
        Assert.Contains("`read_file`", body!, StringComparison.Ordinal);
        Assert.Contains("Agent.cs", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningWorkersTable_HasNoOutColumn()
    {
        // Output size is noise mid-flight: it lands when the row settles, and a column of blanks
        // beside a spinning row is a column that asks to be read and says nothing.
        var sink = HeadlessSink();
        var child = Child();
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", chars: 4_000,
            agentId: child.Agent.Id));

        var body = sink.RunningWorkerBodyForTest(RunningWorker())!;

        Assert.DoesNotContain(" out ", body, StringComparison.Ordinal);
        Assert.Contains("| target |", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInFlightCall_IsTheLastRow_WhileItRuns()
    {
        // The whole point of a live body: the call happening RIGHT NOW is the one that says whether
        // the worker is on the right track, and it is the one the finished-calls list cannot hold.
        var sink = HeadlessSink();
        var child = Child();
        child.Jobs.ToolsChanged([ChildJob("run_shell", "dotnet build", JobState.Running)]);
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", agentId: child.Agent.Id));

        var body = sink.RunningWorkerBodyForTest(RunningWorker())!;
        var rows = body.Split('\n');

        Assert.Contains("dotnet build", rows[^1], StringComparison.Ordinal);
        Assert.Contains("read_file", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInFlightRow_IsAbsent_OnceTheChildsJobIsTerminal()
    {
        // A finished child job is already in the completed list — ToolCallFinished forwards it — so
        // rendering it again from the panel would show the same call twice, once with a spinner.
        var sink = HeadlessSink();
        var child = Child();
        child.Jobs.ToolsChanged([ChildJob("run_shell", "dotnet build", JobState.Succeeded)]);
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", agentId: child.Agent.Id));

        var body = sink.RunningWorkerBodyForTest(RunningWorker())!;

        Assert.DoesNotContain("dotnet build", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerWithNoCallsYet_FallsBackToItsProgressBody()
    {
        // Expanding a running spawn must never reveal an empty block — that is the worst moment to
        // show nothing, and the progress body is what the row has to say until the first call lands.
        var sink = HeadlessSink();
        sink.NoteChild("j1", Child());

        Assert.Null(sink.RunningWorkerBodyForTest(RunningWorker("  type: general")));
    }

    [Fact]
    public void AGrandchildsRunningJob_IsNotShown()
    {
        // DIRECT CHILDREN ONLY. The completed list already carries nested calls, because a parent
        // forwards its children's reports up the chain — but the in-flight row is a read of ONE
        // panel, and walking into a grandchild's would put another agent's work on this row with
        // nothing saying whose it is.
        var sink = HeadlessSink();
        var child = Child();
        var grandchild = Child();
        grandchild.Jobs.ToolsChanged([ChildJob("grep", "\"PluginType\"", JobState.Running)]);
        child.Jobs.ToolsChanged([ChildJob("run_shell", "dotnet build", JobState.Running)]);
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", agentId: child.Agent.Id));

        var body = sink.RunningWorkerBodyForTest(RunningWorker())!;

        Assert.Contains("dotnet build", body, StringComparison.Ordinal);
        Assert.DoesNotContain("grep", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AFinishedWorkersChild_IsReleased()
    {
        // Same discipline as the call accumulator: a fan-out session must not retain every SubAgent
        // it ever spawned, each of which pins a whole Agent and its context.
        var sink = HeadlessSink();
        var child = Child();
        child.Jobs.ToolsChanged([ChildJob("run_shell", "dotnet build", JobState.Running)]);
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", agentId: child.Agent.Id));

        var finished = JobWith(JobState.Succeeded, new Dictionary<string, object?>
        {
            ["content"] = "<sub_agent id=\"child-1\" state=\"completed\">\nthe answer\n</sub_agent>",
        });
        sink.WorkerBodyForTest(finished);

        // Nothing is held under that row any more, so a live read finds no child and falls back.
        Assert.Null(sink.RunningWorkerBodyForTest(RunningWorker("  type: general")));
    }

    [Fact]
    public void TheLiveTimetable_RendersIdenticallyUnderAnyCulture()
    {
        // The in-flight row carries a live millisecond clock, which is exactly the kind of number
        // that takes the current culture's decimal separator if it is not routed through
        // DisplayNumber — and this repo has shipped a culture bug before.
        var sink = HeadlessSink();
        var child = Child();
        child.Jobs.ToolsChanged([ChildJob("run_shell", "dotnet build", JobState.Running,
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3))]);
        sink.NoteChild("j1", child);
        sink.RecordToolCall(Call("read_file", "Agent.cs", "succeeded", ms: 4210,
            agentId: child.Agent.Id));

        var invariant = WithCulture(CultureInfo.InvariantCulture,
            () => sink.RunningWorkerBodyForTest(RunningWorker()));
        var french = WithCulture(new CultureInfo("fr-FR"),
            () => sink.RunningWorkerBodyForTest(RunningWorker()));

        Assert.Contains("4,210", invariant!, StringComparison.Ordinal);
        // The elapsed figures differ between the two calls by however long the first took, so the
        // comparison is of the SHAPE that culture would change: the separators, not the digits.
        Assert.Equal(Digits(invariant!), Digits(french!));
    }

    /// <summary>Replaces every digit with '0', leaving the separators a culture would change.</summary>
    private static string Digits(string text) =>
        new(text.Select(c => char.IsDigit(c) ? '0' : c).ToArray());

    [Fact]
    public void TheInFlightRow_EscapesItsTargetButNotItsToolName()
    {
        // The same split the finished rows are pinned to: a tool name lives inside a code span, where
        // a backslash would SHOW rather than protect — and nearly every tool name is underscored, so
        // escaping there puts a stray mark on nearly every row. A bare target cell has no such
        // shelter and must be escaped, or a raw pipe splits the row and Markdig drops the overflow.
        var sink = HeadlessSink();
        var child = Child();
        child.Jobs.ToolsChanged([ChildJob("run_shell", "grep a_b | wc", JobState.Running)]);
        sink.NoteChild("j1", child);

        var row = sink.RunningWorkerBodyForTest(RunningWorker())!.Split('\n')[^1];

        Assert.Contains("`run_shell`", row, StringComparison.Ordinal);
        Assert.DoesNotContain("run\\_shell", row, StringComparison.Ordinal);
        Assert.Contains("a\\_b", row, StringComparison.Ordinal);
        Assert.DoesNotContain("b | wc", row, StringComparison.Ordinal);
    }

    private static InlineJobSink HeadlessSink() => new(
        new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
        new ChatTranscriptControl());

}
