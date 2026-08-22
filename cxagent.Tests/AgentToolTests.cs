using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The consumer-facing contract. These tests are deliberately shallow: they exist so that a change
/// to <see cref="IAgentTool"/>'s shape breaks HERE, in a file that names the consumer, rather than
/// silently in whatever front end had already implemented it.
/// </summary>
public class AgentToolTests
{
    /// <summary>A tool that needs no permission — the calculator case from the spec.</summary>
    private sealed class StubTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(
            "stub", "does nothing", JsonSerializer.SerializeToElement(new { type = "object" }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true });
    }

    [Fact]
    public void StubToolSatisfiesTheInterface()
    {
        IAgentTool tool = new StubTool();

        Assert.Equal("stub", tool.Definition.Name);
        Assert.Null(tool.Gate(new JobParameters()));
    }

    [Fact]
    public async Task AnUngatedToolStillReturnsAResult()
    {
        // Gate returning null means "no human needed", NOT "do not run". A wrapper that read null
        // as a refusal would make every ungated tool a no-op, which is the kind of inversion that
        // passes a smoke test because nothing errors.
        IAgentTool tool = new StubTool();

        var result = await tool.ExecuteAsync(new JobParameters(), null!, CancellationToken.None);

        Assert.True(result.Success);
    }
}

/// <summary>Duplicate tool names — a consumer mistake that must not take down a session.</summary>
public class AgentToolsetDuplicateTests
{
    private sealed class Named : IAgentTool
    {
        private readonly string _marker;
        public Named(string name, string marker)
        {
            _marker = marker;
            Definition = new(name, marker, JsonSerializer.SerializeToElement(new { type = "object" }));
        }

        public ToolDefinition Definition { get; }
        public PermissionRequest? Gate(JobParameters call) => null;
        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, Output = { ["content"] = _marker } });
    }

    [Fact]
    public void ADuplicateNameDoesNotThrowAtConstruction()
    {
        // The comment on this constructor promised "last one wins rather than throwing" while the
        // code used ToDictionary, which throws. A consumer registering two tools with one name has
        // made a mistake; taking down their session at wiring time is a worse answer than running
        // the one they most recently asked for.
        var set = new AgentToolset([new Named("dup", "first"), new Named("dup", "second")]);

        Assert.True(set.Knows("dup"));
        Assert.Single(set.Definitions());
    }

    [Fact]
    public void TheLastRegistrationIsTheOneThatRuns()
    {
        var set = new AgentToolset([new Named("dup", "first"), new Named("dup", "second")]);

        Assert.Equal("second", Assert.Single(set.Definitions()).Description);
    }
}
