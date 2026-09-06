using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a session was shown, kept so another front end can be shown the same.
///
/// <para>THE PROPERTY WORTH PINNING IS THE PAGING CONTRACT, because it is a decision about the wire
/// rather than about the disk: a client asks for a window of N, and what N counts — messages, not
/// tokens — is what makes the answer renderable.</para>
/// </summary>
public class TranscriptStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-transcript-" + Guid.NewGuid().ToString("N"));

    private readonly TranscriptStore _store;

    public TranscriptStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TranscriptStore(new AppPaths(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ItKeepsWhatItWasGiven()
    {
        _store.Append("s1", 1, "message", "user", "hello");

        var entry = Assert.Single(_store.Window("s1"));
        Assert.Equal("message", entry.Kind);
        Assert.Equal("user", entry.Role);
        Assert.Equal("hello", entry.Body);
    }

    /// <summary>
    /// A MESSAGE IS ONE ROW THAT GROWS. Assistant text arrives token by token, so a row per event
    /// would be thousands per turn — and a client asking for "the last twenty" would get twenty
    /// TOKENS, which renders as a fragment of one sentence.
    /// </summary>
    [Fact]
    public void AGrowingMessageStaysOneRow()
    {
        _store.Append("s1", 1, "message", "assistant", "Hel");
        _store.Append("s1", 1, "message", "assistant", "Hello");
        _store.Append("s1", 1, "message", "assistant", "Hello there");

        var entry = Assert.Single(_store.Window("s1"));
        Assert.Equal("Hello there", entry.Body);
    }

    /// <summary>READING ORDER, oldest first, whatever order the rows went in — a replay that
    /// reordered a token stream would render nonsense.</summary>
    [Fact]
    public void AWindowReadsOldestFirst()
    {
        _store.Append("s1", 3, "message", "assistant", "third");
        _store.Append("s1", 1, "message", "user", "first");
        _store.Append("s1", 2, "message", "assistant", "second");

        Assert.Equal(["first", "second", "third"],
            _store.Window("s1").Select(e => e.Body));
    }

    /// <summary>THE LATEST WINDOW IS THE END OF THE CONVERSATION, because that is what a client
    /// attaching shows first.</summary>
    [Fact]
    public void AWindowTakesTheLatestWhenNoCursorIsGiven()
    {
        for (var i = 1; i <= 10; i++) _store.Append("s1", i, "message", "user", $"m{i}");

        Assert.Equal(["m8", "m9", "m10"], _store.Window("s1", limit: 3).Select(e => e.Body));
    }

    /// <summary>AND SCROLLING BACK ASKS FOR WHAT CAME BEFORE. Nothing is discarded at write time,
    /// so the history is whole and only the transfer is bounded.</summary>
    [Fact]
    public void ScrollingBackPagesThroughTheWholeHistory()
    {
        for (var i = 1; i <= 10; i++) _store.Append("s1", i, "message", "user", $"m{i}");

        var last = _store.Window("s1", limit: 3);
        var previous = _store.Window("s1", before: last[0].Seq, limit: 3);

        Assert.Equal(["m5", "m6", "m7"], previous.Select(e => e.Body));
    }

    /// <summary>ONE SESSION'S TRANSCRIPT IS ITS OWN — the whole point of a store several sessions
    /// share.</summary>
    [Fact]
    public void SessionsDoNotSeeEachOther()
    {
        _store.Append("s1", 1, "message", "user", "alpha");
        _store.Append("s2", 1, "message", "user", "beta");

        Assert.Equal("alpha", Assert.Single(_store.Window("s1")).Body);
        Assert.Equal("beta", Assert.Single(_store.Window("s2")).Body);
    }

    [Fact]
    public void ForgettingOneLeavesTheOther()
    {
        _store.Append("s1", 1, "message", "user", "alpha");
        _store.Append("s2", 1, "message", "user", "beta");

        _store.Forget("s1");

        Assert.Empty(_store.Window("s1"));
        Assert.Single(_store.Window("s2"));
    }

    /// <summary>It survives a new store over the same directory, which is what makes replay work
    /// across a restart at all.</summary>
    [Fact]
    public void ItPersists()
    {
        _store.Append("s1", 1, "message", "user", "remembered");

        var reopened = new TranscriptStore(new AppPaths(_dir));

        Assert.Equal("remembered", Assert.Single(reopened.Window("s1")).Body);
    }
}
