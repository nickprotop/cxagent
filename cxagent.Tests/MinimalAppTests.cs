using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE SMALLEST APP THAT DRIVES AN AGENT, kept as a test because the question it answers — "how much
/// does a second front end have to write?" — is answered wrong by reading the composition root, which
/// is 1,600 lines of terminal. Almost none of that is required.
///
/// <para>It is also the only executable form of the claim. A README saying "five calls" rots the
/// first time a required parameter is added; this stops compiling.</para>
/// </summary>
public class MinimalAppTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "minimal-" + Guid.NewGuid().ToString("N"));

    public MinimalAppTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>
    /// Four calls, and every one of them is load-bearing:
    /// <list type="number">
    ///   <item>the process's shared services — logs, resume store, usage history</item>
    ///   <item>which model to talk to</item>
    ///   <item>where output goes, which no library can guess for its host</item>
    ///   <item>the session itself, over a folder</item>
    ///   <item>a goal</item>
    /// </list>
    ///
    /// <para>THE WORKING MODE IS NOT AMONG THEM ANY MORE. Open required one, and every caller with
    /// no opinion passed AgentMode.Single — which is exactly WorkingMode.Default, and exactly what
    /// an agent picks when nobody sets one. A required parameter whose only sane value is the
    /// default is a caller repeating something back to a library that already knew it.</para>
    ///
    /// <para>NOT TWO. The two that cannot collapse into a Create+Open pair are the provider and the
    /// ports: a manager that invented a provider would be choosing the user's model, and one that
    /// invented an observer would be choosing where a front end it cannot see puts its text. Both are
    /// arguments precisely because they are the caller's to answer.</para>
    /// </summary>
    [Fact]
    public async Task AnAppIsFourCalls()
    {
        var manager = SessionManager.Create(ProcessSetup.For(_dir));
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "hi back", StopReason = "end_turn" });
        var sink = new BufferedChatSink();

        var session = manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() });

        // THE REAL SUBMIT, not the test helper: this is the call a consuming app writes, and its
        // shape is part of what this test claims. Started carries the turn; the caller awaits it.
        var submitted = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("hello"));
        await submitted.Turn;

        // THE REPLY REACHED THE CALLER, which is the whole claim. Asserting the host is non-null
        // would only prove the wiring returned an object; Body is what the provider said, arriving
        // through the observer this app supplied — the round trip, end to end.
        Assert.NotEmpty(sink.Body);
        manager.Dispose();
    }

    // THREE, WHEN THE PROCESS'S OWN CONFIGURATION WILL DO. The manager holds the config directory,
    // so it reads config.json from there and every session runs on that unless one says otherwise —
    // which is what a caller assumes. The four-call form above exists for the caller with an opinion
    // about which model, which is every real front end and every test that wants a fake.
    [Fact]
    public void AnAppOverItsOwnConfigIsThree()
    {
        using var manager = SessionManager.Create(ProcessSetup.For(_dir));

        // NO config.json HERE, so this resolved to "no provider, and here is why" rather than
        // throwing — which is what lets a caller check and report instead of catching.
        Assert.False(manager.Config.HasProvider);
        Assert.NotEmpty(manager.Config.Errors);
    }

    /// <summary>
    /// The gate is a hook, not a requirement — which is what makes the five calls above possible at
    /// all. A headless caller has nobody to ask, so it passes nothing and its sessions are ungated.
    ///
    /// <para>Pinned because the alternative was tempting and wrong: defaulting to DenyAll would make
    /// every tool fail in a host that simply has no UI, and defaulting to AllowAll would make a
    /// missing argument silently permissive. Null means "no gating", stated once, here.</para>
    /// </summary>
    [Fact]
    public void NoGateIsNeededToDriveASession()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        Assert.Null(manager.Shared.Gate);

        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() });

        Assert.NotNull(session.Host);
    }
}
