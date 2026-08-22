namespace CxAgent.Core.Commands;

/// <summary>
/// How loudly Core is saying something.
///
/// <para>SEPARATE FROM THE TEXT, WHICH IS THE WHOLE POINT. Core used to write "[yellow]could not
/// save this rule[/]" — a colour, chosen by a library with no idea what theme it is rendering into,
/// in a dialect it could not itself parse. A front end can map these three onto whatever it uses for
/// emphasis; it could not map a colour onto anything but that colour.</para>
/// </summary>
public enum Severity
{
    /// <summary>An ordinary statement: a mode change, an answer, a note.</summary>
    Info,

    /// <summary>Something did not work, and nothing was lost by it.</summary>
    Warning,

    /// <summary>A fault. <c>BufferedChatSink.Errors</c> is the messages at this level.</summary>
    Error,
}

/// <summary>
/// What Core has to say, and how loudly.
///
/// <para>ONE TYPE FOR BOTH CHANNELS. Core's words reach a user two ways — the observer's
/// <c>Said</c>, and the <c>Reply</c> a command returns — and they already converge on
/// <c>Session.Say</c>. Giving severity to only one of them would have left the channel that carries
/// every actual warning unable to say so.</para>
///
/// <para>THE TEXT IS MARKDOWN. Not this library's own dialect: the models speak markdown, the
/// transcript already renders it for their replies, and a consumer who renders nothing still gets
/// something legible. See the design doc for why the previous format's own examples stripped it.</para>
/// </summary>
/// <param name="Text">Markdown. Interpolated values are escaped during rendering.</param>
/// <param name="Severity">How loudly. Defaults to <see cref="Severity.Info"/>.</param>
public readonly record struct Message(string Text, Severity Severity = Severity.Info)
{
    /// <summary>A plain string is an ordinary statement — the common case, kept short.</summary>
    /// <param name="text">The markdown to say.</param>
    public static implicit operator Message(string text) => new(text);
}
