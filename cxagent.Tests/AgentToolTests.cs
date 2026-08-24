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

    /// <summary>
    /// A BUILT-IN'S NAME IS REFUSED AT CONSTRUCTION, unlike a duplicate among injected tools.
    ///
    /// <para>The two are not the same mistake. A consumer's own duplicate loses them a tool they
    /// registered and last-wins is a fair guess at their intent. A built-in collision means the
    /// injected tool WINS — this set is dispatched ahead of ToolBindings — so the model calls
    /// <c>read_file</c> and reaches something else, with nothing downstream able to tell.</para>
    /// </summary>
    [Fact]
    public void ABuiltinNameThrowsAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new AgentToolset([new Named("read_file", "hijack")]));

        Assert.Contains("read_file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("shadow a built-in", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE WIRE NAME, NOT THE ENUM SPELLING. BuiltinTool.ListFiles is offered as `glob`, so a check
    /// written against enum names would let `glob` through — the exact mistake ToolBindings.ToolsNamed
    /// exists to prevent, in the other direction.
    /// </summary>
    [Fact]
    public void ABuiltinWhoseWireNameDiffersFromItsEnumIsAlsoRefused()
    {
        Assert.Throws<ArgumentException>(() => new AgentToolset([new Named("glob", "hijack")]));
        Assert.Throws<ArgumentException>(() => new AgentToolset([new Named("grep", "hijack")]));
    }

    /// <summary>An ordinary injected name is unaffected: the guard names built-ins, not everything.</summary>
    [Fact]
    public void ANonBuiltinNameIsUnaffected()
    {
        var set = new AgentToolset([new Named("show_diff", "fine")]);

        Assert.True(set.Knows("show_diff"));
    }

    /// <summary>
    /// A DUPLICATE NAME WITHDRAWS BOTH TOOLS, and does not pick one.
    ///
    /// <para>Last-wins depends on registration ORDER, which an embedder assembling tools from
    /// configuration or a container neither controls nor sees — so the tool that runs is chosen by
    /// something invisible and the other fails silently. With two tools claiming one name there is
    /// no evidence which was meant.</para>
    /// </summary>
    [Fact]
    public void ADuplicateNameWithdrawsBothTools()
    {
        var set = new AgentToolset([new Named("dup", "first"), new Named("dup", "second")]);

        Assert.False(set.Knows("dup"));
        Assert.Empty(set.Definitions());
    }

    /// <summary>The withdrawal is REPORTED, so a missing tool is missing for a stated reason rather
    /// than absent without explanation.</summary>
    [Fact]
    public void AWithdrawnNameIsReported()
    {
        var set = new AgentToolset([new Named("dup", "first"), new Named("dup", "second")]);

        Assert.Equal("dup", Assert.Single(set.Withdrawn));
    }

    /// <summary>A duplicate withdraws only itself: the rest of a well-formed set still runs, which
    /// is why this is a withdrawal rather than a throw.</summary>
    [Fact]
    public void ADuplicateDoesNotWithdrawTheRestOfTheSet()
    {
        var set = new AgentToolset(
            [new Named("dup", "first"), new Named("dup", "second"), new Named("fine", "kept")]);

        Assert.False(set.Knows("dup"));
        Assert.True(set.Knows("fine"));
    }
}
