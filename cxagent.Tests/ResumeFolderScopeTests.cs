using CxAgent.Core.Commands;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which sessions `/sessions resume` can reach.
///
/// <para>THE GUARD IS NOW BOTH. `LoadByUid` takes an optional folder and refuses a uid outside it,
/// so `/sessions resume` is scoped by the store rather than only by what the caller listed. `all`
/// widens the listing AND the lookup together — a uid read off `/sessions all` was chosen
/// deliberately from another project — and `--resume` passes no folder at all, because naming a
/// session on the command line is a deliberate act by somebody who typed the id.</para>
///
/// <para>THE OLD SHAPE, kept because it explains why the store's own guard was worth adding: the
/// guard was the listing, not the lookup. `LoadByUid` matches on the agent id alone and asks
/// nothing about folders, so what stops a resume crossing projects is that `Decide` resolves the
/// typed uid against the rows it was HANDED — and the caller scopes those to the working directory
/// unless `all` was asked for. That is a real guard, but an indirect one: it lives in the caller's
/// choice of rows rather than in the resume itself.</para>
/// </summary>
public class ResumeFolderScopeTests
{
    private static SessionInfo Row(string uid, string dir) =>
        new(uid, Title: null, WorkingDir: dir, InputTokens: 0, OutputTokens: 0,
            Finished: false, UpdatedAt: DateTimeOffset.UtcNow);

    /// <summary>A UID THAT IS NOT IN THE LIST IS REFUSED, which is what scopes a resume to a folder
    /// when the caller scoped the list to one.</summary>
    [Fact]
    public void AUidOutsideTheListedRowsIsNotResumed()
    {
        var here = new[] { Row("01AAAA", "/work/alpha") };

        var result = SessionsCommand.Decide("resume 01BBBB", here, TimeSpan.FromDays(30));

        Assert.Null(result.ResumeUid);
        Assert.Contains("No session matches", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND A UID THAT IS IN THE LIST IS RESUMED WITHOUT ANY FOLDER CHECK OF ITS OWN.
    ///
    /// <para>This is the half worth knowing: hand Decide a row from another project — which
    /// `/sessions all` does — and it resolves it happily. Nothing below this point asks whether the
    /// conversation belongs to the folder the session is working in.</para>
    /// </summary>
    [Fact]
    public void AUidFromAnotherFolderResumesWhenItWasListed()
    {
        var everywhere = new[] { Row("01AAAA", "/work/alpha"), Row("01BBBB", "/work/beta") };

        var result = SessionsCommand.Decide("resume 01BBBB", everywhere, TimeSpan.FromDays(30), all: true);

        Assert.Equal("01BBBB", result.ResumeUid);
    }

    /// <summary>
    /// `all` IS DETECTED ANYWHERE IN THE ARGUMENT, so `resume` and `all` can be combined — which is
    /// how a cross-folder resume is reachable in one command rather than two.
    /// </summary>
    [Fact]
    public void TheAllScopeIsTakenFromAnyArgumentWord()
    {
        var words = SessionCommands.ArgumentWords("/sessions resume all");

        Assert.Contains(words, w => w.Equals("all", StringComparison.OrdinalIgnoreCase));
    }
}
