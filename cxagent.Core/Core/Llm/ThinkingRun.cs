using System.Text;

namespace CxAgent.Core.Llm;

/// <summary>
/// One response's thinking, from both sources — the provider's channel and inline &lt;think&gt; text —
/// so nothing downstream can tell them apart. A run opens at the first thinking and closes at the
/// first answer text or tool call; thinking that resumes after text (a note before a call) opens a
/// second run in the same response.
///
/// <para>CALLBACKS, NOT AN OBSERVER: the agent reports to the session's observer and an aside to
/// its own, and the bookkeeping — when a run starts, when it ends, how long and how many words — is
/// the same for both.</para>
/// </summary>
/// <param name="appended">Called with each piece of visible thinking text.</param>
/// <param name="ended">Called once per run, when it closes.</param>
internal sealed class ThinkingRun(Action<string> appended, Action<ThinkingSummary> ended)
{
    private readonly StringBuilder _text = new();
    private DateTimeOffset? _started;
    private bool _redacted;

    /// <summary>Adds to the open run, opening one if none is. Empty text still opens it: the
    /// provider hid the text, and the model thought all the same.</summary>
    public void Add(string delta, bool redacted = false)
    {
        _started ??= DateTimeOffset.UtcNow;
        _redacted |= redacted;
        if (delta.Length == 0) return;
        _text.Append(delta);
        appended(delta);
    }

    /// <summary>Closes the open run, if there is one.</summary>
    public void End()
    {
        if (_started is not { } started) return;
        var words = _text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        ended(new ThinkingSummary(DateTimeOffset.UtcNow - started, words, Hidden: words == 0, _redacted));
        _started = null;
        _text.Clear();
        _redacted = false;
    }
}
