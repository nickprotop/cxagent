using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.UI.Tools;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The injected tool: what it offers, what it asks, and what it hands back to each of its two
/// audiences.
/// </summary>
public class ShowDiffToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-showdiff-" + Guid.NewGuid().ToString("N")[..8]);

    public ShowDiffToolTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private ShowDiffTool Tool() => new(_dir);

    private static JobParameters Call(string path, bool staged = false) =>
        new(new Dictionary<string, object?> { ["path"] = path, ["staged"] = staged });

    [Fact]
    public void PathIsRequiredWhichIsHowOneFileScopeIsEnforced()
    {
        // No path means no diff, so there is no way to ask for the whole tree — the scope is the
        // schema rather than a check somewhere that could be forgotten.
        var schema = Tool().Definition.InputSchema;

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("path", required);
        Assert.DoesNotContain("paths", required);
    }

    [Fact]
    public void GateAlwaysReturnsAFileReadRequest()
    {
        // Including for a path inside the working directory. Whether that request actually needs a
        // HUMAN is PermissionPolicy's answer — it requires the folder to be TRUSTED as well as
        // in-boundary — and this tool must not pre-empt it. Returning null here would skip the
        // prompt in exactly the case the prompt exists for.
        var request = Tool().Gate(Call("src/File.cs"));

        Assert.NotNull(request);
        Assert.Equal(PermissionKind.FileRead, request!.Kind);
    }

    [Fact]
    public void GateResolvesThePathBeforeBuildingTheRequest()
    {
        // "../../etc/passwd" is inside the working directory only as a string. The policy compares
        // real paths, so an unresolved one would be the recurring shape of bug in this codebase: a
        // check that examines part of a request and lets the rest through unexamined.
        var request = Tool().Gate(Call(Path.Combine("..", "..", "etc", "passwd")));

        Assert.NotNull(request);
        Assert.DoesNotContain("..", request!.Display);
        Assert.Equal(Path.GetFullPath(request.Display), request.Display);
    }

    [Fact]
    public void NoAlwaysRuleIsOfferedForAReadOfAnArbitraryPath()
    {
        // A rule would have to name the path, and "always show me diffs of this one file" is not a
        // permission anyone means to grant. Null means no Always button and no stored rule matches.
        Assert.Null(Tool().Gate(Call("a.cs"))!.AlwaysRule);
    }

    [Fact]
    public async Task AMissingPathIsRefusedRatherThanDiffingEverything()
    {
        var result = await Tool().ExecuteAsync(new JobParameters(), new TestJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("one file at a time", result.ErrorMessage);
    }

    [Fact]
    public async Task TheMarkupGoesToTheTranscriptAndASummaryToTheModel()
    {
        // TWO AUDIENCES, TWO KEYS. content is what InlineJobSink renders; summary is what the model
        // is told. Handing the model the markup costs a turn of it describing colour tags for
        // something already on the user's screen.
        var result = await Tool().ExecuteAsync(Call("nothing.cs"), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Output.ContainsKey("content"));
        Assert.True(result.Output.ContainsKey("summary"));
        Assert.NotEqual(result.Output["content"]?.ToString(), result.Output["summary"]?.ToString());
    }

    [Fact]
    public async Task OutsideARepositoryItSaysSoRatherThanFailing()
    {
        // A failed job renders its error; this is not an error, it is an answer. Failing here would
        // also invite an automatic diagnosis round for a fact no diagnosis can change.
        var result = await Tool().ExecuteAsync(Call("f.cs"), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("not a git repository", result.Output["content"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSummaryIsShortAndFactual()
    {
        // Enough to confirm the call worked, not enough to invite a paraphrase.
        var summary = ShowDiffTool.Summarise(new FileDiff("UsageView.cs", 12, 3, DiffStatus.Changed, []));

        Assert.Contains("UsageView.cs", summary);
        Assert.Contains("+12", summary);
        Assert.Contains("shown above", summary);
        Assert.True(summary.Length < 80, "a long summary is one the model will try to expand on");
    }
}
