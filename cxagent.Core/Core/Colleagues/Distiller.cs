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

/// <summary>What one distilling changed in a colleague's experience — nothing, when every list is empty and Core false.</summary>
/// <param name="Colleague">Whose experience.</param>
/// <param name="Version">Its experience version after this distilling.</param>
/// <param name="Core">Whether its core was rewritten.</param>
/// <param name="Updated">Codebase keys whose experience was rewritten.</param>
/// <param name="Added">Codebase keys it had no experience of before.</param>
public sealed record DistillChange(string Colleague, long Version, bool Core, IReadOnlyList<string> Updated, IReadOnlyList<string> Added)
{
    /// <summary>True when nothing was kept — the distiller answered no change.</summary>
    public bool Nothing => !Core && Updated.Count == 0 && Added.Count == 0;

    /// <summary>
    /// The first and last rounds this distilling covered — so a session announces only the rounds it lived, and lets go
    /// of one distilled before, by another process or with nothing to learn.
    /// </summary>
    public long From { get; init; }

    /// <inheritdoc cref="From"/>
    public long Through { get; init; }
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

    /// <summary>Raised with a colleague's name as its waiting rounds start being distilled.</summary>
    public event Action<string>? Started;

    /// <summary>
    /// Raised after each distilling the model answered — what changed, or that nothing did. Not for rounds skipped as
    /// trivial with no call: a stray keystroke sent to a worker is no news.
    /// </summary>
    public event Action<DistillChange>? Distilled;

    /// <summary>Raised when a colleague's waiting rounds are all distilled — before the drain moves to the next one.</summary>
    public event Action<string>? Finished;

    /// <summary>
    /// Raised after every call to the distiller, with what it cost — the call to shorten included, and a call whose
    /// answer is then refused: it was paid for either way. The colleague's own total is already added to when this runs.
    /// </summary>
    public event Action<DistillSpend>? Spent;

    /// <summary>
    /// Distils every colleague's waiting rounds, if no other distiller is at work — Done when all are distilled,
    /// Failed when one or more could not be (each said through <see cref="Failed"/>), Busy when the lock is held.
    ///
    /// <para>EVERY COLLEAGUE, NOT ONLY THE ONE ASKING: a drain that finds the lock held does nothing, trusting the
    /// holder — so the holder must take what the others left, or a colleague never used again keeps a round nobody
    /// learns from. Looked at again before letting go, for rounds appended while it worked.</para>
    ///
    /// <para>A COLLEAGUE THAT FAILS IS LEFT FOR THIS DRAIN, and the rest go on: one model that is down, or one round
    /// refused, must not stop every other colleague learning.</para>
    /// </summary>
    /// <param name="modelsFor">For a colleague's name, how to reach a model by provider entry — see
    /// <see cref="DistillRequest.ProviderFor"/>.</param>
    /// <param name="ct">Stops the drain between calls.</param>
    public async Task<DistillOutcome> DrainAllAsync(Func<string, Func<string?, ILlmProvider?>> modelsFor, CancellationToken ct)
    {
        DistillingLock? held;
        try
        {
            held = DistillingLock.TryTake(store.Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke("the library", $"could not take the distilling lock: {e.Message}");
            return DistillOutcome.Failed;
        }
        if (held is null) return DistillOutcome.Busy;

        using (held)
        {
            // WHAT A FORGET COULD NOT DELETE goes now, under the same lock every forget takes.
            store.SweepForgotten();
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (Waiting(failed) is { } name)
            {
                var why = await DrainOneAsync(new DistillRequest(name, modelsFor(name)), ct);
                if (why is null)
                {
                    Finished?.Invoke(name);
                    continue;
                }
                failed.Add(name);
                Failed?.Invoke(name, why);
            }
            return failed.Count == 0 ? DistillOutcome.Done : DistillOutcome.Failed;
        }
    }

    /// <summary>A colleague with rounds still to distil, other than those that failed in this drain, or null.</summary>
    private string? Waiting(HashSet<string> failed) =>
        store.List().Select(r => r.Name).FirstOrDefault(n => !failed.Contains(n) && store.Undistilled(n).Count > 0);

    /// <summary>
    /// Distils one colleague's waiting rounds, batch by batch, and answers why it stopped short, or null.
    ///
    /// <para>NOTHING HERE THROWS OUT OF THE DRAIN: the store's own failures — a full disk, a folder gone — are a reason
    /// like the model's, said and left for the next drain.</para>
    /// </summary>
    private async Task<string?> DrainOneAsync(DistillRequest request, CancellationToken ct)
    {
        var name = request.Colleague;
        try
        {
            if (store.Undistilled(name).Count > 0) Started?.Invoke(name);
            while (store.Undistilled(name) is { Count: > 0 } waiting)
            {
                var batch = Batch(waiting);
                var failure = await DistilAsync(request, batch, ct);
                if (failure is null)
                {
                    if (store.Find(name) is { Failures: > 0 }) store.Update(name, r => r with { FailingRound = 0, Failures = 0 });
                    continue;
                }
                if (!failure.AboutTheRound || !StepPast(name, batch[0].Seq, failure.Why)) return failure.Why;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            return $"its library could not be written: {e.Message}";
        }
    }

    /// <summary>How many drains in a row may refuse one round before it is stepped past.</summary>
    public const int TriesBeforeSkipping = 3;

    /// <summary>
    /// Counts a refusal of the round at <paramref name="seq"/>, and steps past it at the third in a row — true when it did.
    ///
    /// <para>STEPPED PAST, NOT DROPPED (the user's choice, 2026-11-08): a round the distiller refuses every time — one
    /// holding a real secret, or a false "looks like a key" match — would otherwise hold back every round behind it,
    /// and the colleague would stop learning. Its words stay in the past; the library records it as not learned from,
    /// and the user is told once. ONLY FOR REFUSALS OF THE ROUND ITSELF: a model that is down skips nothing.</para>
    /// </summary>
    private bool StepPast(string name, long seq, string why)
    {
        var record = store.Update(name, r => r.FailingRound == seq
            ? r with { Failures = r.Failures + 1 }
            : r with { FailingRound = seq, Failures = 1 });
        if (record is null || record.Failures < TriesBeforeSkipping) return false;

        store.Update(name, r => r with
        {
            DistilledThrough = Math.Max(r.DistilledThrough, seq),
            FailingRound = 0,
            Failures = 0,
            Skipped = [.. r.Skipped, new SkippedRound(seq, why, DateTimeOffset.UtcNow)],
        });
        Failed?.Invoke(name, $"round {seq} was refused {TriesBeforeSkipping} times and is skipped — it stays in the past but "
                             + $"is not learned from ({why})");
        return true;
    }

    /// <summary>Why a batch was not distilled, and whether the round itself is the reason rather than the model or the disk.</summary>
    private sealed record Failure(string Why, bool AboutTheRound);

    /// <summary>The waiting rounds one call can take, in order — at least one.</summary>
    private static List<PastRound> Batch(IReadOnlyList<PastRound> waiting)
    {
        var batch = new List<PastRound>();
        var size = 0;
        foreach (var round in waiting)
        {
            var length = PastRounds.Render(round).Length;
            if (batch.Count > 0 && size + length > MaxRoundChars) break;
            batch.Add(round);
            size += length;
        }
        return batch;
    }

    /// <summary>Distils one batch, and answers why it could not, or null when it did.</summary>
    private async Task<Failure?> DistilAsync(DistillRequest request, List<PastRound> batch, CancellationToken ct)
    {
        var name = request.Colleague;
        var last = batch[^1].Seq;

        // NOTHING TO LEARN, NO CALL: a greeting or a one-word answer moves the watermark and costs nothing.
        if (batch.All(Trivial))
        {
            store.Update(name, r => r with { DistilledThrough = Math.Max(r.DistilledThrough, last) });
            return null;
        }

        if (request.ProviderFor(null) is not { } model) return new Failure("its distilling model is not configured here", false);

        var experience = store.ReadExperience(name);
        var touched = Touched(batch);
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = Instructions },
            new() { Role = "user", Content = Ask(experience, touched, batch) },
        };

        var call = new DistillCall(name, model, touched);
        Answer? answer;
        try
        {
            answer = await AskAsync(call, messages, ct);
            if (answer is { Changed: true } && Oversized(answer) is { } over)
            {
                // ONCE, AND ONLY ONCE: a model that cannot shorten on the second ask will not on the third, and every ask
                // is paid for.
                messages.Add(new ChatMessage { Role = "assistant", Content = answer.Raw });
                messages.Add(new ChatMessage { Role = "user", Content = $"{over} Answer again in the same form, shorter: merge "
                    + "what overlaps and drop what is least useful." });
                answer = await AskAsync(call, messages, ct);
                if (answer is { Changed: true } && Oversized(answer) is { } still) return new Failure(still + " The previous experience is kept.", true);
                // "NO CHANGE" TO A REQUEST TO SHORTEN is no answer to it: taking it would throw away what the first answer
                // learned and count the rounds as distilled.
                if (answer is { Changed: false }) return new Failure("asked to shorten, the distiller answered no change; the rounds wait", true);
            }
        }
        catch (CutOffException)
        {
            return new Failure("the distiller's answer was cut off before it ended; nothing of it is kept", true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Failure($"the distilling call failed: {e.Message}", false);
        }

        if (answer is null) return new Failure("the distiller's answer could not be read", true);

        if (!answer.Changed)
        {
            var kept = store.Update(name, r => r with { DistilledThrough = Math.Max(r.DistilledThrough, last) });
            Distilled?.Invoke(new DistillChange(name, kept?.ExperienceVersion ?? 0, false, [], []) { From = batch[0].Seq, Through = last });
            return null;
        }

        // A SECRET NEVER REACHES DISK, the memory tool's rule: an experience travels to every session, model and project
        // the colleague is brought into.
        if (answer.Texts().Any(MemoryStore.LooksSecret))
            return new Failure("the distilled experience looked like it held a secret; the previous one is kept", true);

        if (answer.Core is { } core) store.WriteCore(name, core);
        foreach (var (key, text) in answer.Codebases) store.WriteCodebase(name, key, text);

        var folders = batch.Select(r => r.Folder).Reverse().Distinct(StringComparer.Ordinal).ToList();
        var updated = store.Update(name, r => r with
        {
            DistilledThrough = Math.Max(r.DistilledThrough, last),
            ExperienceVersion = r.ExperienceVersion + 1,
            Description = answer.Description is { Length: > 0 } d ? OneLine(d) : r.Description,
            GrownIn = [.. folders.Concat(r.GrownIn.Where(g => !folders.Contains(g))).Take(5)],
        });
        Distilled?.Invoke(new DistillChange(name, updated?.ExperienceVersion ?? 0, answer.Core is not null,
            [.. answer.Codebases.Keys.Where(experience.Codebases.ContainsKey)],
            [.. answer.Codebases.Keys.Where(k => !experience.Codebases.ContainsKey(k))]) { From = batch[0].Seq, Through = last });
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

    /// <summary>
    /// One call to the distiller. AN ANSWER CUT OFF AT ITS LENGTH LIMIT IS REFUSED WHOLE: its last part is missing its
    /// closing tag and would be dropped while the parts before it were written, and the rounds counted as learned from.
    /// </summary>
    private async Task<Answer?> AskAsync(DistillCall call, List<ChatMessage> messages, CancellationToken ct)
    {
        var (name, model, touched) = (call.Colleague, call.Model, call.Touched);
        var started = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var response = await model.ChatAsync([.. messages], tools: null, ct);

        // COUNTED BEFORE THE ANSWER IS JUDGED: a cut-off answer is refused whole, and is the costliest call there is.
        Count(new DistillSpend(name, model.ModelId, response.Usage.InputTokens, response.Usage.OutputTokens, started, clock.ElapsedMilliseconds));

        if (response.StopReason is "max_tokens" or "length") throw new CutOffException();
        return Answer.Parse(response.Text ?? "", touched);
    }

    /// <summary>
    /// Adds one call to the colleague's total and says so. A RECORD THAT CANNOT BE WRITTEN LOSES THE COUNT, NOT THE
    /// DISTILLING: the spend is a figure beside the experience, and the answer already paid for is still worth keeping.
    /// </summary>
    private void Count(DistillSpend call)
    {
        try
        {
            store.Update(call.Colleague, r => r with { Spend = r.Spend.Add(call) });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        Spent?.Invoke(call);
    }

    /// <summary>Who a call distils for, the model asked, and the codebases its answer may write.</summary>
    private sealed record DistillCall(string Colleague, ILlmProvider Model, Dictionary<string, string> Touched);

    private sealed class CutOffException : Exception;

    /// <summary>A description is one line in the prompt's list: whitespace collapsed, at most 200 characters.</summary>
    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line[..Math.Min(line.Length, 200)];
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

    /// <summary>Test seam: what the distiller is told.</summary>
    internal static string InstructionsForTest => Instructions;

    private const string Instructions =
        "You keep the experience of a colleague: an AI worker that lives across many sessions and projects. After each "
        + "round of its work you fold what it learned into its experience, which is what every new session of it starts "
        + "from.\n\n"
        + "Write plain, short statements a colleague would want to know next time: how it works, what it learned the hard "
        + "way, how each codebase is built and why. Not a diary of what happened; what is true and useful now. Merge "
        + "what overlaps; drop what is no longer useful. Never write secrets — keys, passwords, tokens — even when the "
        + "round shows one.\n\n"
        // THE PRINCIPLES IN GENERAL WORDS, as the user approved them (2026-11-08, the kinds 2026-11-09): a rule tailored to
        // a bug seen in one language or tool teaches yesterday's mistake and carries nowhere else. THREE KINDS, NOT TWO:
        // "concluded" once covered every inference, and an inference written as a finding is how a guess became knowledge;
        // only what a round set out to check and established is concluded now.
        + "Say how each statement is known, ending it with its kind and the latest round that bears on it: (observed, "
        + "round N) for what a round read, ran or got back, as it came back; (learned, round N) for what was understood or "
        + "inferred from that, or was told by whoever asked; (concluded, round N) for what a round set out to check and "
        + "established. None on the Codebases index. Write what was observed and what was understood from it as separate "
        + "statements. A statement rises only when a round gives it new evidence of that kind — recalling it, repeating "
        + "it, or being told it again never raises it. That something does not exist, does not work or cannot be done is "
        + "at most learned until a round establishes it. A statement with no kind was learned before kinds were kept; an "
        + "older \"seen\" mark means observed, an older \"concluded\" means learned.\n\n"
        + "Newer evidence wins. When a statement no longer holds, move it: under ## Mistaken if it was never true — what "
        + "was believed, what is true, the round; under ## Superseded if it held and stopped holding — what held, what "
        + "holds now, from when, the round. Keep only the few that still matter.\n\n"
        + "File each statement where it is true: about a codebase in that codebase's experience; what holds everywhere in "
        + "the core. You can write only the core and the codebases listed below; what belongs to another waits until it "
        + "is listed. A round names the folders it read outside every codebase listed — keep what was learned there in "
        + "the core only if it holds generally.\n\n"
        + "The core experience has these sections:\n## How you work\n## Lessons\n## Mistaken\n## Superseded\n## Codebases\n"
        + "(Codebases is an index: one line per codebase it knows.) At most 8,000 tokens.\n\n"
        + "Each codebase's experience has these sections:\n## Architecture\n## Conventions\n## Pitfalls\n## Decisions\n"
        + "## Open threads\n## Mistaken\n## Superseded\nAt most 24,000 tokens each.\n\n"
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
        foreach (var round in batch) sb.Append(PastRounds.Render(round)).Append('\n');
        return sb.ToString();
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
            // REASONING IS SET ASIDE, and the answer is the LAST of each part: a local model writes its thinking into the
            // text, and thinking about the answer names its tags — "put the core between <core> and </core>" — so the
            // first <core> can be inside it. Taken first, the reasoning was written as the colleague's experience.
            var trimmed = ThinkBlock().Replace(text, "").Trim();
            if (trimmed.EndsWith("NO CHANGE", StringComparison.Ordinal) || trimmed.StartsWith("NO CHANGE", StringComparison.Ordinal))
                return new Answer(text, false, null, new Dictionary<string, string>(), null);

            // A PART WITH NO WORDS IN IT WAS NOT GIVEN — "..." or "…" in place of a text keeps the text that was there.
            var core = CorePattern().Matches(trimmed).LastOrDefault() is { } c ? Given(c.Groups[1].Value) : null;
            var codebases = CodebasePattern().Matches(trimmed)
                .Where(m => touched.ContainsKey(m.Groups[1].Value) && Given(m.Groups[2].Value) is not null)
                .GroupBy(m => m.Groups[1].Value)
                .ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim(), StringComparer.Ordinal);
            var description = DescriptionPattern().Matches(trimmed).LastOrDefault() is { } d ? Given(d.Groups[1].Value) : null;
            // TAGS WITH NOTHING USABLE IN THEM — placeholders, a key the model invented — are not "no change": the model
            // meant to answer and did not, so the rounds wait rather than count as learned from.
            if (core is not null || codebases.Count > 0 || description is not null) return new Answer(text, true, core, codebases, description);

            // A PART ONLY FOR A CODEBASE THE ROUNDS DID NOT LIST is no change, not an unreadable answer: the model was told
            // it can write only what is listed, and a refusal counted three times would step the round past for it. NOT when
            // the answer also left a placeholder where a part should be, and NOT when the key names a listed codebase
            // misspelt — by its name or its folder's — which is a mistake to retry: dropped, what it learned would be lost.
            if (CorePattern().IsMatch(trimmed) || DescriptionPattern().IsMatch(trimmed)) return null;
            var listed = touched.Keys.Select(KeyName).Concat(touched.Values.Select(f => Path.GetFileName(Path.TrimEndingDirectorySeparator(f))))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parts = CodebasePattern().Matches(trimmed).Where(m => Given(m.Groups[2].Value) is not null).ToList();
            return parts.Count > 0 && parts.All(m => !listed.Contains(KeyName(m.Groups[1].Value)))
                ? new Answer(text, false, null, new Dictionary<string, string>(), null)
                : null;
        }

        /// <summary>A key's name without its hash — <c>invoice</c> for <c>invoice-7c114824</c> — as a model may write it.</summary>
        private static string KeyName(string key) => Regex.Replace(key.Trim(), "-[0-9a-f]{8}$", "");

        private static string? Given(string part)
        {
            var trimmed = part.Trim();
            return trimmed.Any(char.IsLetterOrDigit) ? trimmed : null;
        }
    }

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    // EACH PART HOLDS NO OPENING TAG OF ITS OWN KIND: prose that mentions "<core>" without closing it must not swallow
    // everything up to the real part's end.
    [GeneratedRegex(@"<core>\s*((?:(?!<core>).)*?)\s*</core>", RegexOptions.Singleline)]
    private static partial Regex CorePattern();

    [GeneratedRegex(@"<codebase\s+key=""([^""]+)"">\s*((?:(?!<codebase).)*?)\s*</codebase>", RegexOptions.Singleline)]
    private static partial Regex CodebasePattern();

    [GeneratedRegex(@"<description>\s*((?:(?!<description>).)*?)\s*</description>", RegexOptions.Singleline)]
    private static partial Regex DescriptionPattern();
}
