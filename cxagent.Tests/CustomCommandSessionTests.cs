using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A typed /name runs as a turn: the row first, then expansion, then the model.</summary>
public class CustomCommandSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "custom-cmd-" + Guid.NewGuid().ToString("N"));
    public CustomCommandSessionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private sealed record Wired(SessionManager Manager, Session Session, BufferedChatSink Observer, MockLlmProvider Model);

    private Wired Open(IPermissionGate? gate = null)
    {
        var manager = SessionManager.Create(new AppPaths(_dir), buildGate: _ => gate ?? PermissionGate.AllowAll);
        var observer = new BufferedChatSink();
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "done again", StopReason = "end_turn" });
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = observer, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return new Wired(manager, session, observer, model);
    }

    private void CommandFile(string name, string text)
    {
        var folder = Path.Combine(_dir, ".cxagent", "commands");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".md"), text);
    }

    [Fact]
    public async Task ATypedCommand_SendsTheExpansion_AndShowsWhatWasTyped()
    {
        CommandFile("review", "Review PR $1 carefully.");
        var w = Open();
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/review 42"));
        await started.Turn;

        Assert.Contains("> /review 42", w.Observer.Transcript);
        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Review PR 42 carefully.");
    }

    /// <summary>
    /// The session list names a command's session by what was typed, not by the expanded prompt the
    /// conversation stores — "/review 42" is what the user would recognise.
    /// </summary>
    [Fact]
    public async Task ACommandsSession_IsTitledByWhatWasTyped()
    {
        CommandFile("review", "Review PR $1 carefully, with a long briefing that nobody would recognise.");
        var w = Open();
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/review 42"));
        await started.Turn;

        Assert.Equal("/review 42", new CxAgent.Core.Storage.FolderSessionStore(new AppPaths(_dir)).List(_dir)[0].Title);
    }

    /// <summary>
    /// An argument may run over several lines — a pasted stack trace is the common case — and the command
    /// still runs with all of it. A newline in the line used to make it "Unknown command".
    /// </summary>
    [Fact]
    public async Task AMultilineArgument_IsPassedWhole()
    {
        CommandFile("look", "Look at this: $ARGUMENTS");
        var w = Open();
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/look first\nsecond\nthird"));
        await started.Turn;

        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Look at this: first\nsecond\nthird");
    }

    /// <summary>
    /// A command whose argument is a pasted token: the model gets the text, the row and the session title
    /// show the line as typed, and the paste is kept with the message for a readable export.
    /// </summary>
    [Fact]
    public async Task ACommandWithAPaste_EchoesTheLabel_AndKeepsThePaste()
    {
        CommandFile("look", "Look at this: $ARGUMENTS");
        var w = Open();
        using var _ = w.Manager;
        var paste = new PastedText("[Pasted text #1 +3 lines]", "a\nb\nc");

        var started = Assert.IsType<Session.SubmitOutcome.Started>(
            w.Session.Submit("/look a\nb\nc", "/look [Pasted text #1 +3 lines]", attachments: Attachments.Of([paste])));
        await started.Turn;

        Assert.Contains("> /look [Pasted text #1 +3 lines]", w.Observer.Transcript);
        var sent = Assert.Single(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Look at this: a\nb\nc");
        Assert.Equal("/look [Pasted text #1 +3 lines]", sent.Shown);
        Assert.Equal([paste], sent.Pastes!);
    }

    /// <summary>
    /// A command typed while a turn runs waits for it, and runs with what it carried: the line as typed,
    /// and its image, not only the label in its text.
    /// </summary>
    [Fact]
    public async Task AHeldCommand_RunsWithItsImage()
    {
        CommandFile("quiet", "!`true`");
        CommandFile("look", "Look at this: $ARGUMENTS");
        var asked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var w = Open(new HeldGate(asked, release));
        using var _ = w.Manager;
        var image = new AttachedImage("[Image #1 4×2]", "h", 4, 2) { Png = [1] };

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/quiet"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<Session.SubmitOutcome.Queued>(
            w.Session.Submit("/look [Image #1 4×2]", attachments: new Attachments([], [image])));
        release.SetResult();
        await started.Turn.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100 && w.Model.LastMessages?.Any(m => m.Images is not null) != true; i++) await Task.Delay(50);

        var sent = Assert.Single(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Look at this: [Image #1 4×2]");
        Assert.Equal([image], sent.Images!);
    }

    /// <summary>A plain message with a paste keeps it the same way.</summary>
    [Fact]
    public async Task AMessageWithAPaste_KeepsThePasteWithTheMessage()
    {
        var w = Open();
        using var _ = w.Manager;
        var paste = new PastedText("[Pasted text #1 +3 lines]", "a\nb\nc");

        var started = Assert.IsType<Session.SubmitOutcome.Started>(
            w.Session.Submit("fix a\nb\nc", "fix [Pasted text #1 +3 lines]", attachments: Attachments.Of([paste])));
        await started.Turn;

        var sent = Assert.Single(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "fix a\nb\nc");
        Assert.Equal("fix [Pasted text #1 +3 lines]", sent.Shown);
        Assert.Equal([paste], sent.Pastes!);
    }

    /// <summary>A newline straight after the name separates it from the arguments, as a space does.</summary>
    [Fact]
    public async Task ANewline_SeparatesTheNameFromItsArguments()
    {
        CommandFile("look", "Look at this: $ARGUMENTS");
        var w = Open();
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/look\nfirst\nsecond"));
        await started.Turn;

        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Look at this: first\nsecond");
    }

    /// <summary>A tab after the name separates it from the arguments, as a space does.</summary>
    [Fact]
    public async Task ATab_SeparatesTheNameFromItsArguments()
    {
        CommandFile("review", "Review PR $1 carefully.");
        var w = Open();
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/review\t42"));
        await started.Turn;

        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Review PR 42 carefully.");
    }

    [Fact]
    public void ABuiltIn_StillWins_OverAFileOfItsName()
    {
        CommandFile("clear", "Not the real clear.");
        var w = Open();
        using var _ = w.Manager;

        Assert.IsType<Session.SubmitOutcome.Handled>(w.Session.Submit("/clear"));
        Assert.Null(w.Session.FindTyped("/clear"));
    }

    [Fact]
    public void AnUnknownName_IsStillUnknown()
    {
        var w = Open();
        using var _ = w.Manager;

        Assert.IsType<Session.SubmitOutcome.Handled>(w.Session.Submit("/nothing-here"));
        Assert.Contains(w.Observer.Notices, n => n.Contains("Unknown command"));
    }

    /// <summary>Only the user's own typing expands: a plugin's goal or a delivery stays text.</summary>
    [Fact]
    public void APluginGoal_StartingWithAName_IsNotExpanded()
    {
        CommandFile("deploy", "!`touch should-not-exist`");
        var w = Open();
        using var _ = w.Manager;

        w.Session.Submit("/deploy", origin: TurnOriginator.Plugin("p"));

        Assert.False(File.Exists(Path.Combine(_dir, "should-not-exist")));
    }

    [Fact]
    public async Task CancellingDuringExpansion_HandsTheTypedLineBack_AndSendsNothing()
    {
        CommandFile("slow", "!`sleep 30`");
        var asked = new TaskCompletionSource();
        var gate = new BlockingGate(asked);
        var w = Open(gate);
        using var _ = w.Manager;
        string? handedBack = null;
        w.Session.Cancelled += (text, _) => handedBack = text;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/slow"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(w.Session.CancelTurn());
        await started.Turn;

        Assert.Equal("/slow", handedBack);
        Assert.Null(w.Model.LastMessages);
        Assert.Equal(SendOutcome.Cancelled, await started.Outcome);
    }

    /// <summary>
    /// A cancel hands the queued line back and takes all of it out of the queue — its words as shown and
    /// its images too — so the next steer is only what is typed next, not a second copy of the screenshot.
    /// </summary>
    [Fact]
    public async Task ACancel_TakesTheQueuedLinesImagesWithIt()
    {
        CommandFile("slow", "!`sleep 30`");
        var asked = new TaskCompletionSource();
        var w = Open(new BlockingGate(asked));
        using var _ = w.Manager;
        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/slow"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var image = new AttachedImage("[Image #1 4×2]", "h", 4, 2) { Png = [1] };
        w.Session.Submit("see [Image #1 4×2]", attachments: new Attachments([], [image]));

        Assert.True(w.Session.CancelTurn());
        await started.Turn;
        w.Session.Steer("also X");
        var next = w.Session.TakeSteer();

        Assert.Equal("also X", next!.Shown);
        Assert.True(next.Attachments is null or { IsEmpty: true });
    }

    /// <summary>Two lines typed while a turn runs go as one message, carrying both lines' images in order.</summary>
    [Fact]
    public async Task TwoQueuedLines_CarryBothImages_InOrder()
    {
        CommandFile("slow", "!`sleep 30`");
        var asked = new TaskCompletionSource();
        var w = Open(new BlockingGate(asked));
        using var _ = w.Manager;
        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/slow"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var one = new AttachedImage("[Image #1 4×2]", "a", 4, 2) { Png = [1] };
        var two = new AttachedImage("[Image #2 4×2]", "b", 4, 2) { Png = [2] };

        w.Session.Submit("see [Image #1 4×2]", attachments: new Attachments([], [one]));
        w.Session.Submit("and [Image #2 4×2]", attachments: new Attachments([], [two]));
        var queued = w.Session.TakeSteer();
        w.Session.CancelTurn();
        await started.Turn;

        Assert.Equal("see [Image #1 4×2]\nand [Image #2 4×2]", queued!.Shown);
        Assert.Equal([one, two], queued.Attachments!.Images);
    }

    [Fact]
    public async Task AnEmptyExpansion_SendsNothing_AndSaysSo()
    {
        CommandFile("empty", "---\ndescription: nothing\n---\n");
        var w = Open();
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/empty"));
        await started.Turn;

        Assert.Null(w.Model.LastMessages);
        Assert.Contains(w.Observer.Notices, n => n.Contains("nothing to send"));
    }

    /// <summary>
    /// The real gate never throws on cancel: PermissionDecider answers a cancelled prompt with a deny.
    /// The expansion must still end as a cancel, not as a refused line followed by a model call.
    /// </summary>
    [Fact]
    public async Task CancellingDuringAPrompt_ThatAnswersDeny_StillSendsNothing()
    {
        CommandFile("slow", "!`sleep 30`");
        var asked = new TaskCompletionSource();
        var w = Open(new DenyOnCancelGate(asked));
        using var _ = w.Manager;
        string? handedBack = null;
        w.Session.Cancelled += (text, _) => handedBack = text;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/slow"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(w.Session.CancelTurn());
        await started.Turn;

        Assert.Equal("/slow", handedBack);
        Assert.Null(w.Model.LastMessages);
        Assert.Equal(SendOutcome.Cancelled, await started.Outcome);
        Assert.DoesNotContain(w.Observer.Notices, n => n.Contains("not run"));
        Assert.DoesNotContain(w.Session.Host!.Context.Messages, m => m.Role == "user");
    }

    /// <summary>An empty expansion ends its lap, not the turn: what was steered meanwhile still runs.</summary>
    [Fact]
    public async Task AnEmptyExpansion_StillRunsWhatWasSteeredDuringIt()
    {
        CommandFile("quiet", "!`true`");
        var asked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var w = Open(new HeldGate(asked, release));
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/quiet"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<Session.SubmitOutcome.Queued>(w.Session.Submit("follow up"));
        release.SetResult();
        await started.Turn.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(w.Observer.Notices, n => n.Contains("nothing to send"));
        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "follow up");
    }

    /// <summary>Text the app queued is not a reason to call the model when the command itself said nothing.</summary>
    [Fact]
    public async Task AnEmptyExpansion_LeavesInjectedTextQueued()
    {
        CommandFile("empty", "---\ndescription: nothing\n---\n");
        var w = Open();
        using var _ = w.Manager;
        w.Session.Inject("app note");

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/empty"));
        await started.Turn;

        Assert.Null(w.Model.LastMessages);
        Assert.Contains(w.Observer.Notices, n => n.Contains("nothing to send"));
        Assert.Equal("app note", w.Session.TakeInjected());
    }

    /// <summary>A command reaching a busy session is held, never steered as its raw /name text.</summary>
    [Fact]
    public void ACommandOnABusySession_IsNotSteeredAsText()
    {
        CommandFile("review", "Review PR $1 carefully.");
        var w = Open();
        using var _ = w.Manager;
        var entry = Assert.IsType<CxAgent.Core.Skills.Invocable>(w.Session.FindTyped("/review 1"));

        using (w.Session.PretendBusyForTesting())
            Assert.IsType<Session.SubmitOutcome.Queued>(w.Session.RunCommand(new TypedCommand("/review 1", entry)));

        Assert.Equal("/review 1", w.Session.HeldCommand);
        Assert.Null(w.Session.PendingSteer);
    }

    /// <summary>
    /// A command's requests say they came from a command, so a front end can make Escape on their
    /// prompt stop the command rather than skip one line of it.
    /// </summary>
    [Fact]
    public async Task ACommandsRequests_AreMarkedAsRaisedByACommand()
    {
        CommandFile("both", "!`echo hi`\nSee @notes.txt");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "n");
        var gate = new RecordingGate();
        var w = Open(gate);
        using var _ = w.Manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("/both"));
        await started.Turn;

        Assert.Equal(2, gate.Seen.Count);
        Assert.All(gate.Seen, r => Assert.True(r.RaisedByCommand));
    }

    private sealed class RecordingGate : IPermissionGate
    {
        public List<PermissionRequest> Seen { get; } = [];

        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            lock (Seen) Seen.Add(request);
            return Task.FromResult(PermissionOutcome.Allow);
        }
    }

    /// <summary>Like PermissionDecider: waits for the answer or the cancel, and answers a cancel with a deny.</summary>
    private sealed class DenyOnCancelGate(TaskCompletionSource asked) : IPermissionGate
    {
        public async Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            asked.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return PermissionOutcome.ByUser;
        }
    }

    /// <summary>Holds the first request until the test releases it, then allows.</summary>
    private sealed class HeldGate(TaskCompletionSource asked, TaskCompletionSource release) : IPermissionGate
    {
        public async Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            asked.TrySetResult();
            await release.Task.WaitAsync(ct);
            return PermissionOutcome.Allow;
        }
    }

    /// <summary>A gate that holds every request open until the turn is cancelled.</summary>
    private sealed class BlockingGate(TaskCompletionSource asked) : IPermissionGate
    {
        public async Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return PermissionOutcome.ByUser;
        }
    }
}
