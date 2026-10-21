using System.Xml.Linq;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The release binary is one self-contained file per platform, and only that file is uploaded.
///
/// <para>A NATIVE LIBRARY LEFT BESIDE IT IS A LIBRARY THE RELEASE DOES NOT HAVE. .NET bundles managed
/// assemblies into a single-file publish but leaves native ones next to it unless told otherwise —
/// and the release uploads the binary alone. Without TextMateSharp's libonigwrap the first code
/// block highlighted in the transcript or a plugin's README ended the process; without
/// libe_sqlite3 input history was never saved. Neither shows in any test run, which uses the
/// build output where both files sit beside the assembly.</para>
/// </summary>
public class SingleFilePublishTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "cxagent", "cxagent.csproj"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"cxagent/cxagent.csproj not found walking up from '{AppContext.BaseDirectory}'.");
    }

    [Fact]
    public void ASingleFilePublish_BundlesTheNativeLibraries()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot(), "cxagent", "cxagent.csproj"));
        var value = project.Descendants("IncludeNativeLibrariesForSelfExtract").Select(e => e.Value.Trim()).FirstOrDefault();
        Assert.Equal("true", value);
    }
}
