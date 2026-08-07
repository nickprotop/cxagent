using CxAgent.Helpers;
using Xunit;

namespace CxAgent.Tests;

public class UlidGeneratorTests
{
    [Fact]
    public void NewId_Produces26CharUniqueSortableIds()
    {
        var a = UlidGenerator.NewId();
        var b = UlidGenerator.NewId();

        Assert.Equal(26, a.Length);
        Assert.NotEqual(a, b);
        // Monotonic: an id generated later sorts >= one generated earlier (ordinal).
        Assert.True(string.CompareOrdinal(b, a) >= 0);
    }
}
