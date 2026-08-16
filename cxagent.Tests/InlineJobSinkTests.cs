using CxAgent.Core.Models;
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
/// <para>The answer was that <c>ToolUpdated</c> only ever called <c>SetStatus</c> — the message BODY
/// stayed whatever <c>Title(job)</c> produced when the job first appeared, i.e. the job's NAME. Every
/// plugin writes its real output to <c>Output["content"]</c> (LlmAgentJobPlugin's worker transcript,
/// ShellJobPlugin's stdout) and the ONLY reader was IntrospectionTools — the tool the ORCHESTRATOR
/// uses to read results. The user could not see what their own workers said.</para>
///
/// <para>There were no InlineJobSink tests at all before this file, which is why it shipped.</para>
/// </summary>
public class InlineJobSinkTests
{
    private static Job JobWith(JobState state, Dictionary<string, object?>? output = null,
        string? error = null, double seconds = 1.0) =>
        new()
        {
            Id = "j1",
            AgentId = "g1",
            PluginType = "llm_agent",
            DisplayName = "read the RFC files",
            State = state,
            Result = new JobResult
            {
                Success = state == JobState.Succeeded,
                Output = output,
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
        // The body used to cap at 4,000 chars. The row is COLLAPSED by default now, so length costs
        // nothing until the user opens it -- and a clipped body meant the full text existed only in
        // the log file, which nobody reads.
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

    // --- Compact rows: driven by "is there anything to read", not by plugin type ------------------

    private static Job TypedJob(string pluginType, JobState state,
        Dictionary<string, object?>? output = null) =>
        new()
        {
            Id = "j1", AgentId = "g1", PluginType = pluginType, DisplayName = "step", State = state,
            Result = new JobResult
            {
                Success = state == JobState.Succeeded, Output = output, Duration = TimeSpan.Zero,
            },
        };

    [Fact]
    public void AJobThatProducedNOTHING_RendersCompact_WhateverItsType()
    {
        // From a live screenshot: five rows reading "Read HeuristicEngine.c...  expand..." at 0.0s,
        // each offering to expand into nothing. The FIRST attempt at this compacted by plugin type
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
        // REPLACES AFAILEDJob_IsNEVERCompact_ItHasAnErrorAndButtons, whose own title names the
        // reason it is obsolete: the Retry/Skip/Diagnose buttons were removed, so the exemption was
        // reserving a footer for an affordance that no longer exists. What it cost was four lines to
        // say one thing -- header ending "failed - 0.0s", the error, a full-width rule, then
        // "failed - 0.0s" AGAIN. Measured live on a missing file.
        //
        // (The old test passed for an unrelated reason: it used an llm_agent, which is exempt as a
        // WORKER whatever its state. It never exercised the failure rule it was named for.)
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
            Id = "j1", AgentId = "g1", PluginType = "llm_agent", DisplayName = "review it",
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
            Id = "j1", AgentId = "g1", PluginType = "llm_agent", DisplayName = "do it",
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
        // long session readable (opencode drops completed rows to textMuted while active rows hold
        // theme.text).
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
    /// <para>Keyed on the ENVELOPE rather than on PluginType: llm_agent is the row type and covers
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
}
