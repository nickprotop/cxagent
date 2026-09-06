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
public sealed record JobSnapshot(
    string Id,
    string AgentId,
    string JobType,
    string DisplayName,
    string State,
    string? Parameters,
    string? ResultOutput,
    string? Error,
    string? DecidedBy,
    double? Progress,
    string? ProgressMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>How much of a result or a parameter set is kept.</summary>
    /// <remarks>
    /// A TOOL'S OUTPUT IS AN ECHO, NOT THE ANSWER. One `dotnet build` would otherwise put megabytes
    /// into whatever keeps these — the same reasoning that caps the in-memory tool report at 4 kB.
    /// </remarks>
    private const int MaxText = 4096;

    /// <summary>Captures a job as it stands, sharing nothing with it afterwards.</summary>
    public static JobSnapshot Of(Job job) => new(
        Id: job.Id,
        AgentId: job.AgentId,
        JobType: job.JobType,
        DisplayName: job.DisplayName,
        State: job.State.ToString(),
        Parameters: Render(job.Parameters?.Values),
        ResultOutput: Render(job.Result?.Output),
        Error: Cap(job.Result?.ErrorMessage),
        DecidedBy: job.DecidedBy,
        Progress: job.Progress,
        ProgressMessage: job.ProgressMessage,
        StartedAt: job.StartedAt,
        CompletedAt: job.CompletedAt);

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
