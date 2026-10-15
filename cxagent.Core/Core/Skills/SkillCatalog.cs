namespace CxAgent.Core.Skills;

/// <summary>One skill: its frontmatter, its directory and its body.</summary>
/// <param name="Name">From the DIRECTORY, not from frontmatter — see <see cref="SkillCatalog"/>.</param>
/// <param name="Directory">The skill's own folder. Returned to the model on load so a later version
/// can point at files beside the SKILL.md without a second capability channel.</param>
/// <param name="Description">What the skill is for — the only thing the model sees before loading it.</param>
/// <param name="Body">The skill text itself, loaded on demand.</param>
public sealed record SkillInfo(string Name, string Description, string Directory, string Body);

/// <summary>A file that looked like a skill and was refused, with the reason a user can act on.</summary>
public sealed record SkillProblem(string Path, string Reason);

/// <summary>
/// What the model may load, and every file discovery refused.
/// </summary>
/// <param name="SourceDirectory">Always null: skills merge per name across folders, so no one folder
/// supplies them — see <see cref="InvocableCatalog"/>. Kept so existing callers still compile.</param>
/// <param name="Skills">The skills that parsed, in the order they will be offered.</param>
/// <param name="Problems">Files that were skipped, and why — the reason /skills exists.</param>
public sealed record SkillCatalogResult(
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<SkillProblem> Problems,
    string? SourceDirectory);

/// <summary>
/// Skills — instructions the model loads when it needs them, instead of paying for them on every
/// turn.
///
/// <para>THE SPLIT THAT MAKES THIS WORTH HAVING: the CATALOG (name + description) rides in the system
/// prompt permanently and costs a few hundred characters; the BODY is fetched by a tool only when the
/// model decides a task matches. Twenty skills of 3k each would be 60k of permanent prefix.</para>
///
/// <para>DISCOVERY IS <see cref="InvocableCatalog"/>'s, and it RUNS PER TURN, inside the prompt build, exactly as <see cref="Llm.ProjectInstructions"/>
/// does — there is no cache, no snapshot and no refresh command. Unchanged files render byte-identical
/// text and the system message is replaced only when it differs, so the prompt prefix holds; editing a
/// skill costs one prefix and takes effect on the next turn. That is the user's call to make, and an
/// agent that ignored their edit until a restart would be behaving as though it knew better.</para>
/// </summary>
public static class SkillCatalog
{
    /// <summary>
    /// The skills the model may load: the model's view of <see cref="InvocableCatalog"/>.
    ///
    /// <para>PER NAME, NEAREST FIRST, across project, config and home — see the catalog. A
    /// "you only" skill (<c>disable-model-invocation</c>) and a name a command file took are not
    /// offered, so the prompt, the <c>skill</c> tool's offer and its loading all read one list.</para>
    /// </summary>
    public static SkillCatalogResult Find(string startDirectory, string? globalDirectory = null,
        string? homeDirectory = null)
    {
        var all = InvocableCatalog.Find(new CatalogRoots(startDirectory, globalDirectory, homeDirectory));
        return new SkillCatalogResult(all.ModelSkills, all.Problems, null);
    }

    /// <summary>
    /// A SKILL.md's frontmatter and body, or null with a reason recorded.
    ///
    /// <para>FRONTMATTER IS REQUIRED HERE, unlike a command file: the description is the only thing the
    /// model sees before loading a skill, so a skill without one is refused rather than guessed at.</para>
    ///
    /// <para>UNKNOWN KEYS ARE IGNORED, NOT FATAL. Published skills carry <c>argument-hint</c> and
    /// <c>allowed-tools</c>; refusing them would make every Claude Code skill unreadable. Ignoring
    /// <c>allowed-tools</c> is a decision with teeth and it is deliberate: it names another
    /// application's tools, and this app's own permission gate governs every call a skill provokes.</para>
    /// </summary>
    internal static SkillInfo? ParseSkill(
        string text, string path, string name, string directory, List<SkillProblem> problems)
    {
        var front = Frontmatter.Parse(text);
        if (!front.Present)
        {
            problems.Add(new SkillProblem(path, "no frontmatter: the file must start with ---"));
            return null;
        }

        if (front.Error is { } error)
        {
            problems.Add(new SkillProblem(path, error));
            return null;
        }

        var description = front.Fields.GetValueOrDefault("description");
        if (string.IsNullOrWhiteSpace(description))
        {
            // THE DESCRIPTION IS THE ENTIRE INTERFACE — it is the only thing the model sees before
            // deciding whether to load. A skill without one is invisible in practice, so it is
            // refused with a reason rather than listed as a nameless entry nothing will ever pick.
            problems.Add(new SkillProblem(path, "no description: the model has nothing to match on"));
            return null;
        }

        if (front.Fields.GetValueOrDefault("name") is { Length: > 0 } declaredName
            && !string.Equals(declaredName, name, StringComparison.Ordinal))
            problems.Add(new SkillProblem(path,
                $"frontmatter name '{declaredName}' does not match its folder — the folder wins"));

        if (front.Body.Length == 0)
        {
            problems.Add(new SkillProblem(path, "no body: nothing to load"));
            return null;
        }

        // THE BODY IS NOT CAPPED, unlike project instructions. That cap guards the SYSTEM PROMPT,
        // which every turn carries whether or not anyone wanted it. A body is paid for only when it
        // is loaded, and removable by compaction.
        return new SkillInfo(name, description, directory, front.Body);
    }
}
