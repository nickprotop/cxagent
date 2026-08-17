using CxAgent.Core.Commands;
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

    // SHAPED LIKE THE IDS THE GENERATOR MAKES NOW: randomness first, timestamp at the tail — so
    // two sessions differ from their opening character. See UlidGenerator for why that changed.
    private static readonly IReadOnlyList<SessionInfo> Two =
        [Row("XNND4VH6W0GR0701KZXC5H9Q"), Row("CSTJC6C9QF7WVD01KZXC96Z5", "asked about /mode")];

    [Fact]
    public void ListingNamesEachSessionBothWaysItCanBeAddressed()
    {
        var reply = SessionsCommand.Decide("", Two, Week).Reply;

        // The number to type, the uid to quote, and the title that says which is which.
        Assert.Contains(" 1", reply);
        Assert.Contains("XNND4V", reply);
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
        var byShortId = SessionsCommand.Decide("resume CSTJC6", Two, Week);

        Assert.Equal("CSTJC6C9QF7WVD01KZXC96Z5", byNumber.ResumeUid);
        Assert.Equal(byNumber.ResumeUid, byShortId.ResumeUid);
    }

    /// <summary>Six from the front, like git — the generator puts the randomness there.</summary>
    [Fact]
    public void TheShortFormIsTheOpeningOfTheUid()
    {
        Assert.Equal("XNND4V", SessionsCommand.Short("XNND4VH6W0GR0701KZXC5H9Q"));
    }

    /// <summary>
    /// IDS MINTED BEFORE THE LAYOUT CHANGED ARE STILL IN THE STORE, and theirs is the random half at
    /// the tail. A user reading one off an older listing types what they saw; refusing it would be a
    /// rule the output never explained, on a row that looks no different from any other.
    /// </summary>
    [Fact]
    public void ALegacyIdIsStillReachableByItsTail()
    {
        IReadOnlyList<SessionInfo> old = [Row("01KZXC5H9QXNND4VH6W0GR07R2")];

        Assert.Equal("01KZXC5H9QXNND4VH6W0GR07R2",
            SessionsCommand.Decide("resume GR07R2", old, Week).ResumeUid);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal("XNND4VH6W0GR0701KZXC5H9Q",
            SessionsCommand.Decide("resume xnnd4v", Two, Week).ResumeUid);
    }

    /// <summary>A whole uid pasted from --sessions or an exit hint still names its session.</summary>
    [Fact]
    public void AFullUidMatchesFromTheFront()
    {
        Assert.Equal("XNND4VH6W0GR0701KZXC5H9Q",
            SessionsCommand.Decide("resume XNND4VH6W0GR0701KZXC5H9Q", Two, Week).ResumeUid);
    }

    [Fact]
    public void AnAmbiguousPrefixNamesTheCandidatesInsteadOfPickingOne()
    {
        IReadOnlyList<SessionInfo> both = [Row("ZZZAAA01KZXC"), Row("ZZZBBB01KZXC")];

        var result = SessionsCommand.Decide("resume ZZZ", both, Week);

        Assert.Null(result.ResumeUid);
        Assert.Contains("ZZZAAA", result.Reply);
        Assert.Contains("ZZZBBB", result.Reply);
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
        IReadOnlyList<SessionInfo> untitled = [Row("ABC123XX01KZXC", title: null)];

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
            .Select(i => Row($"UID{i:0000}01KZXC"))
            .ToList();

        var reply = SessionsCommand.Decide("", many, Week).Reply;

        Assert.Contains("5 older", reply);
        Assert.DoesNotContain("UID0024", reply);
    }


    // --- the plain-text listing, for --sessions ---

    /// <summary>
    /// THE FULL ID, because the reason to read this from a script is to copy an id into the next
    /// command — six characters would have to be re-expanded by hand.
    /// </summary>
    [Fact]
    public void PlainListingPrintsWholeIdsAndIsTabSeparated()
    {
        var line = SessionsCommand.RenderPlain(Two).Split('\n')[0];
        var fields = line.Split('\t');

        Assert.Equal("XNND4VH6W0GR0701KZXC5H9Q", fields[0]);
        Assert.Equal(4, fields.Length);              // id, when, tokens, title
        Assert.Equal("1200", fields[2]);             // 1000 in + 200 out
        Assert.Equal("did a thing", fields[3]);
    }

    /// <summary>No markup: this goes to a pipe as often as to a terminal.</summary>
    [Fact]
    public void PlainListingCarriesNoMarkup()
    {
        var text = SessionsCommand.RenderPlain(Two);

        Assert.DoesNotContain("[/]", text);
        Assert.DoesNotContain(ColorScheme.AccentMarkup, text);
    }

    /// <summary>
    /// EVERY SESSION, unlike the on-screen listing. A list you can pass to grep must not silently
    /// stop at twenty — the caller asked for what is there.
    /// </summary>
    [Fact]
    public void PlainListingIsNotCapped()
    {
        var many = Enumerable.Range(0, SessionsCommand.MaxRows + 5)
            .Select(i => Row($"UID{i:0000}01KZXC"))
            .ToList();

        Assert.Equal(many.Count, SessionsCommand.RenderPlain(many).Split('\n').Length);
    }

    [Fact]
    public void PlainListingSaysSoWhenThereIsNothing()
    {
        Assert.Contains("No sessions", SessionsCommand.RenderPlain([]));
    }

    /// <summary>The folder is a column only when rows can come from different ones.</summary>
    [Fact]
    public void PlainListingAddsTheFolderOnlyForAll()
    {
        Assert.DoesNotContain("/w", SessionsCommand.RenderPlain(Two));
        Assert.Contains("/w", SessionsCommand.RenderPlain(Two, all: true));
    }

    // --- the startup line, which replaced the resume dialog ---

    /// <summary>Nothing recorded here means nothing to say. Silence is the correct output.</summary>
    [Fact]
    public void StartupHintIsAbsentWhenTheFolderHasNoHistory()
    {
        Assert.Null(SessionsCommand.StartupHint(0, null));
    }

    /// <summary>
    /// THE UNFINISHED ONE IS NAMED, because "ended without closing" is the case where someone lost
    /// work and is looking for it — the case the old dialog existed to catch.
    /// </summary>
    [Fact]
    public void StartupHintNamesAnUnfinishedSessionAndItsSize()
    {
        var hint = SessionsCommand.StartupHint(4, unfinishedMessages: 13)!;

        Assert.Contains("without closing", hint);
        Assert.Contains("13 messages", hint);
        Assert.Contains("/sessions", hint);
        Assert.Contains("4 in this folder", hint);
    }

    /// <summary>With everything closed cleanly there is no alarm to raise — just a pointer.</summary>
    [Fact]
    public void StartupHintIsJustACountWhenNothingWasLeftOpen()
    {
        var hint = SessionsCommand.StartupHint(4, unfinishedMessages: null)!;

        Assert.DoesNotContain("without closing", hint);
        Assert.Contains("4 earlier sessions", hint);
        Assert.Contains("/sessions", hint);
    }

    [Fact]
    public void StartupHintReadsCorrectlyForASingleSession()
    {
        var hint = SessionsCommand.StartupHint(1, unfinishedMessages: null)!;

        Assert.Contains("1 earlier session in", hint);   // not "sessions"
        Assert.Contains("to see it", hint);              // not "them"
    }

    // --- the exit hint ---

    /// <summary>
    /// PASTEABLE, which is the whole point: the id is an implementation detail everywhere except
    /// this one moment, where it turns "I closed that by accident" into a command.
    /// </summary>
    [Fact]
    public void ExitHintIsACommandTheUserCanRun()
    {
        var hint = SessionsCommand.ExitHint("XNND4VH6W0GR0701KZXC5H9Q");

        Assert.Contains("cxagent --resume XNND4V", hint);
        Assert.DoesNotContain("[", hint);   // a bare terminal, after the TUI has released it
    }
}
