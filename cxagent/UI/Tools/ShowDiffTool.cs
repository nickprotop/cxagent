using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;

namespace CxAgent.UI.Tools;

/// <summary>
/// <c>show_diff</c> — one file's changes, rendered into the transcript.
///
/// <para>WHY CXAGENT INJECTS THIS RATHER THAN CORE OWNING IT. Core has no transcript, no colour, no
/// folding and no reason to know a markup dialect. This tool's whole output is markup that only
/// cxagent can render, which makes it the clearest case for the injection mechanism existing at
/// all: not a demonstration, a feature a rich TUI should offer.</para>
///
/// <para>NOT <c>/diff</c>, WHICH IS UNTOUCHED. That is a command the USER types, it lives in
/// cxagent.Core, and it answers a different question. The two share no code deliberately — coupling
/// them would make every change to one a risk to the other, across an assembly boundary.</para>
///
/// <para>ONE FILE PER CALL, and <c>path</c> is required, which is the enforcement: no path means no
/// diff, so there is no way to ask for the whole tree. A model that edited four files calls this
/// four times and gets four foldable rows.</para>
/// </summary>
public sealed class ShowDiffTool : IAgentTool
{
    private readonly string _workingDirectory;

    public ShowDiffTool(string workingDirectory) => _workingDirectory = workingDirectory;

    /// <summary>
    /// NOT OFFERED TO A SUB-AGENT, because there is nowhere for it to draw.
    ///
    /// <para>A child's tool rows go to a <c>BufferedJobPanel</c> that nothing displays — the buffer
    /// exists precisely to keep a child's rows out of the parent's transcript, and only each row's
    /// DisplayName is ever read back, for the parent's progress line. So a child calling this would
    /// render the whole diff, be told it succeeded, and have every byte discarded.</para>
    ///
    /// <para>THE CHILD SHOULD SAY WHAT IT CHANGED IN ITS ANSWER, which is the thing its parent
    /// actually reads. Showing is the parent's job, because the parent is the one with a screen.</para>
    /// </summary>
    public bool OfferToSubAgents => false;

    public ToolDefinition Definition { get; } = new(
        "show_diff",
        // "RETURNS NOTHING USEFUL TO YOU" IS DELIBERATE. A tool result the model tries to summarise
        // wastes a turn describing something already on screen, so the description says plainly that
        // the audience is the human and the result is only a confirmation.
        "Show the user a rendered diff of ONE file you changed. Call it once per file after "
            + "editing, so they can see what changed. Returns nothing useful to you: the diff is "
            + "for the human, and is already on their screen.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "The file to show, relative to the working directory." },
                staged = new { type = "boolean", description = "Show staged changes instead of unstaged." },
            },
            required = new[] { "path" },
        }));

    /// <summary>
    /// A <c>FileRead</c> request, ALWAYS — this does not decide "inside or outside" itself.
    ///
    /// <para>An earlier design returned null for anything under the working directory, on the
    /// grounds that reads there are already free. That conflated two things: the free pass is
    /// <c>PermissionPolicy.AllowsSilentWrites</c>, which requires the folder to be TRUSTED as well as
    /// in-boundary. A tool returning null for anything under the cwd would skip the prompt in exactly
    /// the case the prompt exists for — an untrusted folder.</para>
    ///
    /// <para>So the request goes to the policy and the policy answers it. That is also what the
    /// policy's own comment demands about this decision: a second copy of "trusted and in-boundary"
    /// <c>"would be the copy that drifts"</c>.</para>
    /// </summary>
    public PermissionRequest? Gate(JobParameters call)
    {
        var path = call.Get("path", "");

        // RESOLVED BEFORE IT IS JUDGED. "../../etc/passwd" is inside the working directory only as a
        // string; the policy compares real paths, and handing it an unresolved one would be the
        // recurring shape of bug in this codebase — a check that examines part of a request and lets
        // the rest through unexamined.
        var resolved = Path.GetFullPath(Path.Combine(_workingDirectory, path));

        return new PermissionRequest(PermissionKind.FileRead, resolved, AlwaysRule: null);
    }

    public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
    {
        var path = call.Get("path", "");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(new JobResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = "show_diff needs a path: it shows one file at a time.",
            });

        var diff = GitDiff.Read(new GitDiffRequest(path, _workingDirectory, call.Get("staged", false)));
        var markup = DiffRenderer.Render(diff);

        return Task.FromResult(new JobResult
        {
            Success = true,

            // TWO KEYS, TWO AUDIENCES. "content" is what InlineJobSink.BodyText reads to build the
            // row — markup returned under any other key renders BLANK with nothing to say why.
            // "summary" is what the model is told instead, because handing it this markup would cost
            // a turn of it describing colour tags for a diff already on the user's screen.
            Output =
            {
                ["content"] = markup,
                ["summary"] = Summarise(diff),
            },
        });
    }

    /// <summary>
    /// What the MODEL is told, as distinct from what the user is shown.
    ///
    /// <para>Short and factual on purpose: enough to confirm the call worked, not enough to invite a
    /// paraphrase of a diff the user is already looking at.</para>
    /// </summary>
    public static string Summarise(FileDiff diff) => diff.Status switch
    {
        DiffStatus.Changed => $"{diff.Path}, +{diff.Added} {DiffRenderer.Minus}{diff.Removed}, shown above",
        DiffStatus.NoChanges => $"{diff.Path} has no changes",
        DiffStatus.Binary => $"{diff.Path} is binary",
        _ => "not a git repository",
    };
}
