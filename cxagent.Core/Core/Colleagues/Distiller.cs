using System.Text;
using System.Text.RegularExpressions;
using CxAgent.Core.Llm;
using CxAgent.Core.Memory;
using CxAgent.Core.Models;

namespace CxAgent.Core.Colleagues;

/// <summary>What a drain came to.</summary>
public enum DistillOutcome
{
    /// <summary>Another distiller holds the lock; it drains everything before letting go.</summary>
    Busy,

    /// <summary>Every waiting round is distilled, or skipped as having nothing to learn.</summary>
    Done,

    /// <summary>The waiting rounds stay waiting — the previous experience is untouched. See <see cref="Distiller.Failed"/>.</summary>
    Failed,
}

/// <summary>One colleague to distil, and how to reach a model by provider entry.</summary>
/// <param name="Colleague">Its name.</param>
/// <param name="ProviderFor">The provider for an entry name — null asks for the colleague's own distilling entry — or
/// null when that entry no longer exists.</param>
public sealed record DistillRequest(string Colleague, Func<string?, ILlmProvider?> ProviderFor);

/// <summary>
/// Folds a colleague's waiting rounds into its experience: one call, given the current experience and the rounds,
/// answering the whole new text of each part it changed.
///
/// <para>REWRITTEN WHOLE, NOT PATCHED. A model asked to append grows the text forever; asked to rewrite under a cap, it
/// has to merge and drop, which is what keeps an experience small enough to start every session with.</para>
///
/// <para>NOTHING IS WRITTEN UNTIL EVERYTHING CHECKS: an answer that cannot be read, is over a cap after one request to
/// shorten, or looks like it holds a secret leaves the previous experience as it was and the rounds waiting — to be
/// folded in with the next round, which is how a wrong turn here is never a loss.</para>
/// </summary>
public sealed partial class Distiller(ColleagueStore store)
{
    /// <summary>A round shorter than this, with no tool calls, has nothing to learn from and costs no call.</summary>
    public const int TrivialChars = 300;

    /// <summary>How much of a tool's result the distiller sees — the whole of it stays in the past.</summary>
    public const int ToolResultLines = 20;

    /// <summary>
    /// The most round text one call is given. Rounds past it wait for the next call in the same drain; a single round
    /// longer than it is shown with its middle left out, since its brief and its answer say the most about it.
    /// </summary>
    public const int MaxRoundChars = 240_000;

    /// <summary>Raised with the colleague's name and why, when its rounds could not be distilled.</summary>
    public event Action<string, string>? Failed;

    /// <summary>Distils every waiting round of <paramref name="request"/>'s colleague, if no other distiller is at work.</summary>
    public async Task<DistillOutcome> DrainAsync(DistillRequest request, CancellationToken ct)
    {
        using var held = DistillingLock.TryTake(store.Root);
        if (held is null) return DistillOutcome.Busy;

        while (store.Undistilled(request.Colleague) is { Count: > 0 } waiting)
        {
            var batch = Batch(waiting);
            var why = await DistilAsync(request, batch, ct);
            if (why is not null)
            {
                Failed?.Invoke(request.Colleague, why);
                return DistillOutcome.Failed;
            }
        }
        return DistillOutcome.Done;
    }

    /// <summary>The waiting rounds one call can take, in order — at least one.</summary>
    private static List<PastRound> Batch(IReadOnlyList<PastRound> waiting)
    {
        var batch = new List<PastRound>();
        var size = 0;
        foreach (var round in waiting)
        {
            var length = Render(round).Length;
            if (batch.Count > 0 && size + length > MaxRoundChars) break;
            batch.Add(round);
            size += length;
        }
        return batch;
    }

    /// <summary>Distils one batch, and answers why it could not, or null when it did.</summary>
    private async Task<string?> DistilAsync(DistillRequest request, List<PastRound> batch, CancellationToken ct)
    {
        var name = request.Colleague;
        var last = batch[^1].Seq;

        // NOTHING TO LEARN, NO CALL: a greeting or a one-word answer moves the watermark and costs nothing.
        if (batch.All(Trivial))
        {
            store.Update(name, r => r with { DistilledThrough = Math.Max(r.DistilledThrough, last) });
            return null;
        }

        if (request.ProviderFor(null) is not { } model) return "its distilling model is not configured here";

        var experience = store.ReadExperience(name);
        var touched = Touched(batch);
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = Instructions },
            new() { Role = "user", Content = Ask(experience, touched, batch) },
        };

        Answer? answer;
        try
        {
            answer = await AskAsync(model, messages, touched, ct);
            if (answer is { Changed: true } && Oversized(answer) is { } over)
            {
                // ONCE, AND ONLY ONCE: a model that cannot shorten on the second ask will not on the third, and every ask
                // is paid for.
                messages.Add(new ChatMessage { Role = "assistant", Content = answer.Raw });
                messages.Add(new ChatMessage { Role = "user", Content = $"{over} Answer again in the same form, shorter: merge "
                    + "what overlaps and drop what is least useful." });
                answer = await AskAsync(model, messages, touched, ct);
                if (answer is { Changed: true } && Oversized(answer) is { } still) return still + " The previous experience is kept.";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"the distilling call failed: {e.Message}";
        }

        if (answer is null) return "the distiller's answer could not be read";

        if (!answer.Changed)
        {
            store.Update(name, r => r with { DistilledThrough = Math.Max(r.DistilledThrough, last) });
            return null;
        }

        // A SECRET NEVER REACHES DISK, the memory tool's rule: an experience travels to every session, model and project
        // the colleague is brought into.
        if (answer.Texts().Any(MemoryStore.LooksSecret)) return "the distilled experience looked like it held a secret; the previous one is kept";

        if (answer.Core is { } core) store.WriteExperience(name, core, null, null);
        foreach (var (key, text) in answer.Codebases) store.WriteExperience(name, null, key, text);

        var folders = batch.Select(r => r.Folder).Reverse().Distinct(StringComparer.Ordinal).ToList();
        store.Update(name, r => r with
        {
            DistilledThrough = Math.Max(r.DistilledThrough, last),
            ExperienceVersion = r.ExperienceVersion + 1,
            Description = answer.Description is { Length: > 0 } d ? d[..Math.Min(d.Length, 200)] : r.Description,
            GrownIn = [.. folders.Concat(r.GrownIn.Where(g => !folders.Contains(g))).Take(5)],
        });
        return null;
    }

    private static bool Trivial(PastRound round) =>
        round.Messages.All(m => m.ToolCalls is not { Count: > 0 } && m.ToolCallId is null)
        && round.Messages.Sum(m => m.Content.Length) < TrivialChars;

    /// <summary>The codebases a batch touched — each round's folder, and every other workplace it read — by key.</summary>
    private static Dictionary<string, string> Touched(List<PastRound> batch)
    {
        var touched = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in batch.SelectMany(r => r.AlsoRead.Prepend(r.Folder)))
            touched.TryAdd(Codebase.Key(folder), folder);
        return touched;
    }

    private static async Task<Answer?> AskAsync(ILlmProvider model, List<ChatMessage> messages,
        Dictionary<string, string> touched, CancellationToken ct)
    {
        var response = await model.ChatAsync([.. messages], tools: null, ct);
        return Answer.Parse(response.Text ?? "", touched);
    }

    private static string? Oversized(Answer answer)
    {
        if (answer.Core is { } core && ExperienceBudget.Tokens(core) > ExperienceBudget.CoreCap)
            return $"Your core is about {ExperienceBudget.Tokens(core)} tokens; it may be at most {ExperienceBudget.CoreCap}.";
        foreach (var (key, text) in answer.Codebases)
            if (ExperienceBudget.Tokens(text) > ExperienceBudget.CodebaseCap)
                return $"Your text for {key} is about {ExperienceBudget.Tokens(text)} tokens; it may be at most {ExperienceBudget.CodebaseCap}.";
        return null;
    }

    private const string Instructions =
        "You keep the experience of a colleague: an AI worker that lives across many sessions and projects. After each "
        + "round of its work you fold what it learned into its experience, which is what every new session of it starts "
        + "from.\n\n"
        + "Write plain, short statements a colleague would want to know next time: how it works, what it learned the hard "
        + "way, how each codebase is built and why. Not a diary of what happened; what is true and useful now. When a "
        + "round shows an earlier statement was wrong, correct it. Merge what overlaps; drop what is no longer useful. "
        + "Never write secrets — keys, passwords, tokens — even when the round shows one.\n\n"
        + "The core experience has these sections:\n## How you work\n## Lessons\n## Codebases\n"
        + "(Codebases is an index: one line per codebase it knows.) At most 8,000 tokens.\n\n"
        + "Each codebase's experience has these sections:\n## Architecture\n## Conventions\n## Pitfalls\n## Decisions\n"
        + "## Open threads\nAt most 24,000 tokens each.\n\n"
        // THE FORM IN WORDS, NOT A TEMPLATE WITH GAPS: a local model shown "<core>...</core>" answered "..." for the
        // core and the description, word for word.
        + "Answer with tagged parts, each rewritten whole. Put the complete new core between <core> and </core>. For "
        + "each codebase listed below whose experience changed, put its complete new text between <codebase key=\"KEY\"> "
        + "and </codebase>, with KEY exactly as listed. Put one line saying what this colleague is good at between "
        + "<description> and </description>. Leave out a part that did not change.\n\n"
        + "If the rounds teach nothing worth keeping, answer only: NO CHANGE";

    private static string Ask(Experience experience, Dictionary<string, string> touched, List<PastRound> batch)
    {
        var sb = new StringBuilder();
        sb.Append("# Current core experience\n\n").Append(experience.Core.Length > 0 ? experience.Core : "(none yet)").Append("\n\n");
        foreach (var (key, folder) in touched)
        {
            sb.Append("# Current experience of codebase ").Append(key).Append(" (").Append(folder).Append(")\n\n");
            sb.Append(experience.Codebases.TryGetValue(key, out var text) && text.Length > 0 ? text : "(none yet)").Append("\n\n");
        }
        sb.Append("# The rounds to fold in, oldest first\n\n");
        foreach (var round in batch) sb.Append(Render(round)).Append('\n');
        return sb.ToString();
    }

    /// <summary>One round as the distiller reads it: who said what, each tool result clipped to its first lines.</summary>
    private static string Render(PastRound round)
    {
        var sb = new StringBuilder();
        sb.Append("## Round ").Append(round.Seq).Append(" — in ").Append(round.Folder).Append(", ")
            .Append(round.Ended.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        foreach (var m in round.Messages)
        {
            if (m.Role == "system") continue;
            if (m.ToolCallId is not null)
            {
                sb.Append("[tool result]\n").Append(Clip(m.Content)).Append('\n');
                continue;
            }
            if (m.Content.Length > 0) sb.Append('[').Append(m.Role).Append("]\n").Append(m.Content).Append('\n');
            foreach (var call in m.ToolCalls ?? [])
                sb.Append("[tool call] ").Append(call.Name).Append(' ').Append(call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : call.Arguments.GetRawText()).Append('\n');
        }
        var text = sb.ToString();
        if (text.Length <= MaxRoundChars) return text;
        var half = MaxRoundChars / 2;
        return text[..half] + $"\n[… {text.Length - MaxRoundChars} characters of this round left out …]\n" + text[^half..];
    }

    private static string Clip(string result)
    {
        var lines = result.Split('\n');
        return lines.Length <= ToolResultLines
            ? result
            : string.Join('\n', lines.Take(ToolResultLines)) + $"\n[… {lines.Length - ToolResultLines} more lines]";
    }

    /// <summary>What the distiller answered: no change, or the parts it rewrote.</summary>
    private sealed record Answer(string Raw, bool Changed, string? Core, IReadOnlyDictionary<string, string> Codebases, string? Description)
    {
        public IEnumerable<string> Texts() => Codebases.Values.Prepend(Core ?? "").Append(Description ?? "");

        /// <summary>
        /// The answer's parts, or null when it has none. ONLY THE CODEBASES THE ROUNDS TOUCHED are taken: a key the model
        /// invented would become a file no workplace leads to.
        /// </summary>
        public static Answer? Parse(string text, Dictionary<string, string> touched)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("NO CHANGE", StringComparison.Ordinal))
                return new Answer(text, false, null, new Dictionary<string, string>(), null);

            // A PART WITH NO WORDS IN IT WAS NOT GIVEN — "..." or "…" in place of a text keeps the text that was there.
            var core = CorePattern().Match(trimmed) is { Success: true } c ? Given(c.Groups[1].Value) : null;
            var codebases = CodebasePattern().Matches(trimmed)
                .Where(m => touched.ContainsKey(m.Groups[1].Value) && Given(m.Groups[2].Value) is not null)
                .GroupBy(m => m.Groups[1].Value)
                .ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim(), StringComparer.Ordinal);
            var description = DescriptionPattern().Match(trimmed) is { Success: true } d ? Given(d.Groups[1].Value) : null;
            if (core is null && codebases.Count == 0 && description is null)
                return CorePattern().IsMatch(trimmed) || CodebasePattern().IsMatch(trimmed) || DescriptionPattern().IsMatch(trimmed)
                    ? new Answer(text, false, null, new Dictionary<string, string>(), null)
                    : null;
            return new Answer(text, true, core, codebases, description);
        }

        private static string? Given(string part)
        {
            var trimmed = part.Trim();
            return trimmed.Any(char.IsLetterOrDigit) ? trimmed : null;
        }
    }

    [GeneratedRegex(@"<core>\s*(.*?)\s*</core>", RegexOptions.Singleline)]
    private static partial Regex CorePattern();

    [GeneratedRegex(@"<codebase\s+key=""([^""]+)"">\s*(.*?)\s*</codebase>", RegexOptions.Singleline)]
    private static partial Regex CodebasePattern();

    [GeneratedRegex(@"<description>\s*(.*?)\s*</description>", RegexOptions.Singleline)]
    private static partial Regex DescriptionPattern();
}
