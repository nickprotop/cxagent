using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The tests that put a real shell in <c>DetachedProcessRegistry.Default</c>, or read it expecting only
/// their own: run alone, never beside another class.
///
/// <para>THE REGISTRY IS ONE PER PROCESS, AND EVERY MANAGER'S DISPOSE EMPTIES IT. <c>SessionManager.Dispose</c>
/// reaps the default registry because at app exit that is the only reaping a backgrounded command gets —
/// correct for the app, and a hazard for a test run, where dozens of classes each dispose a manager. Beside
/// them, a shell one test parked in the registry is killed by another test's teardown, and a test that
/// lists the registry sees a shell some other class started. Neither failure says anything about the code
/// under test.</para>
///
/// <para>A COLLECTION RATHER THAN A SEAM. The tests here reach the registry through a session or an agent,
/// which take the default by design; isolating them costs a few hundred milliseconds of serial time and
/// changes nothing a user runs.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DefaultShellRegistryCollection
{
    public const string Name = "default-shell-registry";
}
