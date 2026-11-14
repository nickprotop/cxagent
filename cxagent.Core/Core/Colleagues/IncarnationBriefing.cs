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
    /// <param name="record">Who it is.</param>
    /// <param name="preload">The experience it starts with.</param>
    /// <param name="setting">What it may reach here — see <see cref="IncarnationSetting"/>.</param>
    public static string For(ColleagueRecord record, string preload, IncarnationSetting setting)
    {
        var sb = new StringBuilder();
        sb.Append("# Who you are\n\n");
        sb.Append("You are ").Append(record.Name).Append(", a colleague of the user's: a worker they chose to keep, who lives ")
            .Append("across sessions and projects. Below is your experience, distilled from your earlier work. It is yours — ")
            .Append("use it as you would your own memory, and check it against what you find, since the code may have moved ")
            .Append("on. What you do here is kept too, and folded into your experience when each task ends.\n");
        // HOW TO WEIGH EACH KIND, as the user approved it (2026-11-09) — the distiller writes them, the colleague reads them.
        sb.Append("\nEach statement says how it is known: concluded — established by a check; observed — seen as it came ")
            .Append("back; learned — understood or inferred, or told. Rely on concluded, read observed as what was seen at the ")
            .Append("time, and check learned before you build on it. Mistaken holds what you once believed wrongly; Superseded ")
            .Append("holds what used to be true — mind which version you are working with. Your past and the current version ")
            .Append("of your experience are a recall away — including the round behind any statement. A statement with no ")
            .Append("kind was learned before kinds were kept: weigh it as learned.\n");
        if (record.Workplaces.Count > 0)
        {
            sb.Append("\nYou have worked in: ")
                .Append(string.Join(", ", record.Workplaces.OrderByDescending(w => w.Last).Select(w => w.Folder)))
                .Append(".\n");
            // SAID AS IT IS, WITH ITS LIMITS: a read that then asks would otherwise read as a broken promise.
            if (setting.ReadsWorkplaces)
                sb.Append("You can read files in them without asking, unless a folder is no longer trusted or this session is ")
                    .Append("shared to a browser that cannot see it.\n");
            if (setting.GoneWorkplaces.Count > 0)
                sb.Append("Some are gone since you worked there — moved or deleted, so not readable: ")
                    .Append(string.Join(", ", setting.GoneWorkplaces)).Append(".\n");
        }

        // A TALK IS WITH THE USER: no lead reads its answers, so what a worker is told about reporting to one does not hold.
        if (setting.TalkingWithUser)
            sb.Append("\nHere you are talking with the user directly, in a conversation that goes on: answer them, not an agent ")
                .Append("that set you to work. Where your instructions speak of reporting to a parent, that does not apply to this ")
                .Append("conversation.\n");

        // AN ASK HAS SOMEONE WAITING, and is told so, as a Talk worker is told its situation: told only to check what it
        // knows, an Ask worker re-established its experience on every ask — minutes of tool calls for one question, until
        // the lead stopped it and did the work itself.
        //
        // AND NEW GROUND IS THE ASKER'S TO SEND IT TO (user, 2026-11-14). A question with one part beyond its experience
        // turned into minutes of tracing that part before any answer — the parts it knew included — and the lead stopped
        // it with nothing. Answered first, the open part named with what would settle it, the asker decides.
        if (setting.AnsweringAsks)
            sb.Append("\nHere you answer asks: each comes from an agent that waits on your answer. Answer what was asked from ")
                .Append("what you know, and check only what your answer rests on. Where part of a question reaches beyond what ")
                .Append("you know, do not go and find out: answer the rest, say what is still open and what you would check to ")
                .Append("settle it, and leave it to the asker whether to send you to find out.\n");

        sb.Append("\n# Your experience\n\n");
        sb.Append(preload.Length > 0 ? preload : "Nothing is distilled yet: this is early in your work.\n");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>What an incarnation may reach where it is brought in, as its briefing says it.</summary>
/// <param name="ReadsWorkplaces">Its workplaces' files are readable without asking here — an incarnation's, not a
/// one-to-one's, where the user is present to answer.</param>
/// <param name="GoneWorkplaces">Workplaces whose folders are no longer there.</param>
public sealed record IncarnationSetting(bool ReadsWorkplaces, IReadOnlyList<string> GoneWorkplaces)
{
    /// <summary>It is the colleague's Talk worker: talking with the user directly, in a conversation that goes on.</summary>
    public bool TalkingWithUser { get; init; }

    /// <summary>It is the colleague's Ask worker: each run answers an ask whose sender waits on it.</summary>
    public bool AnsweringAsks { get; init; }
}
