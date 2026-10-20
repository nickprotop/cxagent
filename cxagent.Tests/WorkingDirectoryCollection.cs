using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Runs every test class that calls <c>Directory.SetCurrentDirectory</c> on its own, with no other test beside it.
///
/// <para>WHY: the working directory is PROCESS-GLOBAL, and xunit runs test classes in parallel. A test that
/// switches to a temp directory and restores it in a <c>finally</c> is still unsafe: for as long as it holds the
/// switch, every other test in the process resolves relative paths against somewhere it did not choose, and an
/// agent built with no folder of its own (<c>Agent.TryGetWorkingDirectory</c>) names that place in its prompt. Two
/// such tests interleaving is worse — the second captures the first's temp directory as "previous" and restores
/// that, leaving the process pointing at a directory about to be deleted, where a relative-path cleanup deletes
/// whatever it finds.</para>
///
/// <para>NOT PARALLEL WITH ANYTHING, not merely with each other: the readers are every test whose agent was given
/// no folder, far more than the movers, and only running the movers alone keeps all of them safe. xunit runs this
/// collection after the parallel ones; it is a handful of classes, so the suite barely notices.</para>
///
/// <para>Where a test can take the directory as a parameter instead, it should — discovery does. The movers here are
/// the tests whose point is what happens when the process is somewhere else.</para>
/// </summary>
[CollectionDefinition("working-directory", DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
}
