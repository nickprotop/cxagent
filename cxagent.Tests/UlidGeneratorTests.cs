using CxAgent.Core.Helpers;
using Xunit;

namespace CxAgent.Tests;

public class UlidGeneratorTests
{
    [Fact]
    public void NewId_Produces26CharUniqueIds()
    {
        var a = UlidGenerator.NewId();
        var b = UlidGenerator.NewId();

        Assert.Equal(26, a.Length);
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// THE POINT OF THE LAYOUT: ids minted together must differ from their FIRST character.
    ///
    /// <para>An id is something people read and abbreviate, and a timestamp-first ULID makes that
    /// impossible for exactly the ids most likely to be compared — three sessions started while
    /// driving the /sessions listing all rendered as <c>01KZXC</c>. The log directory names shared
    /// the same prefix.</para>
    /// </summary>
    [Fact]
    public void NewId_IdsMintedTogetherDifferInTheirOpeningCharacters()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => UlidGenerator.NewId()).ToList();

        // Six characters is what a listing shows; every one of them must be its own.
        var shorts = ids.Select(id => id[..6]).ToHashSet();

        Assert.Equal(ids.Count, shorts.Count);
    }

    /// <summary>The timestamp is still in there — moved to the tail, not dropped.</summary>
    [Fact]
    public void NewId_StillCarriesTheTimeItWasMinted()
    {
        var before = UlidGenerator.NewId();
        Thread.Sleep(5);
        var after = UlidGenerator.NewId();

        // The last ten characters are the millisecond clock, so they still advance.
        Assert.True(string.CompareOrdinal(after[16..], before[16..]) > 0);
    }

    /// <summary>Crockford base32 throughout: no letters that misread as digits.</summary>
    [Fact]
    public void NewId_UsesOnlyTheCrockfordAlphabet()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        Assert.All(UlidGenerator.NewId(), c => Assert.Contains(c, alphabet));
    }
}
