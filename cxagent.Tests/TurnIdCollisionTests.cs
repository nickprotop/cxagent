using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Turn ids must be unique across one session's transcript.
///
/// <para>ChatMessageId's own summary says they are "MINTED BY THE SESSION, not by whatever is
/// observing it" — the fix for observers generating their own, which made two observers disagree
/// about which turn was which. But AgentHost and Agent each hold a private <c>_nextTurnId</c> and
/// mint into the SAME sink, so the claim is aspirational: the host mints 1,2 for a turn and the
/// agent independently mints 1,2,3 within it.</para>
///
/// <para>A collision is not cosmetic. ChatTranscriptSink keys its row map by <c>id.Value</c>, so the
/// second use of an id overwrites the row the first is still streaming into.</para>
/// </summary>
public class TurnIdCollisionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "turnid-" + Guid.NewGuid().ToString("N"));

    public TurnIdCollisionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private sealed class IdRecorder : ISessionObserver
    {
        public List<long> Ids { get; } = [];
        private void Note(ChatMessageId id) { lock (Ids) Ids.Add(id.Value); }

        public void UserTurnAdded(ChatMessageId id, string text) => Note(id);
        public void AssistantTurnBegan(ChatMessageId id) => Note(id);
        public void AssistantTextAppended(ChatMessageId id, string text) { }
        public void AssistantReasoningAppended(ChatMessageId id, string text) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string label) { }
        public void Said(string markup) { }
        public void Failed(string message) { }
    }

    // A CHILD WRITES TO ITS OWN SINK, so its ids cannot collide with the parent's — verified rather
    // than assumed, because the fix hands the host's minter to ITS agent and a child that shared the
    // parent's transcript would need the same treatment. It does not: SubAgentFactory gives every
    // child a buffered sink of its own, which is why the fallback counter is right there.
    [Fact]
    public async Task AChildNumbersItsOwnTranscript_WithoutTouchingTheParents()
    {
        var recorder = new IdRecorder();
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = recorder, Tools = new BufferedJobPanel() },
            AgentMode.FanOut);

        await session.SendAndWait("hello");

        var duplicates = recorder.Ids.GroupBy(i => i).Where(g => g.Count() > 1).ToList();
        Assert.True(duplicates.Count == 0,
            $"parent transcript has duplicate ids: [{string.Join(", ", recorder.Ids)}]");
    }

    [Fact]
    public async Task OneTurn_DoesNotMintTheSameIdTwice()
    {
        var recorder = new IdRecorder();
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = recorder, Tools = new BufferedJobPanel() },
            AgentMode.Single);

        await session.SendAndWait("hello");

        var duplicates = recorder.Ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(duplicates.Count == 0,
            $"ids minted more than once: [{string.Join(", ", duplicates)}] from [{string.Join(", ", recorder.Ids)}]");
    }
}
