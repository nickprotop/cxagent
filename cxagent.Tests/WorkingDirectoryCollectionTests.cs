using System.Reflection;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE TESTS THAT MOVE THE PROCESS'S FOLDER RUN ALONE. Any agent built without a folder reads the process's, so a
/// move that overlaps any other test can change what that test sees.
/// </summary>
public sealed class WorkingDirectoryCollectionTests
{
    [Fact]
    public void TheWorkingDirectoryCollection_RunsWithNothingBesideIt()
    {
        var definition = typeof(WorkingDirectoryCollection).GetCustomAttribute<CollectionDefinitionAttribute>();

        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
    }
}
