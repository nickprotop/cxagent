using CxAgent.Core.Commands;
using CxAgent.Core.Export;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Where an export goes, how big it reads, and what /export and /sessions export ask for.</summary>
public class ExportTargetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("export-target-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TheDefaultName_IsTheDateAndTheTitle()
    {
        var at = new DateTimeOffset(2026, 10, 18, 10, 42, 0, TimeSpan.Zero);

        Assert.Equal("2026-10-18-fix-the-login-redirect.md", ExportTarget.DefaultFileName("Fix the login redirect!", at));
        Assert.Equal("2026-10-18-session.md", ExportTarget.DefaultFileName(null, at));
        Assert.Equal("2026-10-18-do-these-in-order-1-read.md",
            ExportTarget.DefaultFileName("Do these in order: 1) read prices.py. 2) In prices.py, use replace", at));
    }

    /// <summary>NOTHING HALF-WRITTEN: the file appears whole or not at all, and an older one is replaced.</summary>
    [Fact]
    public void Write_ReplacesTheFileWhole_AndLeavesNoTemporary()
    {
        var path = Path.Combine(_dir, "out.md");
        File.WriteAllText(path, "old");

        ExportTarget.Write(path, "# new\n");

        Assert.Equal("# new\n", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Write_IntoAMissingFolder_Throws() =>
        Assert.ThrowsAny<IOException>(() => ExportTarget.Write(Path.Combine(_dir, "no", "such", "out.md"), "x"));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(14_336, "14 KB")]
    [InlineData(2_202_009, "2.1 MB")]
    public void Size_ReadsAsAPersonWould(int bytes, string expected) => Assert.Equal(expected, ExportTarget.Size(bytes));
}

public class ExportCommandTests
{
    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("last", ExportScope.LastTurn, null, null)]
    [InlineData("last 5", ExportScope.LastTurns, 5, null)]
    [InlineData("full", null, null, true)]
    [InlineData("full last 2", ExportScope.LastTurns, 2, true)]
    [InlineData("last full", ExportScope.LastTurn, null, true)]
    [InlineData("bogus", null, null, null)]
    public void Arguments_Prefill_TheDialog(string arguments, ExportScope? scope, int? turns, bool? full)
    {
        var prefill = ExportCommand.Parse(arguments);

        Assert.Equal(scope, prefill.Scope);
        Assert.Equal(turns, prefill.Turns);
        Assert.Equal(full, prefill.Full);
    }
}

public class SessionsResolveTests
{
    private static SessionInfo Row(string uid) => new(uid, "t", "/w", 0, 0, true, DateTimeOffset.UtcNow);
    private static readonly IReadOnlyList<SessionInfo> Rows = [Row("ABC111"), Row("ABD222"), Row("XYZ333")];

    [Fact]
    public void ANumber_IsAPositionInTheList() => Assert.Equal("ABD222", SessionsCommand.Resolve("2", Rows).ResumeUid);

    [Fact]
    public void AnId_IsMatchedByItsStart() => Assert.Equal("XYZ333", SessionsCommand.Resolve("xyz", Rows).ResumeUid);

    [Fact]
    public void AnAmbiguousId_IsRefused_NamingTheCandidates()
    {
        var result = SessionsCommand.Resolve("AB", Rows);

        Assert.Null(result.ResumeUid);
        Assert.Contains("matches 2 sessions", result.Reply.Text);
    }
}
