using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a session tab is called, given the others.
///
/// <para>A LABEL IS RELATIVE, which is the whole reason this is a function over the SET rather than
/// a property of a path: opening `other/src` beside an existing `src` has to relabel the first tab
/// too, or the unqualified one reads as the main session rather than the one that was there first.</para>
/// </summary>
public class TabLabelsTests
{
    [Fact]
    public void DistinctNamesNeedNoQualifying()
    {
        Assert.Equal(["cxagent", "cxgpu"],
            TabLabels.For(["/home/nick/source/cxagent", "/home/nick/source/cxgpu"]));
    }

    [Fact]
    public void CollidingNamesTakeTheirParent()
    {
        Assert.Equal(["myapp/src", "other/src"],
            TabLabels.For(["/work/myapp/src", "/work/other/src"]));
    }

    /// <summary>ONE COLLISION DOES NOT QUALIFY EVERYTHING. A third tab whose name is unique keeps
    /// its short label — qualifying every tab against the possibility of a collision would make
    /// every label longer to no purpose.</summary>
    [Fact]
    public void OnlyTheCollidingOnesAreQualified()
    {
        Assert.Equal(["myapp/src", "other/src", "docs"],
            TabLabels.For(["/work/myapp/src", "/work/other/src", "/work/docs"]));
    }

    /// <summary>
    /// THE SAME FOLDER TWICE FALLS TO A COUNTER, because nothing about either path can tell them
    /// apart. Reachable only on purpose: a folder resolves to its existing session by default.
    /// </summary>
    [Fact]
    public void TheSameFolderTwiceIsNumbered()
    {
        Assert.Equal(["myapp/src (1)", "myapp/src (2)"],
            TabLabels.For(["/work/myapp/src", "/work/myapp/src"]));
    }

    [Fact]
    public void OneSessionIsJustItsName()
    {
        Assert.Equal(["cxagent"], TabLabels.For(["/home/nick/source/cxagent"]));
    }

    /// <summary>A TRAILING SEPARATOR IS NOT A DIFFERENT FOLDER. Paths arrive from a picker, a
    /// command argument and a working directory, and only some of them are normalised.</summary>
    [Fact]
    public void ATrailingSeparatorDoesNotChangeTheName()
    {
        Assert.Equal(["cxagent"], TabLabels.For(["/home/nick/source/cxagent/"]));
    }

    /// <summary>A root has no name to take, so it keeps the only thing it has.</summary>
    [Fact]
    public void ARootStillGetsALabel()
    {
        var labels = TabLabels.For(["/"]);

        Assert.Single(labels);
        Assert.NotEmpty(labels[0]);
    }
}
