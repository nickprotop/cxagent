using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Session tabs are NOT a prefix of the tab strip.
///
/// <para>THE SHARPEST BUG THIS WINDOW HAS HAD, and the one hardest to notice: a file or shell tab
/// appends to the same strip, so `/open` followed by `/sessions new` gives strip
/// [chat, file, session] against a session list of two. Indexing one by the other's position
/// silently returned the FIRST session while the user was looking at the second — typed goals ran in
/// the wrong conversation, or vanished with no echo and no error.</para>
///
/// <para>Every earlier multi-session bug was "invisible with one session, wrong with two". This one
/// is a different axis: "a session plus any other kind of tab, opened first".</para>
/// </summary>
public class SessionTabIndexTests : IDisposable
{
    private readonly EditorHostFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private Session OpenSecondSession()
    {
        var session = new Session(_fixture.WorkingDirectory);
        _fixture.Host.Main.AddSessionTab(session);
        return session;
    }

    private void OpenAFileTab()
    {
        var path = Path.Combine(_fixture.WorkingDirectory, "in-the-way.txt");
        File.WriteAllText(path, "x\n");
        CxAgent.UI.FileTab.Open(_fixture.Host, CxAgent.UI.FileLoad.TryLoad(path, out _)!);
    }

    /// <summary>
    /// A SESSION OPENED AFTER A FILE TAB IS STILL THE ACTIVE SESSION. This is the whole bug: without
    /// the strip mapping, the window answered with session one while showing session two.
    /// </summary>
    [Fact]
    public void ASessionOpenedAfterAFileTabIsTheActiveOne()
    {
        OpenAFileTab();
        var second = OpenSecondSession();

        Assert.Same(second, _fixture.Host.Main.ActiveSession);
    }

    /// <summary>And the window knows it is a conversation — Escape's scope turns on this, so getting
    /// it wrong makes Escape refuse to cancel a turn the user is watching.</summary>
    [Fact]
    public void ASessionAfterAFileTabIsStillAChatTab()
    {
        OpenAFileTab();
        OpenSecondSession();

        Assert.True(_fixture.Host.Main.ChatTabIsActive);
    }

    /// <summary>A FILE TAB IS NOT A CONVERSATION, however many sessions exist around it.</summary>
    [Fact]
    public void AFileTabIsNotAChatTab()
    {
        OpenSecondSession();
        OpenAFileTab();

        Assert.False(_fixture.Host.Main.ChatTabIsActive);
    }

    /// <summary>
    /// CLOSING A TAB BEFORE A SESSION MOVES IT, and the mapping has to move with it — left alone it
    /// would name a strip position now holding something else.
    /// </summary>
    [Fact]
    public void ClosingAFileTabKeepsTheSessionAddressable()
    {
        OpenAFileTab();
        var second = OpenSecondSession();

        // The file tab sits at index 1; closing it shifts the session from 2 to 1.
        _fixture.Host.Main.CloseTab(1);

        Assert.Same(second, _fixture.Host.Main.ActiveSession);
        Assert.True(_fixture.Host.Main.ChatTabIsActive);
    }
}
