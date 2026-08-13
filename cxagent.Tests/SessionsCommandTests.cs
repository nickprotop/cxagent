using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/sessions</c> — the listing, and the two ways to name a row in it.
/// </summary>
public class SessionsCommandTests
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static SessionInfo Row(string uid, string? title = "did a thing", int minutesAgo = 5) =>
        new(Uid: uid,
            Title: title,
            WorkingDir: "/w",
            InputTokens: 1000,
            OutputTokens: 200,
            Finished: false,
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    // ULID-SHAPED ON PURPOSE: a shared leading timestamp and a distinguishing tail, which is what
    // real sessions look like and what makes the tail the part worth showing.
    private static readonly IReadOnlyList<SessionInfo> Two =
        [Row("01KZXC5H9QXNND"), Row("01KZXC96Z5CSTJ", "asked about /mode")];

    [Fact]
    public void ListingNamesEachSessionBothWaysItCanBeAddressed()
    {
        var reply = SessionsCommand.Decide("", Two, Week).Reply;

        // The number to type, the uid to quote, and the title that says which is which.
        Assert.Contains(" 1", reply);
        Assert.Contains("QXNND", reply);
        Assert.Contains("asked about /mode", reply);
    }

    [Fact]
    public void TheRetentionWindowIsStatedRatherThanDiscovered()
    {
        Assert.Contains("7 days", SessionsCommand.Decide("", Two, Week).Reply);
    }

    /// <summary>The gate for this step: both spellings reach the same session.</summary>
    [Fact]
    public void ANumberAndAShortIdReachTheSameSession()
    {
        var byNumber = SessionsCommand.Decide("resume 2", Two, Week);
        var byShortId = SessionsCommand.Decide("resume Z5CSTJ", Two, Week);

        Assert.Equal("01KZXC96Z5CSTJ", byNumber.ResumeUid);
        Assert.Equal(byNumber.ResumeUid, byShortId.ResumeUid);
    }

    /// <summary>The listing shows the tail because a ULID's head is a shared timestamp — three
    /// sessions made minutes apart would otherwise all read as the same six characters.</summary>
    [Fact]
    public void TheShortFormIsTheEndOfTheUidNotTheStart()
    {
        Assert.Equal("QXNND4", SessionsCommand.Short("01KZXC5H9QXNND4"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal("01KZXC5H9QXNND", SessionsCommand.Decide("resume qxnnd", Two, Week).ResumeUid);
    }

    /// <summary>A whole uid pasted from --sessions or an exit hint still names its session.</summary>
    [Fact]
    public void AFullUidMatchesFromTheFront()
    {
        Assert.Equal("01KZXC5H9QXNND",
            SessionsCommand.Decide("resume 01KZXC5H9QXNND", Two, Week).ResumeUid);
    }

    [Fact]
    public void AnAmbiguousPrefixNamesTheCandidatesInsteadOfPickingOne()
    {
        IReadOnlyList<SessionInfo> both = [Row("01KZXCAAAZZZ"), Row("01KZXCBBBZZZ")];

        var result = SessionsCommand.Decide("resume ZZZ", both, Week);

        Assert.Null(result.ResumeUid);
        Assert.Contains("AAAZZZ", result.Reply);
        Assert.Contains("BBBZZZ", result.Reply);
    }

    [Fact]
    public void ANumberOutsideTheListIsRefusedWithItsSize()
    {
        var result = SessionsCommand.Decide("resume 9", Two, Week);

        Assert.Null(result.ResumeUid);
        Assert.Contains("2", result.Reply);
    }

    [Fact]
    public void AnUnknownIdIsRefusedRatherThanResumingSomethingElse()
    {
        Assert.Null(SessionsCommand.Decide("resume zzz", Two, Week).ResumeUid);
    }

    [Fact]
    public void ResumeWithNothingToResumeAsksWhichOne()
    {
        var result = SessionsCommand.Decide("resume", Two, Week);

        Assert.Null(result.ResumeUid);
        Assert.Contains("Which one", result.Reply);
    }

    [Fact]
    public void AnEmptyListSaysSoRatherThanRenderingAnEmptyTable()
    {
        Assert.Contains("none", SessionsCommand.Decide("", [], Week).Reply);
    }

    [Fact]
    public void ASessionWithNoTitleStillGetsARowThatCanBeActedOn()
    {
        IReadOnlyList<SessionInfo> untitled = [Row("01KZXCABC123", title: null)];

        var reply = SessionsCommand.Decide("", untitled, Week).Reply;

        Assert.Contains("ABC123", reply);
        Assert.Contains("no messages yet", reply);
    }

    /// <summary>The folder is shown only when it could differ from the one you are in.</summary>
    [Fact]
    public void OnlyTheAllListingShowsWhichFolderEachSessionBelongsTo()
    {
        Assert.DoesNotContain("/w", SessionsCommand.Decide("", Two, Week).Reply);
        Assert.Contains("/w", SessionsCommand.Decide("all", Two, Week, all: true).Reply);
    }

    [Fact]
    public void ALongListIsCappedAndSaysHowManyItLeftOut()
    {
        var many = Enumerable.Range(0, SessionsCommand.MaxRows + 5)
            .Select(i => Row($"01KZXCUID{i:0000}"))
            .ToList();

        var reply = SessionsCommand.Decide("", many, Week).Reply;

        Assert.Contains("5 older", reply);
        Assert.DoesNotContain("UID0024", reply);
    }

    [Fact]
    public void CompletionsCompleteToTheNumberAndDescribeByTheTitle()
    {
        var rows = SessionsCommand.Completions(Two);

        Assert.Equal("1", rows[0].Name);
        Assert.Equal("2", rows[1].Name);
        Assert.Contains("asked about /mode", rows[1].Summary);
        Assert.All(rows, r => Assert.True(r.Completes));
    }
}
