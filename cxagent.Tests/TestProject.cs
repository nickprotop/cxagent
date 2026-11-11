namespace CxAgent.Tests;

/// <summary>
/// A temporary folder that counts as a project — it holds a project file — so a colleague keeps experience of it: only a
/// project is a codebase (see Codebase.IsProject). A bare temp folder is a scratch folder and is not one.
/// </summary>
internal static class TestProject
{
    public static string New(string prefix) => Mark(Directory.CreateTempSubdirectory(prefix).FullName);

    /// <summary>Makes <paramref name="folder"/> a project, creating it if needed, and answers it.</summary>
    public static string Mark(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project />");
        return folder;
    }
}
