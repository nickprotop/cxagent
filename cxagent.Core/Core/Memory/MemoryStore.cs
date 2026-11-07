using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Memory;

/// <summary>Whose a note is: the user's, kept for every folder, or one project's.</summary>
public enum MemoryScope
{
    /// <summary>About the user — read in every folder.</summary>
    Global,

    /// <summary>About one project — read only in that folder.</summary>
    Project,
}

/// <summary>One note the agent kept.</summary>
/// <param name="Name">A kebab-case slug, unique across both scopes; also its file name.</param>
/// <param name="Description">One line — what the index shows, so the model can tell whether to read it.</param>
/// <param name="Scope">Whose it is.</param>
/// <param name="Body">The note itself.</param>
public sealed record MemoryNote(string Name, string Description, MemoryScope Scope, string Body);

/// <summary>
/// The agent's notes across sessions: one markdown file each, under cxagent's own folder —
/// <c>memory/global/</c> for the user, <c>memory/projects/&lt;folder&gt;-&lt;hash&gt;/</c> for a project.
///
/// <para>NEVER IN THE REPOSITORY. What the agent learns about a person is theirs, not something to commit beside the
/// code for everyone who clones it; and a project's notes in its own tree would be one more thing for the user to
/// keep out of a diff.</para>
///
/// <para>READ FROM DISK EVERY CALL, no cache: the files are the truth, a user may edit one in the editor tab or by
/// hand between two calls, and a few small files cost nothing to read.</para>
/// </summary>
public sealed partial class MemoryStore
{
    /// <summary>The longest description kept — an index line, not a paragraph.</summary>
    public const int MaxDescription = 200;

    /// <summary>The longest note kept. A note is a fact or a preference; a document belongs in a file.</summary>
    public const int MaxBody = 20_000;

    private readonly string? _project;

    /// <param name="configDir">cxagent's own folder.</param>
    /// <param name="projectDir">The folder the session works in, or null when there is none — then only global notes
    /// can be kept.</param>
    public MemoryStore(string configDir, string? projectDir)
    {
        var root = Path.Combine(configDir, "memory");
        GlobalFolder = Path.Combine(root, "global");
        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            _project = RealPath(projectDir);
            ProjectFolder = Path.Combine(root, "projects", FolderName(_project));
        }
    }

    /// <summary>Where the user's own notes are.</summary>
    public string GlobalFolder { get; }

    /// <summary>Where this project's notes are, or null without a project.</summary>
    public string? ProjectFolder { get; }

    /// <summary>The project's folder as the store keys it, or null.</summary>
    public string? Project => _project;

    /// <summary>
    /// Every note this session can see: the global ones, then the project's, each sorted by name — the order the
    /// index shows them in, so the prompt is the same bytes for the same notes.
    /// </summary>
    public IReadOnlyList<MemoryNote> List() =>
        [.. Read(GlobalFolder, MemoryScope.Global), .. ProjectFolder is { } p ? Read(p, MemoryScope.Project) : []];

    /// <summary>The note called <paramref name="name"/>, or null.</summary>
    public MemoryNote? Find(string name) =>
        PathOf(name) is { } path ? Parse(path, ScopeOf(path)) : null;

    /// <summary>
    /// The file of the note called <paramref name="name"/>, or null. A project's note wins over a global one of the
    /// same name, which only a hand-made file can produce: the store itself keeps a name to one scope.
    /// </summary>
    public string? PathOf(string name)
    {
        if (!ValidName(name)) return null;
        foreach (var folder in new[] { ProjectFolder, GlobalFolder })
        {
            if (folder is null) continue;
            var path = Path.Combine(folder, name + ".md");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Writes a note, replacing one of the same name in the same scope; returns why not, or null.</summary>
    public string? Write(MemoryNote note)
    {
        if (!ValidName(note.Name))
            return $"'{note.Name}' is not a note name: use a short kebab-case slug, like prefers-tabs or deploy-target.";
        var description = note.Description.ReplaceLineEndings(" ").Trim();
        if (description.Length == 0) return "A note needs a one-line description — it is what the index shows.";
        if (description.Length > MaxDescription) return $"The description is {description.Length} characters; keep it to one line under {MaxDescription}.";
        if (note.Body.Length > MaxBody) return $"The note is {note.Body.Length} characters; keep notes under {MaxBody}. A document belongs in a file.";

        // REFUSED, NOT REDACTED: a note with a key cut out of it is a note that lies about what it says, and the user
        // should know the agent tried to keep a secret at all.
        if (LooksSecret(description) || LooksSecret(note.Body))
            return "That looks like a secret — a key, token or password. Notes are plain files in cxagent's folder; do not keep secrets in them.";

        var folder = note.Scope == MemoryScope.Global ? GlobalFolder : ProjectFolder;
        if (folder is null) return "There is no project folder in this session, so only global notes can be kept.";

        // ONE NAME, ONE NOTE: /memory open and forget take a name alone, and a name in both scopes would make them guess.
        var other = note.Scope == MemoryScope.Global ? ProjectFolder : GlobalFolder;
        if (other is not null && File.Exists(Path.Combine(other, note.Name + ".md")))
            return $"A {(note.Scope == MemoryScope.Global ? "project" : "global")} note is already called '{note.Name}'. Update that one, or pick another name.";

        Directory.CreateDirectory(folder);
        if (note.Scope == MemoryScope.Project && _project is not null)
        {
            // WHICH PROJECT THIS IS, beside its notes: the folder name is a hash, and /memory has to say whose they are.
            var marker = Path.Combine(folder, "path");
            if (!File.Exists(marker)) File.WriteAllText(marker, _project + "\n");
        }

        var text = $"---\nname: {note.Name}\ndescription: {description}\n---\n\n{note.Body.TrimEnd()}\n";
        var target = Path.Combine(folder, note.Name + ".md");
        // WHOLE OR NOT AT ALL: an editor tab open on the note, or a session reading the index, never sees half a file.
        var temp = target + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, target, overwrite: true);
        return null;
    }

    /// <summary>Deletes the note called <paramref name="name"/>; false when there was none.</summary>
    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Whether <paramref name="name"/> can name a note.</summary>
    public static bool ValidName(string name) => name.Length is > 0 and <= 64 && NamePattern().IsMatch(name);

    /// <summary>
    /// Whether <paramref name="text"/> carries something that looks like a credential: a known key format, a private
    /// key block, or a key, token or password assigned a value. Talking ABOUT passwords is not one.
    /// </summary>
    public static bool LooksSecret(string text) => SecretPattern().IsMatch(text);

    private MemoryScope ScopeOf(string path) =>
        ProjectFolder is not null && path.StartsWith(ProjectFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? MemoryScope.Project : MemoryScope.Global;

    private static IEnumerable<MemoryNote> Read(string folder, MemoryScope scope)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.md")
            .Where(f => ValidName(Path.GetFileNameWithoutExtension(f)))
            .Order(StringComparer.Ordinal)
            .Select(f => Parse(f, scope))
            .OfType<MemoryNote>()
            .ToList();
    }

    /// <summary>
    /// A note from its file. The NAME IS THE FILE'S, whatever the frontmatter says: a hand edit that renamed one inside
    /// would otherwise list a note no read could find. A file with no frontmatter is all body and no description.
    /// </summary>
    private static MemoryNote? Parse(string path, MemoryScope scope)
    {
        string text;
        try { text = File.ReadAllText(path).ReplaceLineEndings("\n"); }
        catch (IOException) { return null; }

        var description = "";
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
        {
            foreach (var line in text[4..end].Split('\n'))
                if (line.StartsWith("description:", StringComparison.Ordinal)) description = line["description:".Length..].Trim();
            var after = text.IndexOf('\n', end + 1);
            body = after < 0 ? "" : text[(after + 1)..];
        }
        return new MemoryNote(Path.GetFileNameWithoutExtension(path), description, scope, body.Trim('\n'));
    }

    // THE REAL FOLDER: a project reached through a link is the same project, and its notes must not split in two.
    private static string RealPath(string dir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        try { return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full; }
        catch (IOException) { return full; }
    }

    // READABLE AND UNIQUE: the folder's own name so a person browsing ~/.config/cxagent/memory can tell them apart, a
    // hash of the whole path so two folders of one name do not share notes. Case-folded where the file system is.
    private static string FolderName(string project)
    {
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? project.ToLowerInvariant() : project;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        var name = SafeChars().Replace(Path.GetFileName(project), "-").Trim('-');
        return name.Length == 0 ? hash : $"{name}-{hash}";
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex SafeChars();

    [GeneratedRegex(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----"
        + @"|\bsk-[A-Za-z0-9_-]{20,}"
        + @"|\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|\bgithub_pat_[A-Za-z0-9_]{30,}"
        + @"|\bxox[abprs]-[A-Za-z0-9-]{10,}"
        + @"|\bAKIA[0-9A-Z]{16}\b"
        + @"|\bAIza[0-9A-Za-z_-]{35}"
        + @"|\b(api[_-]?key|secret|token|password|passwd|pwd)\b\s*[:=]\s*\S{8,}",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();
}
