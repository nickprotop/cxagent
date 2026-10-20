namespace CxAgent.Tests.GateHarness;

/// <summary>
/// The tree every gate-harness case runs against: a project, a sub-folder a child can be rooted in, a
/// folder outside both, links that stay in and links that leave, code folders, and an alias that opens
/// the project through a symlink.
///
/// <para>EVERY FILE SAYS WHICH FOLDER IT IS IN — by content (<c>MARK:proj</c>) and by a name only that
/// folder has (<c>proj-only.txt</c>) — so a real run's output shows where its bytes came from, whether
/// the verb prints contents (<c>cat</c>) or names (<c>ls</c>).</para>
///
/// <para>THE PROCESS DIRECTORY IS SET TO <see cref="Outside"/> for the fixture's life. Code that resolves
/// a path against it rather than against the agent's folder then reads OUTSIDE files and fails the
/// harness, instead of passing by the accident of where the test runner happens to sit. The harness runs
/// in the working-directory collection for that reason.</para>
/// </summary>
public sealed class GateFixture : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();

    public string Top { get; }
    public string Proj { get; }
    public string Sub { get; }
    public string Outside { get; }
    public string Alias { get; }

    public GateFixture()
    {
        Top = Path.Combine(Path.GetTempPath(), "cx-gate-" + Guid.NewGuid().ToString("N"));
        Proj = Path.Combine(Top, "proj");
        Sub = Path.Combine(Proj, "sub");
        Outside = Path.Combine(Top, "outside");
        Alias = Path.Combine(Top, "alias");

        Directory.CreateDirectory(Outside);
        Write(Outside, "in.txt", "MARK:outside");
        Write(Outside, "out-only.txt", "MARK:outside");
        Write(Outside, "secret.txt", "MARK:outside");

        Directory.CreateDirectory(Sub);
        Write(Proj, "in.txt", "MARK:proj");
        Write(Proj, "proj-only.txt", "MARK:proj");
        Write(Sub, "in.txt", "MARK:sub");
        Write(Sub, "sub-only.txt", "MARK:sub");

        foreach (var folder in new[] { Proj, Sub })
        {
            Directory.CreateDirectory(Path.Combine(folder, ".git", "hooks"));
            Write(Path.Combine(folder, ".git", "hooks"), "pc", "#!/bin/sh");
            File.CreateSymbolicLink(Path.Combine(folder, "linkout"), Path.Combine(Outside, "secret.txt"));
            File.CreateSymbolicLink(Path.Combine(folder, "dangling"), Path.Combine(Outside, "missing.txt"));
            Directory.CreateSymbolicLink(Path.Combine(folder, "linkdir"), Outside);
        }
        File.CreateSymbolicLink(Path.Combine(Proj, "linkin"), Path.Combine(Proj, "in.txt"));
        File.CreateSymbolicLink(Path.Combine(Sub, "linkin"), Path.Combine(Sub, "in.txt"));
        File.CreateSymbolicLink(Path.Combine(Sub, "linkup"), Path.Combine(Proj, "in.txt"));
        Directory.CreateSymbolicLink(Alias, Proj);

        Directory.SetCurrentDirectory(Outside);
    }

    /// <summary>Where an agent in <paramref name="place"/> works, what bounds it, and whose trust it reads.</summary>
    public PlaceSetup SetupFor(Place place) => place switch
    {
        Place.NewFolder => new PlaceSetup(AgentFolder: Proj, Boundary: Proj, PolicyRoot: Proj, Narrow: false),
        // TRUST IS THE PROJECT'S: a child's policy is narrowed from the project's, and reads its trust.
        Place.RootedChild => new PlaceSetup(AgentFolder: Sub, Boundary: Sub, PolicyRoot: Proj, Narrow: true),
        // OPENED THROUGH THE LINK: trust is stored under the folder as it was opened.
        Place.SymlinkedProject => new PlaceSetup(AgentFolder: Alias, Boundary: Proj, PolicyRoot: Alias, Narrow: false),
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        try { Directory.Delete(Top, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Write(string folder, string name, string text) =>
        File.WriteAllText(Path.Combine(folder, name), text);
}

/// <summary>Where an agent works: an ordinary session elsewhere than the process directory, a child rooted
/// in a sub-folder, or a project opened through a symlink.</summary>
public enum Place { NewFolder, RootedChild, SymlinkedProject }

/// <summary>
/// One place's setup. <see cref="PolicyRoot"/> is the folder the policy is built on and its trust stored
/// under; <see cref="Narrow"/> narrows it to <see cref="AgentFolder"/> for a rooted child.
/// </summary>
public sealed record PlaceSetup(string AgentFolder, string Boundary, string PolicyRoot, bool Narrow);
