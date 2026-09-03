using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The file-tab tests run one at a time.
///
/// <para>THEY SHARE PROCESS-WIDE STATE that the app legitimately keeps global: FileTab.SuppressWatch
/// is one flag for one process, because a save is a save whichever window made it. Run in parallel,
/// one class's save suppresses another's watcher and a test that should see a change sees nothing —
/// a one-in-eight failure that says nothing about the code under test.</para>
///
/// <para>xUnit runs test CLASSES in parallel by default; a shared collection is how you say these
/// particular ones must not be.</para>
/// </summary>
[CollectionDefinition("file-tabs", DisableParallelization = true)]
public sealed class FileTabCollection { }
