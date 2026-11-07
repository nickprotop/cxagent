using CxAgent.Core.Memory;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The notes the agent keeps across sessions: one markdown file each, in cxagent's own folder, global or per project.
/// </summary>
public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("mem-config-").FullName;
    private readonly string _projectA = Directory.CreateTempSubdirectory("mem-a-").FullName;
    private readonly string _projectB = Directory.CreateTempSubdirectory("mem-b-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _projectA, _projectB })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private MemoryStore Store(string? project) => new(_config, project);

    private static MemoryNote Note(string name, MemoryScope scope, string body = "the body", string description = "what it is") =>
        new(name, description, scope, body);

    [Fact]
    public void A_written_note_is_listed_and_read_back()
    {
        var store = Store(_projectA);
        Assert.Null(store.Write(Note("prefers-tabs", MemoryScope.Global, "Indent with tabs.", "Indentation preference")));

        var note = Assert.Single(store.List());
        Assert.Equal("prefers-tabs", note.Name);
        Assert.Equal("Indentation preference", note.Description);
        Assert.Equal(MemoryScope.Global, note.Scope);
        Assert.Equal("Indent with tabs.", store.Find("prefers-tabs")!.Body);
    }

    [Fact]
    public void A_note_is_a_markdown_file_with_its_name_and_description_up_front()
    {
        var store = Store(_projectA);
        store.Write(Note("deploy-target", MemoryScope.Project, "Deploys go to fly.io.", "Where it deploys"));

        var text = File.ReadAllText(store.PathOf("deploy-target")!);
        Assert.StartsWith("---\nname: deploy-target\ndescription: Where it deploys\n---\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Deploys go to fly.io.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_notes_stay_with_their_project_and_global_ones_go_everywhere()
    {
        Store(_projectA).Write(Note("about-a", MemoryScope.Project));
        Store(_projectA).Write(Note("about-me", MemoryScope.Global));

        var fromB = Store(_projectB).List().Select(n => n.Name).ToList();
        Assert.Equal(["about-me"], fromB);
        Assert.Equal(["about-me", "about-a"], Store(_projectA).List().Select(n => n.Name).ToList());
    }

    [Fact]
    public void Notes_live_in_cxagents_folder_never_in_the_project()
    {
        var store = Store(_projectA);
        store.Write(Note("fact", MemoryScope.Project));

        Assert.StartsWith(Path.Combine(_config, "memory"), store.PathOf("fact")!, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_projectA));
    }

    [Fact]
    public void A_projects_folder_records_which_project_it_is()
    {
        var store = Store(_projectA);
        store.Write(Note("fact", MemoryScope.Project));

        Assert.Equal(Path.GetFullPath(_projectA), File.ReadAllText(Path.Combine(store.ProjectFolder!, "path")).Trim());
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("../escape")]
    [InlineData("trailing-")]
    [InlineData("")]
    public void A_name_that_is_not_a_kebab_case_slug_is_refused(string name)
    {
        var store = Store(_projectA);
        Assert.NotNull(store.Write(Note(name, MemoryScope.Global)));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData("The key is sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("password = hunter2-but-longer")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nabc")]
    [InlineData("AKIAABCDEFGHIJKLMNOP is the access key")]
    public void A_note_that_looks_like_a_secret_is_refused(string body)
    {
        var store = Store(_projectA);
        var problem = store.Write(Note("creds", MemoryScope.Global, body));

        Assert.Contains("secret", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List());
    }

    [Fact]
    public void A_secret_in_the_description_is_refused_too() =>
        Assert.NotNull(Store(_projectA).Write(Note("creds", MemoryScope.Global, "fine", "api_key: abcdef0123456789")));

    [Fact]
    public void Talking_about_passwords_is_not_a_secret() =>
        Assert.Null(Store(_projectA).Write(Note("auth", MemoryScope.Project, "Passwords are hashed with argon2; the token lives in the keychain.")));

    [Fact]
    public void A_name_is_one_note_across_both_scopes()
    {
        // ONE NAME, ONE NOTE: /memory open and forget take a name alone, and a name in both scopes would make them guess.
        var store = Store(_projectA);
        store.Write(Note("style", MemoryScope.Global));

        Assert.Contains("global", store.Write(Note("style", MemoryScope.Project)), StringComparison.Ordinal);
        Assert.Null(store.Write(Note("style", MemoryScope.Global, "rewritten")));
        Assert.Equal("rewritten", store.Find("style")!.Body);
    }

    [Fact]
    public void A_deleted_note_is_gone()
    {
        var store = Store(_projectA);
        store.Write(Note("old", MemoryScope.Project));

        Assert.True(store.Delete("old"));
        Assert.Null(store.Find("old"));
        Assert.False(store.Delete("old"));
    }

    [Fact]
    public void Without_a_project_folder_only_global_notes_can_be_kept()
    {
        var store = Store(null);
        Assert.NotNull(store.Write(Note("fact", MemoryScope.Project)));
        Assert.Null(store.Write(Note("me", MemoryScope.Global)));
    }

    [Fact]
    public void A_hand_edited_note_without_frontmatter_is_still_listed_by_its_file_name()
    {
        var store = Store(_projectA);
        store.Write(Note("seed", MemoryScope.Global));
        File.WriteAllText(Path.Combine(store.GlobalFolder, "loose.md"), "Just text.\n");

        var loose = store.Find("loose")!;
        Assert.Equal("", loose.Description);
        Assert.Equal("Just text.", loose.Body.Trim());
    }
}

/// <summary>The index the system prompt carries: names and descriptions, both scopes, bounded.</summary>
public sealed class MemoryIndexTests
{
    private static MemoryNote Note(string name, MemoryScope scope, string description = "d") => new(name, description, scope, "");

    [Fact]
    public void Global_notes_come_first_then_the_projects_each_sorted_by_name()
    {
        var text = MemoryIndex.Render([Note("zeta", MemoryScope.Project), Note("beta", MemoryScope.Global),
            Note("alpha", MemoryScope.Project), Note("aardvark", MemoryScope.Global)], 8000);

        var order = new[] { "aardvark", "beta", "alpha", "zeta" }.Select(n => text.IndexOf("- " + n, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order().ToList(), order);
        Assert.True(text.IndexOf("About you", StringComparison.Ordinal) < text.IndexOf("About this project", StringComparison.Ordinal));
    }

    [Fact]
    public void Past_the_cap_the_rest_are_counted_not_listed()
    {
        var notes = Enumerable.Range(0, 200).Select(i => Note($"note-{i:000}", MemoryScope.Global, new string('x', 80))).ToList();
        var text = MemoryIndex.Render(notes, 2000);

        Assert.True(text.Length < 2400);
        Assert.Matches(@"\d+ more — memory list", text);
    }

    [Fact]
    public void No_notes_is_no_list() => Assert.Equal("", MemoryIndex.Render([], 8000));
}
