using CxAgent.Core.Colleagues;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// ONLY A PROJECT IS A CODEBASE (user, 2026-11-11): sessions run in /tmp left a colleague a "tmp" experience file — a
/// codebase that was only a scratch folder. A folder is a project when it belongs to a git repository or holds a
/// project file of its own.
/// </summary>
public sealed class CodebaseProjectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codebase-project-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_bare_folder_is_not_a_project() => Assert.False(Codebase.IsProject(_dir));

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("App.sln")]
    [InlineData("package.json")]
    [InlineData("Cargo.toml")]
    [InlineData("go.mod")]
    [InlineData("pyproject.toml")]
    public void A_folder_holding_a_project_file_is_one(string file)
    {
        File.WriteAllText(Path.Combine(_dir, file), "");
        Assert.True(Codebase.IsProject(_dir));
    }

    [Fact]
    public void A_folder_inside_a_git_repository_is_one()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "src", "deep")).FullName;

        Assert.True(Codebase.IsProject(sub));
    }

    [Fact]
    public void The_system_temp_folder_is_not_one() => Assert.False(Codebase.IsProject(Path.GetTempPath()));
}
