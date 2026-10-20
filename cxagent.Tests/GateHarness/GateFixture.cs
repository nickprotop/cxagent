namespace CxAgent.Tests.GateHarness;

/// <summary>
/// The tree every gate-harness case runs against: a project, a sub-folder a child can be rooted in, a
/// folder outside both, links that stay in and links that leave, code folders, and an alias that opens
/// the project through a symlink.
///
/// <para>EVERY FILE SAYS WHICH FOLDER IT IS IN — by content (<c>MARK:proj</c>) and by a name only that
/// folder has (<c>proj-only.txt</c>) — so a real run's output shows where its bytes came from, whether
/// the verb prints contents (<c>cat</c>) or names (<c>ls</c>). The top folder holding them all has a name
/// of its own too, so a listing of it is seen for the leak it is.</para>
///
/// <para>THE PROCESS DIRECTORY IS <see cref="Outside"/> for the fixture's life, and <see cref="Proj"/> only
/// while <see cref="Place.ProcessDirectory"/>'s cases run. Code that resolves a path against it rather than
/// against the agent's folder then reads OUTSIDE files and fails the harness, instead of passing by the
/// accident of where the test runner happens to sit. The harness runs in the working-directory collection
/// for that reason.</para>
///
/// <para>LINUX ONLY: the links need no privilege there, and the oracle needs GNU <c>realpath</c>'s
/// <c>-m</c> and <c>-z</c>. Elsewhere the fixture builds nothing and <see cref="Supported"/> says so.</para>
/// </summary>
public sealed class GateFixture : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();

    /// <summary>Whether this platform can run the harness.</summary>
    public static bool Supported => OperatingSystem.IsLinux();

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
        if (!Supported) return;

        Directory.CreateDirectory(Outside);
        Write(Top, "top-only.txt", "MARK:top");
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
        // A LINK OUT THAT ONLY sub HAS, so a path read past `cd sub` is not hidden by a namesake in the project.
        File.CreateSymbolicLink(Path.Combine(Sub, "subout"), Path.Combine(Outside, "secret.txt"));
        Directory.CreateSymbolicLink(Alias, Proj);

        Directory.SetCurrentDirectory(Outside);
    }

    /// <summary>Where an agent in <paramref name="place"/> works, what bounds it, and whose trust it reads.</summary>
    public PlaceSetup SetupFor(Place place) => place switch
    {
        Place.NewFolder => new PlaceSetup(AgentFolder: Proj, Boundary: Proj, PolicyRoot: Proj, Narrow: false),
        Place.ProcessDirectory => new PlaceSetup(AgentFolder: Proj, Boundary: Proj, PolicyRoot: Proj, Narrow: false),
        // TRUST IS THE PROJECT'S: a child's policy is narrowed from the project's, and reads its trust.
        Place.RootedChild => new PlaceSetup(AgentFolder: Sub, Boundary: Sub, PolicyRoot: Proj, Narrow: true),
        // OPENED THROUGH THE LINK: trust is stored under the folder as it was opened.
        Place.SymlinkedProject => new PlaceSetup(AgentFolder: Alias, Boundary: Proj, PolicyRoot: Alias, Narrow: false),
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    /// <summary>The process directory while <paramref name="place"/>'s cases run.</summary>
    public string ProcessDirectoryFor(Place place) => place == Place.ProcessDirectory ? Proj : Outside;

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        try { Directory.Delete(Top, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Write(string folder, string name, string text) =>
        File.WriteAllText(Path.Combine(folder, name), text);
}

/// <summary>
/// Where an agent works: a folder other than the process directory, the process directory itself (the
/// ordinary session), a child rooted in a sub-folder, or a project opened through a symlink.
/// </summary>
public enum Place { NewFolder, ProcessDirectory, RootedChild, SymlinkedProject }

/// <summary>
/// One place's setup. <see cref="PolicyRoot"/> is the folder the policy is built on and its trust stored
/// under; <see cref="Narrow"/> narrows it to <see cref="AgentFolder"/> for a rooted child.
/// </summary>
public sealed record PlaceSetup(string AgentFolder, string Boundary, string PolicyRoot, bool Narrow);
