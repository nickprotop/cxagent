using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Core.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Folders a colleague may read without asking — its workplaces — granted through a policy derived from the session's:
/// reads only, each folder only while it is trusted and there, and never while the session is shared to a browser that
/// may not see it. Nothing else the policy decides moves.
/// </summary>
public sealed class ReadAlsoPolicyTests : IDisposable
{
    private readonly string _top = PermissionPolicy.RealFolder(Directory.CreateTempSubdirectory("read-also-").FullName);
    private readonly string _project;
    private readonly string _workplace;
    private readonly string _untrusted;
    private readonly PermissionRulesStore _rules;

    public ReadAlsoPolicyTests()
    {
        _project = Path.Combine(_top, "invoice");
        _workplace = Path.Combine(_top, "consoleex");
        _untrusted = Path.Combine(_top, "stranger");
        foreach (var d in new[] { _project, _workplace, _untrusted }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(_workplace, "a.cs"), "x");
        File.WriteAllText(Path.Combine(_untrusted, "b.cs"), "y");
        _rules = new PermissionRulesStore(new AppPaths(Path.Combine(_top, "config")));
        _rules.SetTrust(_project, TrustState.Trusted);
        _rules.SetTrust(_workplace, TrustState.Trusted);
        _rules.SetTrust(_untrusted, TrustState.Untrusted);
    }

    public void Dispose()
    {
        try { Directory.Delete(_top, recursive: true); } catch (IOException) { }
    }

    private PermissionPolicy Session(EditMode edits = EditMode.AcceptEdits) => new(_project, _rules, edits);

    private static PermissionRequest Read(string path) => new(PermissionKind.FileRead, path, AlwaysRule: null);

    private string InWorkplace => Path.Combine(_workplace, "a.cs");

    [Fact]
    public void A_file_read_in_a_trusted_workplace_is_silent()
    {
        Assert.True(Session().WithReadAlso(new([_workplace], null)).IsSilentlyAllowed(Read(InWorkplace)));
        Assert.False(Session().IsSilentlyAllowed(Read(InWorkplace)));
    }

    /// <summary>
    /// A WRITE THERE STILL ASKS, from either tool; a command that can only look reads there as the file tools do (user,
    /// 2026-11-11: one rule for both — a colleague's `grep` in a codebase it knows asked every time).
    /// </summary>
    [Fact]
    public void A_write_there_still_asks_and_a_command_that_only_looks_does_not()
    {
        var policy = Session().WithReadAlso(new([_workplace], null));

        Assert.False(policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.FileWrite, InWorkplace, null)));
        Assert.False(policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.Shell, $"touch {InWorkplace}", null, Subject: $"touch {InWorkplace}")));
        Assert.True(policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.Shell, $"cat {InWorkplace}", null, Subject: $"cat {InWorkplace}")));
    }

    [Theory]
    [InlineData(EditMode.AcceptEdits)]
    [InlineData(EditMode.AlwaysAsk)]
    public void A_move_or_delete_in_a_workplace_asks(EditMode edits)
    {
        var policy = Session(edits).WithReadAlso(new([_workplace], null));

        Assert.False(policy.IsSilentlyAllowed(Read(InWorkplace) with { RemovesSource = true }));
        Assert.False(policy.IsSilentlyAllowed(new PermissionRequest(PermissionKind.FileWrite, InWorkplace, null) { ActsOn = InWorkplace }));
    }

    [Fact]
    public void An_untrusted_workplace_still_asks() =>
        Assert.False(Session().WithReadAlso(new([_untrusted], null)).IsSilentlyAllowed(Read(Path.Combine(_untrusted, "b.cs"))));

    [Fact]
    public void A_workplace_that_cannot_be_trusted_asks()
    {
        var root = Path.GetPathRoot(_top)!;
        _rules.SetTrust(root, TrustState.Trusted);

        Assert.False(Session().WithReadAlso(new([root], null)).IsSilentlyAllowed(Read(InWorkplace)));
    }

    [Fact]
    public void A_path_that_leaves_the_workplace_asks()
    {
        var policy = Session().WithReadAlso(new([_workplace], null));
        Assert.False(policy.IsSilentlyAllowed(Read(Path.Combine(_workplace, "..", "stranger", "b.cs"))));

        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_workplace, "out"), _untrusted);
        Assert.False(policy.IsSilentlyAllowed(Read(Path.Combine(_workplace, "out", "b.cs"))));
    }

    [Fact]
    public void A_workplace_that_is_gone_asks()
    {
        var gone = Path.Combine(_top, "moved");
        _rules.SetTrust(gone, TrustState.Trusted);

        Assert.False(Session().WithReadAlso(new([gone], null)).IsSilentlyAllowed(Read(Path.Combine(gone, "c.cs"))));
    }

    [Fact]
    public void Served_to_a_browser_a_workplace_outside_its_folders_asks()
    {
        var served = new ServedFolders { Roots = [_project] };
        var policy = Session().WithReadAlso(new([_workplace], served));

        Assert.False(policy.IsSilentlyAllowed(Read(InWorkplace)));
        served.Roots = null;
        Assert.True(policy.IsSilentlyAllowed(Read(InWorkplace)));
        served.Roots = [_top];
        Assert.True(policy.IsSilentlyAllowed(Read(InWorkplace)));
    }

    [Fact]
    public void Narrowing_keeps_read_also()
    {
        var sub = Path.Combine(_project, "sub");
        Directory.CreateDirectory(sub);

        Assert.True(Session().WithReadAlso(new([_workplace], null)).NarrowedTo(sub).IsSilentlyAllowed(Read(InWorkplace)));
    }

    /// <summary>
    /// Read-also changes what may be read and nothing else: a policy said to be narrowed has the sandbox drawn around its
    /// own folder, and an incarnation that works where its session does must keep the session's.
    /// </summary>
    [Fact]
    public void Read_also_does_not_narrow()
    {
        var session = Session();
        var policy = session.WithReadAlso(new([_workplace], null));

        Assert.False(policy.IsNarrowed);
        Assert.Equal(session.Boundary, policy.Boundary);
        session.Edits = EditMode.AlwaysAsk;
        Assert.Equal(EditMode.AlwaysAsk, policy.Edits);   // the mode is still the session's, read live
    }
}
