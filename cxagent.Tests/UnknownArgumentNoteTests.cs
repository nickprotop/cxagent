using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The note appended to a validation failure naming arguments that were SENT BUT IGNORED.
/// Unrecognised names are silently dropped, so `read_file {"file_path": "..."}` reported
/// "'path' is required" — a message contradicting what the caller just sent. A misspelling and an
/// omission were indistinguishable in that output despite needing opposite fixes.
/// </summary>
public class UnknownArgumentNoteTests
{
    private static readonly string[] FileParams = ["path", "offset", "limit"];

    [Fact]
    public void SaysNothingWhenEveryNameIsRecognised()
    {
        // The note must never appear on a call whose names were all fine — the failure is then
        // about something else, and extra text would send the caller looking for a second mistake.
        Assert.Equal("", UnknownArgumentNote.For(["path", "limit"], FileParams, "read_file"));
    }

    [Fact]
    public void NamesTheIgnoredArgumentAndSuggestsTheRealOne()
    {
        var note = UnknownArgumentNote.For(["file_path"], FileParams, "read_file");

        Assert.Contains("'file_path'", note);
        Assert.Contains("did you mean 'path'", note);
        Assert.Contains("read_file accepts: path, offset, limit", note);
    }

    [Theory]
    [InlineData("filePath")]
    [InlineData("filepath")]
    [InlineData("file_path")]
    [InlineData("FilePath")]
    public void MatchesAcrossCaseAndSeparators(string sent)
    {
        Assert.Contains("did you mean 'path'", UnknownArgumentNote.For([sent], FileParams, "read_file"));
    }

    [Fact]
    public void OffersNoGuessWhenNothingIsClose()
    {
        // A wrong suggestion is worse than none: the caller will follow it. `pattern` is 3 edits
        // from `path`, which edit-distance matching would happily call a near miss.
        var note = UnknownArgumentNote.For(["encoding"], FileParams, "read_file");

        Assert.Contains("'encoding'", note);
        Assert.DoesNotContain("did you mean", note);
    }

    [Fact]
    public void OffersNoGuessWhenTwoAcceptedNamesBothMatch()
    {
        // Which one was meant is exactly what is unknown.
        var note = UnknownArgumentNote.For(["pathlimit"], FileParams, "read_file");

        Assert.DoesNotContain("did you mean", note);
    }

    [Fact]
    public void DoesNotReportActionAsUnrecognised()
    {
        // `action` is pinned by the toolset rather than sent by the caller, and a planned job
        // legitimately carries it. Flagging it would send the caller hunting for a mistake it did
        // not make.
        Assert.Equal("", UnknownArgumentNote.For(["path", "action"], FileParams, "read_file"));
    }
}
