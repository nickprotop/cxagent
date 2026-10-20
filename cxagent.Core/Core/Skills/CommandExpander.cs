using System.Text;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Skills;

/// <summary>Which entry, with what the user typed after its name.</summary>
internal sealed record ExpansionRequest(Invocable Entry, string Arguments);

/// <summary>What expansion needs from the session it runs in.</summary>
/// <param name="WorkingDirectory">Where shell lines run and <c>@</c> paths resolve.</param>
/// <param name="Allow">Asks the session's permission gate; true when the action may go ahead.</param>
/// <param name="Say">The session's own notices, under the user's row.</param>
internal sealed record ExpansionPorts(string WorkingDirectory,
    Func<PermissionRequest, CancellationToken, Task<bool>> Allow, Action<Message> Say)
{
    /// <summary>Runs one shell line; <see cref="ShellLine.RunAsync"/> when null. A seam for tests.</summary>
    public Func<string, CancellationToken, Task<ShellLineResult>>? RunShell { get; init; }
}

/// <summary>
/// Turns a command file or a typed skill into the prompt the model receives.
///
/// <para>THE ORDER IS THE SAFETY. Shell lines and <c>@</c> files are found in the file's own text
/// (<see cref="CommandText.Segments"/>) BEFORE any argument is filled in, so what the user typed can
/// fill a placeholder but can never become a shell line or a file include.</para>
///
/// <para>EVERY SHELL LINE AND EVERY FILE GOES THROUGH THE GATE, exactly as the model's own call would.
/// A command file is text from wherever the user cloned it; its shell lines get the same prompt, the
/// same rules and the same headless refusal as <c>run_shell</c>.</para>
/// </summary>
internal static class CommandExpander
{
    public static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(60);

    public static async Task<string> ExpandAsync(ExpansionRequest request, ExpansionPorts ports,
        CancellationToken ct)
    {
        var (entry, arguments) = (request.Entry, request.Arguments.Trim());
        var typed = TypedArguments.From(arguments);
        var prompt = new StringBuilder();
        var attachments = new StringBuilder();
        var placeholders = false;

        foreach (var segment in CommandText.Segments(entry.Body))
        {
            // CHECKED BEFORE EVERY PART, not only where something awaits: a literal never touches the
            // token, so a cancel landing between two shell lines would otherwise run the second.
            ct.ThrowIfCancellationRequested();

            switch (segment)
            {
                case Segment.Literal literal:
                    placeholders |= CommandText.HasPlaceholder(literal.Text);
                    prompt.Append(CommandText.Substitute(literal.Text, typed, quote: false));
                    break;

                case Segment.Shell shell:
                    placeholders |= CommandText.HasPlaceholder(shell.Command);
                    prompt.Append(await RunShellAsync(
                        CommandText.Substitute(shell.Command, typed, quote: true), ports, ct));
                    break;

                case Segment.At at:
                    // THE READ USES THE FILE'S OWN PATH, THE TEXT GETS ARGUMENTS. "@$1" names no file
                    // until the user types one, and a typed name must never become an include; but
                    // the model should still see "@foo.cs" where the author wrote "@$1".
                    placeholders |= CommandText.HasPlaceholder(at.Token);
                    if (await ReadAsync(at, ports, ct) is { } contents)
                    {
                        prompt.Append(at.Token);
                        // A FENCE LONGER THAN ANY RUN INSIDE, because an included markdown file has
                        // fences of its own and a ``` one would close at the first of them.
                        attachments.Append($"\n\n{at.Path}:\n{Md.Fence(contents, "")}");
                    }
                    else
                    {
                        prompt.Append(CommandText.Substitute(at.Token, typed, quote: false));
                    }
                    break;
            }
        }

        var text = prompt.ToString().Trim() + attachments;

        // NOTHING THE USER TYPED IS LOST. A file with no placeholder still gets the arguments, on a
        // line of their own, which is what Claude Code does with the same file.
        if (!placeholders && arguments.Length > 0) text += $"\n\nARGUMENTS: {arguments}";

        // A TYPED SKILL OPENS LIKE A LOADED ONE, so the skill tool does not send it again and the
        // panel and compaction count it — see SkillLoader.LoadedIn.
        if (entry.Kind == InvocableKind.Skill)
            text = $"{SkillLoader.BodyMarkerPrefix}{entry.Name}]\ndirectory: {entry.SkillDirectory}\n\n{text}";

        return text;
    }

    private static async Task<string> RunShellAsync(string command, ExpansionPorts ports,
        CancellationToken ct)
    {
        var requests = PermissionPolicy.RequestsFor("shell",
            new JobParameters(new Dictionary<string, object?> { ["command"] = command }),
            ports.WorkingDirectory);

        if (!await AllAllowedAsync(requests, ports, ct))
        {
            ports.Say(new Message($"not run: `{command}` — refused", Severity.Warning));
            return "(not run: refused)";
        }

        var run = ports.RunShell ?? ((c, token) => ShellLine.RunAsync(c, ports.WorkingDirectory, ShellTimeout, token));

        // A SHELL THAT CANNOT START COSTS THIS LINE, NOT THE PROMPT. Cancellation is not a failure to
        // start and must still reach the caller, so Esc stops the expansion.
        ShellLineResult result;
        try { result = await run(command, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ports.Say(new Message($"`{command}` not run: {ex.Message}", Severity.Warning));
            return $"(not run: {ex.Message})";
        }

        if (result.TimedOut)
        {
            ports.Say(new Message($"`{command}` timed out after {ShellTimeout.TotalSeconds:0}s", Severity.Warning));
            return $"(timed out after {ShellTimeout.TotalSeconds:0}s)";
        }

        var size = Encoding.UTF8.GetByteCount(result.Output);
        ports.Say(new Message($"ran `{command}` — exit {result.ExitCode}, {Size(size)}",
            result.ExitCode == 0 ? Severity.Info : Severity.Warning));

        if (result.ExitCode == 0) return result.Output;
        return result.Output.Length == 0 ? $"(exit {result.ExitCode})" : $"{result.Output}\n(exit {result.ExitCode})";
    }

    /// <summary>The file's contents, or null to leave the token as text.</summary>
    private static async Task<string?> ReadAsync(Segment.At at, ExpansionPorts ports,
        CancellationToken ct)
    {
        // RESOLVED ONCE: the gate judges this real path and this is what is read, so a link retargeted while the
        // question is answered cannot change which file is attached.
        if (new PathResolver(ports.WorkingDirectory).AsFileTool(at.Path) is not { } full) return null;

        // NOT A FILE, NOT A QUESTION. @Override, @types/node and @mentions are common in prose and
        // code samples; only a path that exists is an include, and anything else stays as written
        // with nothing said about it.
        if (!File.Exists(full)) return null;

        var requests = PermissionPolicy.RequestsFor("file",
            new JobParameters(new Dictionary<string, object?> { ["action"] = "read", ["path"] = full }),
            ports.WorkingDirectory);

        if (!await AllAllowedAsync(requests, ports, ct))
        {
            ports.Say(new Message($"not included: {at.Path} — refused", Severity.Warning));
            return null;
        }

        // A FILE CAN VANISH OR BE UNREADABLE AFTER THE EXISTS CHECK AND THE GATE. That costs the
        // include, not the whole prompt: the token stays text and the user is told why.
        // AT MOST THE CAP AND ONE MORE CHARACTER, read — not the whole file read and then cut. An
        // @ pointing at a log or a dump would otherwise be loaded entire to keep its first 64K; the
        // extra character is how a cut is told apart from a file exactly the cap long.
        var cap = ToolBindings.MaxToolResultChars;
        string text;
        try
        {
            using var reader = new StreamReader(full);
            var buffer = new char[cap + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await reader.ReadAsync(buffer.AsMemory(read), ct)) > 0)
                read += n;
            text = new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ports.Say(new Message($"not included: {at.Path} — {ex.Message}", Severity.Warning));
            return null;
        }

        // NUL NEVER APPEARS IN TEXT. An image or a binary decoded as UTF-8 is a page of replacement
        // characters that costs context and tells the model nothing.
        if (text.Contains('\0'))
        {
            ports.Say(new Message($"not included: {at.Path} — not text", Severity.Warning));
            return null;
        }

        return text.Length <= cap ? text : text[..cap] + "\n[truncated]";
    }

    // EVERY REQUEST THE POLICY RAISES IS ASKED, AND THE FIRST DENIAL REFUSES. The policy returns a
    // list because some actions touch more than one thing; assuming one would let a second request
    // go unasked the day a rule adds it. REQUESTER STAYS NULL: it names the sub-agent that asked,
    // and the prompt and the classifier both render it as "requested by sub-agent". The user typed
    // this command, so the request is the session's own.
    internal static async Task<bool> AllAllowedAsync(IEnumerable<PermissionRequest> requests,
        ExpansionPorts ports, CancellationToken ct)
    {
        // NOTHING ASKED IS NOT A YES. The policy always has something to ask about a shell line or a
        // read; a list that came back empty means something upstream went wrong, and a permission
        // check that allows by default is the wrong way to find out.
        var asked = false;
        foreach (var request in requests)
        {
            asked = true;
            var allowed = await ports.Allow(request, ct);

            // A CANCEL IS NOT A REFUSAL. A gate whose prompt was cancelled answers deny rather than
            // throwing, and treating that as the user's no would say "refused" after "Stopped." for
            // a question nobody answered — and carry on to the next part.
            ct.ThrowIfCancellationRequested();
            if (!allowed) return false;
        }
        return asked;
    }

    private static string Size(int bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.0} KB";
}
