using CxAgent.Core.Agents;
using CxAgent.Core.Permissions;
using CxAgent.Hosting;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Every question the agent asks goes to every front end showing its session, and the first answer wins.</summary>
public sealed class PromptHubTests
{
    private sealed class Recorder(Func<string, bool>? serves = null) : IPromptResponder
    {
        public List<PendingPrompt> Shown { get; } = [];
        public List<(string Id, Resolution Resolution)> Resolutions { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public bool Serves(string sessionId) => serves?.Invoke(sessionId) ?? true;

        public void Show(PendingPrompt prompt, CancellationToken resolved)
        {
            lock (Shown) { Shown.Add(prompt); Tokens.Add(resolved); }
        }

        public void Resolved(string promptId, Resolution resolution)
        {
            lock (Resolutions) Resolutions.Add((promptId, resolution));
        }
    }

    private static readonly string Scratch = Directory.CreateTempSubdirectory("prompthub-").FullName;

    // THE SESSION A PERMISSION REQUEST BELONGS TO RIDES ON ITS POLICY, the way the gate receives it.
    private static PermissionRequest Request(string sessionId) =>
        new(PermissionKind.Shell, "ls", "ls*", "ls")
        {
            Policy = new PermissionPolicy(Scratch, new PermissionRulesStore(new CxAgent.Core.Storage.AppPaths(Scratch)))
            {
                SessionId = sessionId,
            },
        };

    private static Task<PromptAnswer?> Ask(PromptHub hub, string session) =>
        hub.AskAsync(new TrustPending(PromptHub.NewId(), session, "/x"), CancellationToken.None);

    [Fact]
    public async Task Two_answers_race_and_only_the_first_applies()
    {
        var hub = new PromptHub();
        var a = new Recorder();
        var b = new Recorder();
        hub.Add(a);
        hub.Add(b);

        var asked = Ask(hub, "S");
        var id = a.Shown.Single().Id;
        using var start = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(() => { start.SignalAndWait(); return hub.Answer(id, new TrustAnswer(true), "terminal"); }),
            Task.Run(() => { start.SignalAndWait(); return hub.Answer(id, new TrustAnswer(false), "nick"); }));

        Assert.Equal(1, results.Count(won => won));
        var winner = results[0] ? "terminal" : "nick";
        var answer = Assert.IsType<TrustAnswer>(await asked);
        Assert.Equal(winner == "terminal", answer.Trusted);
        Assert.Equal(winner, a.Resolutions.Single().Resolution.AnsweredBy);
        Assert.Equal(winner, b.Resolutions.Single().Resolution.AnsweredBy);
        Assert.True(a.Tokens.Single().IsCancellationRequested);
    }

    [Fact]
    public async Task Late_responder_sees_pending_prompt()
    {
        var hub = new PromptHub();
        var asked = Ask(hub, "S");
        Assert.Single(hub.PendingFor("S"));

        var late = new Recorder();
        hub.Add(late);

        var shown = late.Shown.Single();
        Assert.True(hub.Answer(shown.Id, new TrustAnswer(true), "web"));
        Assert.True(Assert.IsType<TrustAnswer>(await asked).Trusted);
        Assert.Empty(hub.PendingFor("S"));
    }

    [Fact]
    public async Task Prompts_for_one_session_show_one_at_a_time()
    {
        var hub = new PromptHub();
        var r = new Recorder();
        hub.Add(r);

        var first = Ask(hub, "S");
        var second = Ask(hub, "S");
        var other = Ask(hub, "T");

        Assert.Equal(2, r.Shown.Count);                       // S's first and T's; S's second waits
        hub.Answer(r.Shown[0].Id, new TrustAnswer(true), "x");
        Assert.Equal(3, r.Shown.Count);                       // S's second appears once the first is answered
        hub.Answer(r.Shown[2].Id, new TrustAnswer(false), "x");
        hub.Answer(r.Shown[1].Id, new TrustAnswer(true), "x");

        Assert.True(Assert.IsType<TrustAnswer>(await first).Trusted);
        Assert.False(Assert.IsType<TrustAnswer>(await second).Trusted);
        Assert.True(Assert.IsType<TrustAnswer>(await other).Trusted);
    }

    [Fact]
    public async Task A_question_does_not_wait_behind_a_permission_prompt()
    {
        var hub = new PromptHub();
        var r = new Recorder();
        hub.Add(r);

        _ = hub.AskPermissionAsync(Request("S"), offerTrust: false, CancellationToken.None);
        var question = hub.AskUserFor("S")([new UserQuestion("Which?")], CancellationToken.None);
        var trust = hub.AskTrustAsync("S", "/f", CancellationToken.None);

        // ONLY PERMISSION PROMPTS QUEUE PER SESSION — a background agent's permission must not stall behind the
        // host's unanswered question, nor the other way round.
        Assert.Equal(3, r.Shown.Count);
        hub.Answer(r.Shown[1].Id, new QuestionAnswer(QuestionAnswers.Cancel), "x");
        hub.Answer(r.Shown[2].Id, new TrustAnswer(true), "x");
        await question;
        Assert.True(await trust);
        Assert.Single(hub.PendingFor("S"));
    }

    [Fact]
    public async Task Screens_are_told_before_the_waiter_resumes()
    {
        var hub = new PromptHub();
        Task<PromptAnswer?>? asked = null;
        var waiterDoneWhenToldResolved = new List<bool>();
        var tokenCancelledWhenToldResolved = new List<bool>();
        var r = new Recorder();
        var watcher = new Watcher(() => asked!.IsCompleted, waiterDoneWhenToldResolved, tokenCancelledWhenToldResolved);
        hub.Add(watcher);

        asked = Ask(hub, "S");
        hub.Answer(watcher.LastShown!.Id, new TrustAnswer(true), "web");
        await asked;

        // THE OLD PROMPT COMES DOWN FIRST. A turn resumed before that can raise its next prompt into a tab whose
        // cell is still taken, and that prompt is then never placed.
        Assert.False(waiterDoneWhenToldResolved.Single());
        Assert.True(tokenCancelledWhenToldResolved.Single());
    }

    private sealed class Watcher(Func<bool> waiterDone, List<bool> doneAtResolve, List<bool> cancelledAtResolve) : IPromptResponder
    {
        private CancellationToken _token;
        public PendingPrompt? LastShown { get; private set; }
        public bool Serves(string sessionId) => true;
        public void Show(PendingPrompt prompt, CancellationToken resolved) { LastShown = prompt; _token = resolved; }
        public void Resolved(string promptId, Resolution resolution)
        {
            doneAtResolve.Add(waiterDone());
            cancelledAtResolve.Add(_token.IsCancellationRequested);
        }
    }

    [Fact]
    public void A_resolved_prompt_is_never_shown_to_a_late_responder()
    {
        var hub = new PromptHub();
        _ = Ask(hub, "S");
        var id = hub.PendingFor("S").Single().Id;
        hub.Answer(id, new TrustAnswer(true), "x");

        var late = new Recorder();
        hub.Add(late);
        Assert.Empty(late.Shown);
    }

    [Fact]
    public async Task Cancel_resolves_for_everyone()
    {
        var hub = new PromptHub();
        var r = new Recorder();
        hub.Add(r);
        using var cts = new CancellationTokenSource();

        var asked = hub.AskAsync(new TrustPending(PromptHub.NewId(), "S", "/x"), cts.Token);
        cts.Cancel();

        Assert.Null(await asked);
        Assert.Single(r.Resolutions);
        Assert.Null(r.Resolutions[0].Resolution.Answer);
        Assert.True(r.Tokens.Single().IsCancellationRequested);
        Assert.Empty(hub.PendingFor("S"));
    }

    [Fact]
    public void Responder_only_sees_sessions_it_serves()
    {
        var hub = new PromptHub();
        var onlyS = new Recorder(id => id == "S");
        hub.Add(onlyS);

        _ = Ask(hub, "T");
        _ = Ask(hub, "S");

        Assert.Equal("S", onlyS.Shown.Single().SessionId);
    }

    [Fact]
    public async Task Removed_responder_is_not_shown_more()
    {
        var hub = new PromptHub();
        var r = new Recorder();
        hub.Add(r).Dispose();

        var asked = Ask(hub, "S");
        Assert.Empty(r.Shown);
        hub.Answer(hub.PendingFor("S").Single().Id, new TrustAnswer(true), "x");
        await asked;
    }

    [Fact]
    public async Task Permission_question_and_trust_all_go_through_the_same_race()
    {
        var hub = new PromptHub();
        var r = new Recorder();
        hub.Add(r);

        var permission = hub.AskPermissionAsync(Request("S"), offerTrust: true, CancellationToken.None);
        var p = Assert.IsType<PermissionPending>(r.Shown.Single());
        Assert.True(p.OfferTrust);
        hub.Answer(p.Id, new PermissionAnswer(PermissionChoice.Always), "web");
        Assert.Equal(PermissionChoice.Always, await permission);

        var question = hub.AskUserFor("S")([new UserQuestion("Pick one?")], CancellationToken.None);
        var q = Assert.IsType<QuestionPending>(r.Shown[1]);
        var answers = QuestionAnswers.Cancel;
        hub.Answer(q.Id, new QuestionAnswer(answers), "web");
        Assert.Equal(answers, await question);

        var trust = hub.AskTrustAsync("S", "/folder", CancellationToken.None);
        var t = Assert.IsType<TrustPending>(r.Shown[2]);
        Assert.Equal("/folder", t.Folder);
        hub.Answer(t.Id, new TrustAnswer(true), "terminal");
        Assert.True(await trust);
    }

    [Fact]
    public async Task A_cancelled_permission_is_a_deny()
    {
        var hub = new PromptHub();
        using var cts = new CancellationTokenSource();
        var permission = hub.AskPermissionAsync(Request("S"), offerTrust: false, cts.Token);
        cts.Cancel();
        Assert.Equal(PermissionChoice.Deny, await permission);
    }
}
