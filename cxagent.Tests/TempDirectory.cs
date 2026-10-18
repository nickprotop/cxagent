namespace CxAgent.Tests;

/// <summary>Removing a test's temp folder.</summary>
internal static class TempDirectory
{
    /// <summary>
    /// Removes <paramref name="path"/>, tolerating a write that is still landing in it.
    ///
    /// <para>A SESSION OUTLIVES THE TEST THAT MADE IT BY A FEW WRITES. Its lifecycle log and its
    /// detached teardown append after the test's last line, so a write landing between a recursive
    /// delete's scan and its rmdir makes the directory non-empty again — "Directory not empty",
    /// failing a test whose assertions all passed. <c>SessionLifecycleLog</c> documents the same race
    /// from the other side.</para>
    ///
    /// <para>RETRIED, THEN IGNORED, because this is cleanup of a temp folder the OS will reap anyway.
    /// A test that FAILS on its own teardown reports a defect that does not exist, which is worse
    /// than a stray directory under /tmp.</para>
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
