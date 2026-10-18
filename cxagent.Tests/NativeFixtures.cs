namespace CxAgent.Tests;

/// <summary>
/// Where the native test fixtures are, on whichever platform the tests run.
///
/// <para>THE FILE NAME FOLLOWS THE PLATFORM — .so, .dylib, .dll — because the fixtures are built as
/// each platform's own shared library, and the native tests run on Windows and macOS as well as
/// Linux. A test that names ".so" passes only where nothing else was ever tried.</para>
/// </summary>
internal static class NativeFixtures
{
    /// <summary>This platform's shared-library extension.</summary>
    public static string Suffix =>
        OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>The fixture built under <paramref name="name"/>, or null when this machine built none
    /// (no C compiler) — the tests that need one then skip, as they always have.</summary>
    public static string? Path(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, name + Suffix);
        return File.Exists(path) ? path : null;
    }
}
