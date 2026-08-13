using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// How a plan row is meant to render.
///
/// <para>The sink marshals every mutation through <c>EnqueueOnUIThread</c>, which only the real
/// <c>Run()</c> pump drains — so a headless test cannot observe the landed transcript. What it CAN
/// pin are the decisions the sink makes about a row, which is where every mistake in this feature
/// was: four separate branches keyed on <c>PluginType == "llm_agent"</c>, each of which had to learn
/// about "todo" and none of which the suite covered.</para>
/// </summary>
public class TodoRowTests
{
    private static Job TodoJob(string name = "plan · 1/3", JobState state = JobState.Succeeded) => new()
    {
        Id = "job-1",
        PlanLocalId = "todowrite",
        AgentId = "agent-1",
        PluginType = "todo",
        DisplayName = name,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?>
            {
                ["content"] = "1 of 3 done\n\nNow\n  - [>] the current step\n\nNext\n  - [ ] the next one",
            },
        },
    };

    /// <summary>
    /// NOT COMPACT, which is what keeps the list on screen. A compact row shows its header and hides
    /// the body behind `expand…` — and the body IS the plan, so collapsing it hides the thing the
    /// user just asked to see.
    /// </summary>
    [Fact]
    public void APlanRow_IsNotCompact()
    {
        Assert.False(InlineJobSink.IsCompactRowForTest(TodoJob()));
    }

    /// <summary>
    /// A worker's row is the precedent and the only other non-compact kind. Pinned together so a
    /// future edit to one is an obvious question about the other.
    /// </summary>
    [Fact]
    public void OnlyPlansAndWorkers_AreNonCompact()
    {
        Assert.True(InlineJobSink.IsCompactRowForTest(TodoJob() with { PluginType = "file" }));
        Assert.True(InlineJobSink.IsCompactRowForTest(TodoJob() with { PluginType = "shell" }));
        Assert.False(InlineJobSink.IsCompactRowForTest(TodoJob() with { PluginType = "llm_agent" }));
    }

    /// <summary>
    /// STATE IS THE CALLER'S BUSINESS, not this predicate's. <c>IsCompactRow</c> answers "is this
    /// KIND of row compact"; every call site ORs it with <c>!IsTerminal(job.State)</c>, so a running
    /// row is compact because it has no body yet rather than because of what it is.
    ///
    /// <para>Pinned because I assumed the opposite while chasing a missing row, and a predicate that
    /// silently grew a state check would break the running-worker rows in a way nothing else covers.
    /// </para>
    /// </summary>
    [Fact]
    public void IsCompactRow_JudgesTheKindOfRow_NotItsState()
    {
        Assert.Equal(
            InlineJobSink.IsCompactRowForTest(TodoJob(state: JobState.Succeeded)),
            InlineJobSink.IsCompactRowForTest(TodoJob(state: JobState.Running)));
    }
}
