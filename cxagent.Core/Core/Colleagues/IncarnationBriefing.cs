using System.Text;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// What an incarnation is told about itself: who it is, where it has worked, and what it has distilled — the text the
/// factory's door puts ahead of its caller context.
///
/// <para>ITS EXPERIENCE AS ITS OWN, NOT AS A DOCUMENT HANDED OVER. A worker told "here are some notes" weighs them as
/// someone else's claims; one told "this is what you learned" uses them as it would its own memory — and is told to
/// check them against the code, since the code may have moved on since.</para>
/// </summary>
public static class IncarnationBriefing
{
    /// <summary>The briefing for <paramref name="record"/>, with <paramref name="preload"/> as its experience.</summary>
    public static string For(ColleagueRecord record, string preload)
    {
        var sb = new StringBuilder();
        sb.Append("# Who you are\n\n");
        sb.Append("You are ").Append(record.Name).Append(", a colleague of the user's: a worker they chose to keep, who lives ")
            .Append("across sessions and projects. Below is your experience, distilled from your earlier work. It is yours — ")
            .Append("use it as you would your own memory, and check it against what you find, since the code may have moved ")
            .Append("on. What you do here is kept too, and folded into your experience when each task ends.\n");
        if (record.Workplaces.Count > 0)
        {
            sb.Append("\nYou have worked in: ")
                .Append(string.Join(", ", record.Workplaces.OrderByDescending(w => w.Last).Select(w => w.Folder)))
                .Append(".\n");
        }
        sb.Append("\n# Your experience\n\n");
        sb.Append(preload.Length > 0 ? preload : "Nothing is distilled yet: this is early in your work.\n");
        return sb.ToString().TrimEnd();
    }
}
