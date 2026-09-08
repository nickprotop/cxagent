using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Sessions;

/// <summary>
/// A job as it was at one moment, safe to keep.
///
/// <para>WHY A SNAPSHOT AT ALL. <see cref="IToolObserver"/> hands out the live <see cref="Job"/>,
/// which has ten settable properties mutated mid-flight by design — <c>DecidedBy</c>'s own comment
/// says "the value arrives mid-flight, from the gate, on a job the row is already showing". A
/// subscriber that KEEPS one, to write it down or to send it somewhere, is keeping a reference to
/// something that will change underneath it: history rewritten before anybody read it.</para>
///
/// <para>AND THE MUTATION REACHES DEEPER THAN THE OBJECT. <c>Parameters.Values</c> and a result's
/// output are dictionaries of <c>object?</c>, which may hold <c>JsonElement</c>s over a
/// <c>JsonDocument</c> somebody else will dispose — a hazard <c>SessionManager</c> already
/// documents. Rendering those to strings at the moment of capture settles both problems at once:
/// nothing shared stays shared.</para>
///
/// <para>NOT A REPLACEMENT FOR <see cref="Job"/>. The live object is right for the front end that
/// re-renders on every change; this is for anything that has to remember what a job LOOKED LIKE.
/// </para>
/// </summary>
/// <summary>Which job this is, and what to call it.</summary>
/// <param name="Id">The job's own id, stable for its life.</param>
/// <param name="AgentId">Which agent ran it — replaced by a re-wire, so not a session key.</param>
/// <param name="JobType">The tool behind it.</param>
/// <param name="DisplayName">What a row calls it.</param>
public sealed record JobIdentity(string Id, string AgentId, string JobType, string DisplayName);

/// <summary>What the job did, once it had done it.</summary>
/// <param name="State">Its state at the moment of capture, as text — the live enum is not the DTO's to carry.</param>
/// <param name="Parameters">The call's arguments, rendered to JSON at capture.</param>
/// <param name="Output">What the tool returned, capped. An echo, not the answer.</param>
/// <param name="Error">Why it failed, capped, or null when it did not.</param>
/// <param name="DecidedBy">Who answered the permission question, when one was asked.</param>
/// <param name="Reviewing">
/// Whether the gate is asking a model about this job right now.
///
/// <para>A ROW RENDERS IT, so a DTO without it loses the "reviewing…" state a client shows while a
/// classifier is being consulted — which is exactly the moment a user wonders why nothing is
/// happening. There is no reason a remote client needs it less than a local one.</para>
/// </param>
/// <param name="RetryCount">How many times this job has been retried.</param>
public sealed record JobOutcome(
    string State, string? Parameters, string? Output, string? Error, string? DecidedBy,
    bool Reviewing = false, int RetryCount = 0);

/// <summary>How far along it is, as a client would show it.</summary>
/// <param name="Fraction">How far along, 0 to 1, when the job reports it.</param>
/// <param name="Message">The short progress line a row shows.</param>
/// <param name="Body">The longer progress text, where a job reports one beside its short message.</param>
public sealed record JobProgress(double? Fraction, string? Message, string? Body = null);

/// <summary>
/// When it happened.
///
/// <para>CREATED IS NOT STARTED, for a queued job — the gap between them is the wait, which is the
/// only thing a user staring at a pending row wants to know.</para>
/// </summary>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="StartedAt">When it began running.</param>
/// <param name="CompletedAt">When it finished, or null while it runs.</param>
public sealed record JobTiming(
    DateTimeOffset? CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

/// <summary>Which job, what came of it, how far along, and when — see each part's own doc.</summary>
/// <param name="Identity">Which job this is.</param>
/// <param name="Outcome">What it did.</param>
/// <param name="Progress">How far along.</param>
/// <param name="Timing">When.</param>
public sealed record JobSnapshot(
    JobIdentity Identity, JobOutcome Outcome, JobProgress Progress, JobTiming Timing)
{
    /// <summary>How much of a result or a parameter set is kept.</summary>
    /// <remarks>
    /// A TOOL'S OUTPUT IS AN ECHO, NOT THE ANSWER. One `dotnet build` would otherwise put megabytes
    /// into whatever keeps these — the same reasoning that caps the in-memory tool report at 4 kB.
    /// </remarks>
    private const int MaxText = 4096;

    /// <summary>Captures a job as it stands, sharing nothing with it afterwards.</summary>
    public static JobSnapshot Of(Job job) => new(
        new JobIdentity(job.Id, job.AgentId, job.JobType, job.DisplayName),
        new JobOutcome(
            State: job.State.ToString(),
            Parameters: Render(job.Parameters?.Values),
            Output: Render(job.Result?.Output),
            Error: Cap(job.Result?.ErrorMessage),
            DecidedBy: job.DecidedBy,
            Reviewing: job.Reviewing,
            RetryCount: job.RetryCount),
        new JobProgress(job.Progress, job.ProgressMessage, Cap(job.ProgressBody)),
        new JobTiming(job.CreatedAt, job.StartedAt, job.CompletedAt));

    /// <summary>
    /// A parameter dictionary as text.
    ///
    /// <para>SERIALISED RATHER THAN COPIED, because the values are <c>object?</c> and may be
    /// <c>JsonElement</c>s whose document is disposed the moment the call that produced them
    /// returns. A copied reference would then throw when read; a string cannot.</para>
    ///
    /// <para>A FAILURE IS NOT FATAL. Something unserialisable in a parameter must not stop a job
    /// being recorded, so it becomes a note rather than an exception.</para>
    /// </summary>
    private static string? Render(Dictionary<string, object?>? values)
    {
        if (values is null || values.Count == 0) return null;

        try { return Cap(JsonSerializer.Serialize(values)); }
        catch (Exception) { return "(unserialisable)"; }
    }

    private static string? Cap(string? text) =>
        text is null ? null
        : text.Length <= MaxText ? text
        : text[..MaxText] + "…";
}

/// <summary>
/// A permission request as it can cross a boundary.
///
/// <para><see cref="PermissionRequest"/> ITSELF CANNOT. It carries an <c>Action&lt;bool&gt;</c>
/// callback and a live <see cref="PermissionPolicy"/> holding a rules store and a settable edit
/// mode — neither of which means anything outside the process that made it. What a front end needs
/// is the question and enough to answer it.</para>
///
/// <para>THE ID IS THE POINT. An answer names the request it answers, so a decision arriving after
/// something else resolved the same prompt is recognisably late rather than applied to whatever is
/// current. A single outstanding prompt did not need one; concurrent prompts do.</para>
/// </summary>
public sealed record PermissionRequestDto(
    long Id,
    string Kind,
    string Display,
    string? Requester,
    string? SessionId,
    string? AlwaysRule,
    bool OfferTrust)
{
    /// <summary>Projects a request, dropping everything that only means something in this process.</summary>
    public static PermissionRequestDto Of(long id, PermissionRequest request, bool offerTrust) => new(
        Id: id,
        Kind: request.Kind.ToString(),
        Display: request.Display,
        Requester: request.Requester,
        SessionId: request.Policy?.SessionId,
        AlwaysRule: request.AlwaysRule,
        OfferTrust: offerTrust);
}
