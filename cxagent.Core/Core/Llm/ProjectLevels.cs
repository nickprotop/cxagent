namespace CxAgent.Core.Llm;

/// <summary>
/// The folders that make up the project an agent works in: its own folder, and every folder above it up to
/// the repository root, nearest first — or its own folder alone when no repository encloses it.
///
/// <para>ONE WALK for the instruction files and the skill and command folders, so the two cannot disagree
/// about which folders are the project's.</para>
///
/// <para>NO REPO, NO WALK. Outside a worktree "the project" has no boundary that means anything, so the only
/// folder that certainly describes this work is the one the agent was started in; collecting ancestors there
/// is how a scratch folder under <c>~</c> would pick up the home folder's own files.</para>
///
/// <para>A HOME THAT IS A REPOSITORY IS NOT THE PROJECT. Dotfiles kept in git put a <c>.git</c> at <c>~</c>,
/// and a folder under it with none of its own — a downloaded tarball — would walk up to it and read the home
/// folder's files as the project's. The home folder is the project only when the agent works in it.</para>
/// </summary>
internal static class ProjectLevels
{
    /// <param name="start">Where the agent works.</param>
    /// <param name="home">The user's home folder, or null for the real one.</param>
    public static List<string> From(string start, string? home = null)
    {
        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var dirs = new List<string>();
        var foundRoot = false;
        try
        {
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                var here = Path.TrimEndingDirectorySeparator(d.FullName);
                if (here == home && dirs.Count > 0) break;
                dirs.Add(d.FullName);

                // A .git ENTRY, FILE OR DIRECTORY: a submodule and a linked worktree mark their root with a .git
                // FILE holding a gitdir: pointer, so testing for the directory alone walks out of the repo.
                var git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) { foundRoot = true; break; }
            }
        }
        catch (Exception)
        {
            // Best effort, like every read in this app that is not the work itself.
        }

        if (!foundRoot && dirs.Count > 1) dirs.RemoveRange(1, dirs.Count - 1);
        return dirs;
    }
}
