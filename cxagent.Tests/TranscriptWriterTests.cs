using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Where the gate's own words go.
///
/// <para>ONE GATE SERVES EVERY SESSION and writes its denial echoes through this. It took a
/// <c>ChatTranscriptControl</c>, and the composition root passed <c>mainWindow.Chat</c> — a PROPERTY
/// over the active tab, evaluated ONCE at startup when only one tab exists, then held for the life
/// of the process. So every session's "denied: …" and "trusted this folder" landed in the FIRST
/// session's transcript: a security notice filed against a conversation that did not produce it,
/// and missing from the one that did.</para>
///
/// <para><b>WHAT IS PINNED HERE IS THE SHAPE, NOT THE DELIVERY.</b> Every write is marshalled
/// through <c>EnqueueOnUIThread</c> and the control is resolved INSIDE that action — later than a
/// test can observe, and deliberately so: the tab in front at render time is the right answer, not
/// the tab in front when the gate spoke. There is no pump seam on the window system, so a test
/// cannot make the queue run; asserting on rendered rows here would assert on the scheduler.</para>
///
/// <para>So the delivery is drive-verified — two sessions, a denial in each, each landing in its own
/// transcript — and what a test can hold is the constructor: it must take a DELEGATE, because a
/// control taken by value is the bug, and it is the kind of change a later edit makes without
/// noticing.</para>
/// </summary>
public class TranscriptWriterTests
{
    /// <summary>
    /// THE WRITER TAKES A WAY TO FIND THE TRANSCRIPT, not a transcript.
    ///
    /// <para>The distinction is the whole defect. A `ChatTranscriptControl` parameter accepts
    /// `mainWindow.Chat` — a property call, evaluated once — and nothing about the call site says it
    /// has been frozen. A `Func` cannot be frozen by accident.</para>
    /// </summary>
    [Fact]
    public void TheWriterIsBuiltOverALookupRatherThanAControl()
    {
        var parameters = typeof(TranscriptWriter)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.Contains(parameters, p => p.ParameterType == typeof(Func<ChatTranscriptControl>));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(ChatTranscriptControl));
    }

}
