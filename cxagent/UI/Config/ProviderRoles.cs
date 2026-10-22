using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>
/// The Roles block's two choices: which instance new sessions start on, and which one reviews actions in
/// auto mode. Both shown at once, each a list to pick from, and the reviewer's "none" a value like any
/// other — it is what turns auto mode off, so it must be something a person can see and choose.
/// </summary>
public static class ProviderRoles
{
    /// <summary>The reviewer choice that clears <c>classifier</c>, saying what that does.</summary>
    public const string NoReviewer = "none — auto mode off";

    /// <summary>The instances, in the rail's order.</summary>
    public static IReadOnlyList<string> DefaultChoices(ProviderSettings s) =>
        [.. s.Providers.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    /// <summary>The default's place in <see cref="DefaultChoices"/>, or −1 with none set.</summary>
    public static int DefaultIndex(ProviderSettings s) =>
        DefaultChoices(s).ToList().IndexOf(s.DefaultProvider ?? "");

    /// <summary><see cref="NoReviewer"/>, then the instances.</summary>
    public static IReadOnlyList<string> ReviewerChoices(ProviderSettings s) => [NoReviewer, .. DefaultChoices(s)];

    /// <summary>
    /// The reviewer's place in <see cref="ReviewerChoices"/>; none when unset, and none when it names an
    /// instance that is gone — which is what such a config does, since auto mode then has no reviewer.
    /// </summary>
    public static int ReviewerIndex(ProviderSettings s) =>
        s.Classifier is { } c && DefaultChoices(s).ToList().IndexOf(c) is var i and >= 0 ? i + 1 : 0;

    /// <summary>The instance a reviewer choice names, or null for <see cref="NoReviewer"/>.</summary>
    public static string? ReviewerFrom(string choice) => choice == NoReviewer ? null : choice;
}
