using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;

namespace CxAgent.Tests;

/// <summary>
/// A job sink over a headless screen, shared by every test class that needs one.
///
/// <para>ONE COPY, because it is the sink's constructor written down: a second would be a second
/// thing to keep in step with it, and these tests exist to catch exactly that kind of drift.</para>
///
/// <para>A UNIT TEST HAS NO UI LOOP to drain the enqueues, so only the synchronous <c>…Now</c> halves
/// of the sink's paths are reachable from here — which is why those splits exist at all.</para>
/// </summary>
internal static class SinkFixture
{
    public static (InlineJobSink Sink, ChatTranscriptControl Chat) Build()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var chat = new ChatTranscriptControl();
        return (new InlineJobSink(system, chat), chat);
    }
}
