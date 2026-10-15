using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

public class CommandExpanderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "expand-" + Guid.NewGuid().ToString("N"));
    private readonly List<PermissionRequest> _asked = [];
    private readonly List<string> _ran = [];
    private readonly List<Message> _said = [];
    private bool _allow = true;

    public CommandExpanderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private ExpansionPorts Ports() => new(_dir,
        (request, _) => { _asked.Add(request); return Task.FromResult(_allow); },
        _said.Add)
    {
        RunShell = (command, _) =>
        {
            _ran.Add(command);
            return Task.FromResult(new ShellLineResult(0, false, "OUT(" + command + ")"));
        },
    };

    private static Invocable Command(string body, InvocableKind kind = InvocableKind.Command) =>
        new("x", kind, "/f/x.md", "/f", null, "d", null, true, kind == InvocableKind.Skill, [], body,
            kind == InvocableKind.Skill ? "/skills/x" : null);

    private Task<string> Expand(string body, string args = "", InvocableKind kind = InvocableKind.Command) =>
        CommandExpander.ExpandAsync(new ExpansionRequest(Command(body, kind), args), Ports(), default);

    [Fact]
    public async Task Arguments_FillPlaceholders()
    {
        Assert.Equal("Review PR 42.", await Expand("Review PR $1.", "42"));
    }

    [Fact]
    public async Task Arguments_WithNoPlaceholder_AreAppended()
    {
        Assert.Equal("Tidy up.\n\nARGUMENTS: the tests", await Expand("Tidy up.", "the tests"));
    }

    [Fact]
    public async Task AShellLine_RunsAfterTheGate_AndItsOutputReplacesIt()
    {
        var text = await Expand("Diff:\n!`git diff`\nEnd");

        Assert.Equal("Diff:\nOUT(git diff)\nEnd", text);
        Assert.Equal(PermissionKind.Shell, Assert.Single(_asked).Kind);
        Assert.Contains(_said, m => m.Text.Contains("ran `git diff`"));
    }

    [Fact]
    public async Task ARefusedShellLine_IsNotRun()
    {
        _allow = false;

        var text = await Expand("!`rm -rf build`");

        Assert.Equal("(not run: refused)", text);
        Assert.Empty(_ran);
    }

    /// <summary>What the user typed can fill a placeholder; it can never become a shell line.</summary>
    [Fact]
    public async Task TypedShellSyntax_IsNeverRun()
    {
        var text = await Expand("Say: $ARGUMENTS", "!`touch pwned`");

        Assert.Equal("Say: !`touch pwned`", text);
        Assert.Empty(_ran);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task ArgumentsInAShellLine_AreQuoted()
    {
        await Expand("!`gh pr view $1`", "\"1; rm -rf ~\"");

        Assert.Equal("gh pr view '1; rm -rf ~'", Assert.Single(_ran));
    }

    [Fact]
    public async Task AnAtFile_IsAttachedUnderThePrompt_AfterTheReadGate()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "class A {}");

        var text = await Expand("Review @a.cs please");

        Assert.StartsWith("Review @a.cs please", text);
        Assert.Contains("```\nclass A {}\n```", text);
        Assert.Equal(PermissionKind.FileRead, Assert.Single(_asked).Kind);
    }

    [Fact]
    public async Task AnAtThatIsNotAFile_StaysText_WithNoRead()
    {
        var text = await Expand("Use @Override and @types/node.");

        Assert.Equal("Use @Override and @types/node.", text);
        Assert.Empty(_asked);
        Assert.Empty(_said);
    }

    /// <summary>Typed text is never an include.</summary>
    [Fact]
    public async Task ATypedAtFile_IsNotRead()
    {
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "s3cret");

        var text = await Expand("Look at $1", "@secret.txt");

        Assert.Equal("Look at @secret.txt", text);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task ARefusedRead_StaysText_WithANotice()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "x");
        _allow = false;

        Assert.Equal("See @a.cs", await Expand("See @a.cs"));
        Assert.Contains(_said, m => m.Text.Contains("a.cs"));
    }

    [Fact]
    public async Task ATypedSkill_OpensWithTheLoadedMarker()
    {
        var text = await Expand("Deploy carefully.", "staging", InvocableKind.Skill);

        Assert.StartsWith("[skill: x]\ndirectory: /skills/x\n\nDeploy carefully.", text);
        Assert.EndsWith("ARGUMENTS: staging", text);
    }

    [Fact]
    public async Task ATimedOutLine_SaysSo_AsAWarning()
    {
        var ports = Ports() with
        {
            RunShell = (_, _) => Task.FromResult(new ShellLineResult(-1, true, "")),
        };

        var text = await CommandExpander.ExpandAsync(new ExpansionRequest(Command("!`sleep 99`"), ""), ports, default);

        Assert.Equal("(timed out after 60s)", text);
        Assert.Contains(_said, m => m.Severity == Severity.Warning);
    }

    [Fact]
    public async Task ANonZeroExit_IsAppended()
    {
        var ports = Ports() with
        {
            RunShell = (_, _) => Task.FromResult(new ShellLineResult(2, false, "bad")),
        };

        Assert.Equal("bad\n(exit 2)",
            await CommandExpander.ExpandAsync(new ExpansionRequest(Command("!`false`"), ""), ports, default));
    }

    [Fact]
    public async Task AnUnreadableFile_StaysText_WithAWarning()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "locked.txt");
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, UnixFileMode.None);

        // Root reads a mode-000 file anyway, so the case only exists for an ordinary user.
        if (Environment.UserName == "root") return;

        Assert.Equal("See @locked.txt", await Expand("See @locked.txt"));
        Assert.Contains(_said, m => m.Severity == Severity.Warning && m.Text.Contains("locked.txt"));
    }

    [Fact]
    public async Task APlaceholderInAnAtToken_IsFilled_WithoutARead()
    {
        Assert.Equal("Explain @foo.cs", await Expand("Explain @$1", "foo.cs"));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task APlaceholderInAnAtToken_NeverBecomesAnInclude()
    {
        File.WriteAllText(Path.Combine(_dir, "foo.cs"), "secret");

        Assert.Equal("Explain @foo.cs", await Expand("Explain @$1", "foo.cs"));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task AShellThatCannotStart_IsReportedNotThrown()
    {
        var ports = Ports() with
        {
            RunShell = (_, _) => throw new InvalidOperationException("/bin/sh could not be started."),
        };

        var text = await CommandExpander.ExpandAsync(new ExpansionRequest(Command("!`ls`"), ""), ports, default);

        Assert.Equal("(not run: /bin/sh could not be started.)", text);
        Assert.Contains(_said, m => m.Severity == Severity.Warning);
    }

    [Fact]
    public async Task Requests_AreTheParents_NotASubAgents()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "x");

        await Expand("!`ls`\n@a.cs");

        Assert.Equal(2, _asked.Count);
        Assert.All(_asked, r => Assert.Null(r.Requester));
    }

    /// <summary>A markdown file's own fence must not close the attachment around it.</summary>
    [Fact]
    public async Task AnAttachedFileWithItsOwnFence_StaysInsideTheAttachment()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.md"), "intro\n```bash\nrm -rf x\n```\nafter");

        var text = await Expand("Read @notes.md");

        Assert.Contains("````\nintro\n```bash\nrm -rf x\n```\nafter\n````", text);
    }

    /// <summary>A permission check that was never asked does not allow.</summary>
    [Fact]
    public async Task NoRequests_IsNotAnAllow() =>
        Assert.False(await CommandExpander.AllAllowedAsync([], Ports(), default));

    /// <summary>An @ path outside the folder reaches the gate by its full path, where the policy can see it is outside.</summary>
    [Fact]
    public async Task AnAtPathOutsideTheFolder_IsAskedByItsFullPath()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            await Expand("See @../" + Path.GetFileName(outside));

            Assert.Equal(PermissionKind.FileRead, Assert.Single(_asked).Kind);
            Assert.Contains(outside, Assert.Single(_asked).What);
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task AnAbsoluteAtPath_IsAsked()
    {
        var absolute = Path.Combine(_dir, "abs.txt");
        File.WriteAllText(absolute, "x");

        await Expand("See @" + absolute);

        Assert.Contains(absolute, Assert.Single(_asked).What);
    }

    [Fact]
    public async Task AllArguments_InAShellLine_AreOneQuotedWord()
    {
        await Expand("!`echo $ARGUMENTS`", "a b; c");

        Assert.Equal("echo 'a b; c'", Assert.Single(_ran));
    }

    /// <summary>A failing line that printed nothing says only its exit, with no blank line before it.</summary>
    [Fact]
    public async Task AFailureWithNoOutput_IsJustTheExit()
    {
        var ports = Ports() with { RunShell = (_, _) => Task.FromResult(new ShellLineResult(2, false, "")) };

        Assert.Equal("Result: (exit 2)",
            await CommandExpander.ExpandAsync(new ExpansionRequest(Command("Result: !`false`"), ""), ports, default));
    }

    /// <summary>A binary file is not text for the model: it stays a token, and the user is told.</summary>
    [Fact]
    public async Task ABinaryAtFile_StaysText_WithANotice()
    {
        File.WriteAllBytes(Path.Combine(_dir, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02]);

        var text = await Expand("Describe @logo.png");

        Assert.Equal("Describe @logo.png", text);
        Assert.Contains(_said, m => m.Text.Contains("logo.png") && m.Text.Contains("not text"));
    }

    /// <summary>A large file is cut at the cap, and only that much of it is read.</summary>
    [Fact]
    public async Task ALargeAtFile_IsCut_AtTheCap()
    {
        var cap = CxAgent.Core.Jobs.ToolBindings.MaxToolResultChars;
        File.WriteAllText(Path.Combine(_dir, "big.log"), new string('x', cap * 3));

        var text = await Expand("See @big.log");

        Assert.Contains("[truncated]", text);
        Assert.True(text.Length < cap + 200, $"length {text.Length}");
    }
}
