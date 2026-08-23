using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/trust</c> — the in-app way to see and change a folder's classification.
///
/// <para>THE DECISION IS A PURE FUNCTION, which is the whole reason it is separated from the session
/// that persists it: trust is a security control, and a control whose logic can only be exercised
/// through a store, a window and a running agent is one nobody writes the awkward cases for.</para>
/// </summary>
public class TrustCommandTests
{
    private const string Root = "/tmp/proj";

    private static TrustCommandResult Run(string argument, TrustState current) =>
        TrustCommand.Decide(new(argument, current, Root));

    [Fact]
    public void Bare_ReportsTrusted_AndOffersTheWayBack()
    {
        var result = Run("", TrustState.Trusted);

        Assert.Null(result.NewState);
        Assert.Contains("trusted", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains(Root, result.Reply.Text, StringComparison.Ordinal);

        // REVOKING MUST BE DISCOVERABLE FROM HERE. It is the direction that did not exist at all
        // before this command, so a reader who lands on the report and is not told how to undo has
        // reached the same dead end by a different road.
        Assert.Contains("/trust no", result.Reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Bare_ReportsUntrusted_AndOffersTheWayBack()
    {
        var result = Run("", TrustState.Untrusted);

        Assert.Null(result.NewState);
        Assert.Contains("not trusted", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("/trust yes", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// NEVER ASKED IS NOT THE SAME AS DECLINED, even though they behave identically. Reporting
    /// Unknown as "not trusted" would be true about the permissions and false about the user — and
    /// it is the one state where the startup question is still owed, which is exactly what a user on
    /// a filesystem with no birth time needs told when they wonder why they keep being asked.
    /// </summary>
    [Fact]
    public void Bare_DistinguishesNeverAsked_FromDeclined()
    {
        var unknown = Run("", TrustState.Unknown).Reply.Text;
        var declined = Run("", TrustState.Untrusted).Reply.Text;

        Assert.Contains("not been classified", unknown, StringComparison.Ordinal);
        Assert.NotEqual(declined, unknown);
    }

    [Fact]
    public void Yes_Trusts_AndSaysWhatThatBuys()
    {
        var result = Run("yes", TrustState.Untrusted);

        Assert.Equal(TrustState.Trusted, result.NewState);
        Assert.Contains(Root, result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DIRECTION THAT DID NOT EXIST. Before this command a folder could be trusted with one
    /// click and never untrusted except by hand-editing permissions.json — the sharper of the two
    /// gaps the permissions design left for follow-up.
    /// </summary>
    [Fact]
    public void No_RevokesTrust()
    {
        var result = Run("no", TrustState.Trusted);

        Assert.Equal(TrustState.Untrusted, result.NewState);
        Assert.Contains("ask every time", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REVOCATION IS NOT AN ERROR, but it is worth colouring: what follows is a session that
    /// prompts for everything, and the startup question uses the same severity for the same answer.
    /// </summary>
    [Fact]
    public void Revoking_IsWarningSeverity()
    {
        Assert.Equal(Severity.Warning, Run("no", TrustState.Trusted).Reply.Severity);
        Assert.Equal(Severity.Info, Run("yes", TrustState.Untrusted).Reply.Severity);
    }

    /// <summary>
    /// A NO-OP DOES NOT RE-STORE. Rewriting the same value is invisible from here and touches the
    /// file for nothing — and a user typing `/trust no` on an untrusted folder is asking whether it
    /// is off, so the honest answer names the state rather than implying a change just happened.
    /// </summary>
    [Theory]
    [InlineData("yes", TrustState.Trusted)]
    [InlineData("no", TrustState.Untrusted)]
    public void SettingWhatIsAlreadySet_ChangesNothing(string argument, TrustState current)
    {
        var result = Run(argument, current);

        Assert.Null(result.NewState);
        Assert.Contains("already", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEVERAL SPELLINGS PER ANSWER. This command is typed rarely, and the word that comes to mind
    /// is whichever the user last read: the button says "Trust this folder", the stored value says
    /// "Untrusted", the summary says yes/no. Rejecting the other two would make a security control
    /// feel broken at the moment it is reached for.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("y")]
    [InlineData("trust")]
    [InlineData("trusted")]
    [InlineData("on")]
    [InlineData("YES")]
    [InlineData("  yes  ")]
    public void TrustingAcceptsEveryReasonableSpelling(string argument) =>
        Assert.Equal(TrustState.Trusted, Run(argument, TrustState.Untrusted).NewState);

    [Theory]
    [InlineData("no")]
    [InlineData("n")]
    [InlineData("untrust")]
    [InlineData("untrusted")]
    [InlineData("off")]
    [InlineData("NO")]
    public void UntrustingAcceptsEveryReasonableSpelling(string argument) =>
        Assert.Equal(TrustState.Untrusted, Run(argument, TrustState.Trusted).NewState);

    /// <summary>
    /// AN UNRECOGNISED ARGUMENT CHANGES NOTHING AND SAYS SO. Guessing a direction here would be the
    /// worst possible default for a security control: `/trust maybe` silently trusting the folder is
    /// how a typo grants every read and write in it.
    /// </summary>
    [Fact]
    public void AnUnknownArgument_ChangesNothing()
    {
        var result = Run("maybe", TrustState.Untrusted);

        Assert.Null(result.NewState);
        Assert.Equal(Severity.Warning, result.Reply.Severity);
        Assert.Contains("/trust yes", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>Both routes to the same state must word it identically — see TrustCommand.Changed.
    /// Two copies of this sentence is how the startup question and the command drift into
    /// describing the same folder differently.</summary>
    [Fact]
    public void TheChangedWording_IsSharedWithTheStartupQuestion()
    {
        Assert.Equal(TrustCommand.Changed(TrustState.Trusted, Root).Text,
            Run("yes", TrustState.Untrusted).Reply.Text);
        Assert.Equal(TrustCommand.Changed(TrustState.Untrusted, Root).Text,
            Run("no", TrustState.Trusted).Reply.Text);
    }
}
