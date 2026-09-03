namespace CxAgent.UI;

/// <summary>
/// What a typed <c>/open</c> means, before any window exists.
///
/// <para>SEPARATED FROM THE HANDLER because the handler needs a window system and a session and so
/// can only be driven by hand, while the decision it makes first is a pure function of two strings.
/// <c>ShellCommandLine</c> is the same split for the same reason.</para>
/// </summary>
public sealed record OpenTarget(bool ShowPicker, string? Path)
{
    /// <summary>
    /// Reads an argument as either "show me the picker" or a path.
    ///
    /// <para>BARE MEANS THE PICKER, not an error and not a usage line: someone who types
    /// <c>/open</c> wants to open something, and the app knows how to ask which.</para>
    ///
    /// <para>RESOLVED AGAINST THE SESSION'S DIRECTORY so a relative path means what it means
    /// everywhere else in the app. An absolute one is taken as given — the same latitude <c>@</c>
    /// has, because completion is not the permission boundary and reading is gated separately.</para>
    /// </summary>
    public static OpenTarget For(string argument, string workingDirectory)
    {
        var trimmed = argument.Trim();
        if (trimmed.Length == 0) return new OpenTarget(ShowPicker: true, Path: null);

        // QUOTES COME OFF, because the composer passes the argument through verbatim and a path with
        // a space in it is typed the way a shell would take it.
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];

        return new OpenTarget(ShowPicker: false,
            Path: System.IO.Path.GetFullPath(System.IO.Path.Combine(workingDirectory, trimmed)));
    }
}
