using CxAgent.Core.Models;
using CxAgent.Core.Execution;
using Xunit;

namespace CxAgent.Tests;

public class JobDigestTests
{
    private static Job Done(string content, string? error = null) => new()
    {
        Id = "01JQZX", PlanLocalId = "r1", AgentId = "g", JobType = "llm_agent",
        DisplayName = "Review the file",
        State = error is null ? JobState.Succeeded : JobState.Failed,
        Parameters = new JobParameters(new() { ["prompt"] = "Review this", ["role"] = "reviewer" }),
        Result = new JobResult
        {
            Success = error is null,
            ErrorMessage = error,
            Output = new Dictionary<string, object?> { ["content"] = content },
        },
    };

    [Fact]
    public void From_CarriesTheJobsResolvedParameters()
    {
        // The user's explicit decision: the orchestrator must see WHAT IT ASKED FOR, not just the
        // outcome. A drive showed it referencing {{read_app_paths}} while its own job was named
        // differently — it could not see its own typo because the digest never showed it.
        var d = JobDigest.From(Done("ok"));
        Assert.Equal("Review this", d.Parameters["prompt"]);
        Assert.Equal("reviewer", d.Parameters["role"]);
        Assert.Equal("r1", d.PlanLocalId);
    }

    [Fact]
    public void From_TruncatesLongOutput_WithAVisibleMarker()
    {
        // Output is uncapped by DEFAULT now, so this asks for a cap explicitly -- the mechanism it
        // guards (head+tail with a visible marker, true size still reported) is unchanged.
        var d = JobDigest.From(Done(new string('x', 10_000)), outputCap: 2048);
        Assert.True(d.OutputExcerpt.Length < 5_000);
        Assert.Contains("elided", d.OutputExcerpt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10_000, d.OutputBytes);   // the TRUE size is still reported
    }

    [Fact]
    public void From_NeverTruncatesTheErrorMessage()
    {
        // A truncated error is the one thing a debugger role cannot work with, and the orchestrator
        // is about to decide what to do BECAUSE of this error.
        var err = "Unhandled: " + new string('e', 5_000);
        var d = JobDigest.From(Done("", err));
        Assert.Equal(err, d.ErrorMessage);
    }

    [Fact]
    public void From_TruncatesAHugeParameterValue_ButKeepsTheKey()
    {
        // A file-write job's `content` param can be megabytes. The orchestrator needs to know the
        // param EXISTS and roughly what it holds — not to be handed the whole payload back.
        var job = Done("ok");
        job.Parameters.Values["content"] = new string('y', 50_000);
        var d = JobDigest.From(job, perValueCap: 100);
        Assert.True(d.Parameters["content"].Length < 400);
        Assert.Contains("elided", d.Parameters["content"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_IsStableForTheSameJob()
    {
        // Deterministic rendering: an unstable digest changes the prompt every consult and defeats
        // provider-side caching, the same reason CreatePlanTool's schema is ordinally sorted.
        var job = Done("ok");
        Assert.Equal(JobDigest.From(job).Render(), JobDigest.From(job).Render());
    }

    [Fact]
    public void Render_NamesTheLogFileWhenOutputWasTruncated()
    {
        // Large payloads live in FILES, not in the orchestrator's context — it can pull detail with
        // get_job_output. That is only actionable if the digest says where.
        var job = Done(new string('x', 10_000));
        job.Result = job.Result! with { LogFile = "/logs/g/01JQZX.log" };
        // With an explicit cap, since the line is conditional on output ACTUALLY having been cut.
        Assert.Contains("/logs/g/01JQZX.log", JobDigest.From(job, outputCap: 2048).Render());
    }

    private static Job JobWithOutput(Dictionary<string, object?> output) => new()
    {
        Id = "01JQZX", PlanLocalId = "r1", AgentId = "g", JobType = "llm_agent",
        DisplayName = "Review the file",
        State = JobState.Succeeded,
        Parameters = new JobParameters(new() { ["prompt"] = "Review this", ["role"] = "reviewer" }),
        Result = new JobResult { Success = true, Output = output },
    };

    [Fact]
    public void From_ReadsTheTruncatedFlagFromTheOutputBag()
    {
        // A worker that hit its turn cap sets Output["truncated"] = true. Unread, it reaches the
        // orchestrator only as a "truncated: True" line among the other output keys.
        var job = JobWithOutput(new Dictionary<string, object?>
        {
            ["content"] = "partial work",
            ["truncated"] = true,
        });

        Assert.True(JobDigest.From(job).Truncated);
    }

    [Fact]
    public void From_AnUntruncatedJobIsNotFlagged()
    {
        var job = JobWithOutput(new Dictionary<string, object?> { ["content"] = "all done" });

        Assert.False(JobDigest.From(job).Truncated);
    }

    [Fact]
    public void Render_SaysTheWorkIsUNFINISHED_NotJustThatAFlagIsSet()
    {
        // The point of the whole change. "truncated: True" among the output keys is technically visible
        // and practically invisible — and it says nothing about what it MEANS. The orchestrator has to
        // learn that the answer is PARTIAL, or it reads half an answer as a whole one.
        var job = JobWithOutput(new Dictionary<string, object?>
        {
            ["content"] = "partial work",
            ["truncated"] = true,
        });

        var text = JobDigest.From(job).Render();

        Assert.Contains("INCOMPLETE", text, StringComparison.OrdinalIgnoreCase);
        // ...and names the remedy, since the orchestrator's job is to decide what happens next.
        Assert.Contains("smaller", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A non-worker job: a file read, whose output is an ECHO of bytes already on disk.</summary>
    private static Job FileRead(string content) => new()
    {
        Id = "01FILE", PlanLocalId = "f1", AgentId = "g", JobType = "file",
        DisplayName = "Read QEncoder.cs",
        State = JobState.Succeeded,
        Parameters = new JobParameters(new() { ["action"] = "read", ["path"] = "QEncoder.cs" }),
        Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?> { ["content"] = content },
        },
    };

    [Fact]
    public void Render_PlaceholderMode_WithholdsAFileJobsBody()
    {
        // Measured on a real drive: this exact job rendered 9,007 chars cut to 2,078, twice per
        // goal, into a prompt whose only decision is continue/modify/finish.
        var text = JobDigest.From(FileRead(new string('x', 9007))).Render(bulkOutputAsPlaceholder: true);

        Assert.DoesNotContain("xxxx", text);
        Assert.Contains("9,007", text);   // the TRUE size, not the capped one
    }

    [Fact]
    public void Render_PlaceholderMode_KEEPSAWorkersOutput()
    {
        // The whole point of the split: a worker's output IS the answer, not an echo. The audit
        // found workers were never among the truncated, and this keeps it that way.
        Assert.Contains("wwww",
            JobDigest.From(Done(new string('w', 9007))).Render(bulkOutputAsPlaceholder: true));
    }

    [Fact]
    public void Render_DefaultMode_StillCarriesAFileJobsBody()
    {
        // THE GUARD ON SayClosingAnswerAsync. That path renders these digests to write the user's
        // answer and passes tools:null, so if placeholder mode ever leaks into the default, the
        // answer to "list ~/bin. what it does?" silently becomes "I read three scripts".
        Assert.Contains("xxxx", JobDigest.From(FileRead(new string('x', 9007))).Render());
    }

    [Fact]
    public void Render_PlaceholderMode_SaysNothingForAJobThatProducedNothing()
    {
        // "0 chars withheld" would invent a withholding that never happened — the orchestrator must
        // still be able to tell "produced nothing" from "produced something you cannot see".
        var empty = FileRead("");
        Assert.DoesNotContain("withheld", JobDigest.From(empty).Render(bulkOutputAsPlaceholder: true));
    }

    [Fact]
    public void From_OutputIsUNCAPPEDByDefault()
    {
        // The orchestrator PLANS and ANSWERS from this text: consult decides continue/modify/finish
        // by it, and SayClosingAnswerAsync writes the user's reply out of it with no tool available
        // to fetch the rest. A 2048-char cut through a worker's review silently discarded findings
        // -- the reader could not even tell how much was missing.
        var big = new string('w', 20_000);

        var digest = JobDigest.From(Done(big));

        Assert.Equal(20_000, digest.OutputExcerpt.Length);
        Assert.DoesNotContain("elided", digest.OutputExcerpt);
    }

    [Fact]
    public void From_PARAMETERSAreStillCapped()
    {
        // Params keep their bound: a write_file job's `content` is routinely the whole artefact, and
        // the pending list echoes it back verbatim on EVERY consult -- a cost the output does not
        // have, since a job's output is rendered once per digest.
        var job = FileRead("small output") with
        {
            Parameters = new JobParameters(new() { ["content"] = new string('p', 20_000) }),
        };

        var digest = JobDigest.From(job);

        Assert.True(digest.Parameters["content"].Length < 20_000);
        Assert.Contains("elided", digest.Parameters["content"]);
    }

    [Fact]
    public void From_AnExplicitOutputCapStillApplies()
    {
        // The knob is for a caller that genuinely needs a bound; it is not the default, which would
        // silently truncate everything.
        var digest = JobDigest.From(Done(new string('w', 20_000)), outputCap: 500);
        Assert.True(digest.OutputExcerpt.Length < 20_000);
    }

    [Fact]
    public void Render_AProposalReadsAsJobsToADOPT_NotAsAnOutputBlob()
    {
        // The digest is the ONLY thing the orchestrator sees of a finished job. Rendered as raw JSON
        // under a bare key, a proposal reads as "here is some data" and gets summarised rather than
        // adopted -- which looks exactly like a goal that quietly does less than it was asked.
        var job = Done("planned it") with { Parameters = new JobParameters(new() { ["role"] = "planner" }) };
        job.Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?>
            {
                ["content"] = "planned it",
                ["proposed_jobs"] = """{"jobs":[{"id":"read_x","name":"Read X"}],"notes":"careful with QP"}""",
            },
        };

        var text = JobDigest.From(job).Render();

        Assert.Contains("PROPOSED JOBS", text);
        Assert.Contains("jobs_to_add", text);      // says what to DO with it
        Assert.Contains("read_x", text);
        Assert.Contains("careful with QP", text);  // the notes survive
    }

    [Fact]
    public void Render_APlannersProposalSurvivesThePlaceholderMode()
    {
        // BuildConsultPrompt withholds bulk output for non-worker jobs. A planner is an llm_agent,
        // so it is exempt -- but this is the one job whose output the orchestrator MUST see, and an
        // exemption that holds by accident is one that breaks when the rule is next edited.
        var job = Done("planned it");
        job.Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?>
            {
                ["proposed_jobs"] = """{"jobs":[{"id":"read_x"}]}""",
            },
        };

        Assert.Contains("read_x", JobDigest.From(job).Render(bulkOutputAsPlaceholder: true));
    }
}
