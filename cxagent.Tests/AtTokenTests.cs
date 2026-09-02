using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Whether the caret sits in an <c>@</c> reference, which is the only question the menu asks.
/// </summary>
public class AtTokenTests
{
    [Fact]
    public void AnAtAfterASpace_OpensAToken()
    {
        var token = AtToken.At("fix the bug in @Shell", caret: 21);

        Assert.NotNull(token);
        Assert.Equal(15, token!.Value.Start);
        Assert.Equal("Shell", token.Value.Prefix);
    }

    [Fact]
    public void AnAtAtTheStart_OpensAToken()
    {
        Assert.Equal("src", AtToken.At("@src", caret: 4)!.Value.Prefix);
    }

    /// <summary>
    /// AN EMAIL ADDRESS IS NOT A REFERENCE. The @ follows a letter, so it is literal — this is the
    /// case that decides the rule, because it is common and a menu opening inside it is wrong every
    /// time.
    /// </summary>
    [Fact]
    public void AnAtInsideAWord_IsLiteral()
    {
        Assert.Null(AtToken.At("mail nick@example.com", caret: 21));
        Assert.Null(AtToken.At("[Fact]", caret: 6));
    }

    /// <summary>An empty prefix is a token: `@` alone offers everything.</summary>
    [Fact]
    public void ABareAt_OpensWithNoPrefix()
    {
        var token = AtToken.At("look at @", caret: 9);

        Assert.NotNull(token);
        Assert.Equal("", token!.Value.Prefix);
    }

    /// <summary>A space ends the token — what follows is prose, not more path.</summary>
    [Fact]
    public void ASpaceEndsTheToken()
    {
        Assert.Null(AtToken.At("@src/UI now fix it", caret: 18));
    }

    /// <summary>The caret must be IN the token. Typing @foo then clicking to the start is not.</summary>
    [Fact]
    public void ACaretBeforeTheAt_IsNotInIt()
    {
        Assert.Null(AtToken.At("fix @Shell", caret: 2));
    }

    [Fact]
    public void NoAtAtAll_IsNull()
    {
        Assert.Null(AtToken.At("fix the bug", caret: 11));
        Assert.Null(AtToken.At("", caret: 0));
    }
}
