using System;
using System.IO;
using System.Linq;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

public class CoreMarkdownTests
{
    [Fact]
    public void APlainStringIsAnInfoMessage()
    {
        // THE IMPLICIT CONVERSION IS WHAT KEEPS THE ORDINARY CASE SHORT. Most of Core's lines are
        // neutral, and requiring `new Message(text)` at every one of them would make the common case
        // the noisy one — which is how a severity parameter ends up ignored.
        Message m = "switched to local:qwen";

        Assert.Equal("switched to local:qwen", m.Text);
        Assert.Equal(Severity.Info, m.Severity);
    }

    [Fact]
    public void ToneIsStatedWhenItMatters()
    {
        var m = new Message("could not save this rule", Severity.Warning);

        Assert.Equal(Severity.Warning, m.Severity);
    }

    [Theory]
    [InlineData("my_test_file.cs", @"my\_test\_file.cs")]
    [InlineData("a*b", @"a\*b")]
    [InlineData("`code`", @"\`code\`")]
    [InlineData("[link]", @"\[link\]")]
    public void InterpolatedValuesKeepTheirCharacters(string raw, string escaped)
    {
        // A PATH IS NOT EMPHASIS. `my_test_file.cs` interpolated raw into markdown renders as
        // my<i>test</i>file.cs — the same class of bug the old Markup.Escape existed to prevent, in
        // the new format. Core interpolates paths, error text and model output constantly.
        Assert.Equal(escaped, Md.Escape(raw));
    }

    [Fact]
    public void OrdinaryTextIsUntouched()
    {
        Assert.Equal("could not read the file", Md.Escape("could not read the file"));
    }

    [Fact]
    public void ErrorsIsTheMessagesAtErrorSeverity()
    {
        // WAS A PARALLEL CHANNEL, NOW A FILTER. `Failed` routed into its own list, which is why it
        // survived as a second method — but that list has 29 references and every one is a test
        // using it as an assertion handle. Derived from severity, it answers the same question
        // without the contract carrying a method to ask it.
        var sink = new BufferedChatSink();

        sink.Said("a mode change");
        sink.Said(new("could not save this rule", Severity.Warning));
        sink.Said(new("the model returned no answer", Severity.Error));

        Assert.Single(sink.Errors);
        Assert.Contains("no answer", sink.Errors[0]);

        // A WARNING IS NOT A FAULT. A caller checking for failures must not find one here — the same
        // distinction Notices and Errors were kept apart for.
        Assert.DoesNotContain(sink.Errors, e => e.Contains("could not save"));
    }

    [Fact]
    public void ACommandRefusalIsAWarning()
    {
        // THE 22 COLOURED REPLIES LIVED HERE, not on the sink. A command returns its text and the
        // session says it; giving severity only to the sink would have left every actual warning in
        // Core unable to say it was one.
        var result = ModelCommand.Decide("no-such-provider",
            ProviderRegistry.FromProviders(
                new Dictionary<string, ILlmProvider> { ["local"] = new MockLlmProvider("qwen3") },
                "local", new Dictionary<string, int?> { ["local"] = 1000 }),
            "local");

        Assert.Equal(Severity.Warning, result.Reply.Severity);
        Assert.DoesNotContain("[yellow]", result.Reply.Text);
    }

    [Fact]
    public void NoCoreFileNamesAColour()
    {
        // THE REGRESSION THIS EXISTS FOR is one message at a time: someone adds a line, reaches for
        // the nearest example, and the example is still coloured. A grep in a test is the only thing
        // that notices, because a stray tag renders as literal text a reader shrugs at.
        //
        // ROOTED AT cxagent.Core/Core, NOT cxagent.Core. The examples under cxagent.Core/examples
        // (SpectreAgent, ReadOnlyAgent, ToolAgent) still write real markup on purpose — they render
        // through SharpConsoleUI/Spectre, which is the one place a colour tag is still the right
        // format — and a later task owns converting them. Rooting the sweep at the library itself
        // would fail on files this task has no reason to touch and no way to fix without doing that
        // task's work first.
        //
        // DOC-COMMENT LINES ARE SKIPPED, not scanned. Two files quote a colour tag on purpose inside
        // `///` prose: ISessionObserver.cs (Ruling 6 — "a model writing \"[red]\" as ordinary prose
        // must not open a style scope", the documentation of this very feature) and Message.cs
        // (Severity's own doc, quoting what Core "used to write" before this change). Both are
        // examples ABOUT markup, in a sentence, never rendered; every real offender this task fixed
        // was a live `Say("[yellow]...[/]")` call outside a `///` line. Filtering by line prefix
        // catches that distinction without hard-coding either filename — a THIRD file doing the same
        // thing later would be exempted for the same reason, not because it dodged a filename list.
        var core = Path.Combine(RepoRoot(), "cxagent.Core", "Core");
        var tagPattern = new System.Text.RegularExpressions.Regex(@"\[(red|yellow|green|grey\d*|cyan\d*)\]");
        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllLines(f)
                .Any(line => !line.TrimStart().StartsWith("///") && tagPattern.IsMatch(line)))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        // RULING 10 — A SHRINKING ALLOWLIST, NOT Assert.Empty, and DELIBERATELY TEMPORARY: these
        // three still hold real colour tags because Markup.cs stays alive until its last consumer
        // is gone (Task 6 takes Skills/Agents, Task 7 takes Diff and the stats pair — Ruling 8).
        // Assert.Empty here would have to be skipped or ignored until Task 7 lands, and a guard test
        // nobody trusts guards nothing. Asserting the exact set keeps the suite green today AND still
        // fails the moment a FOURTH file grows a tag, or one of these three loses its tag without
        // this list being updated — both are real regressions this construction catches.
        //
        // AS EACH TASK LANDS, DELETE ITS FILE FROM THIS LIST. The last deletion turns this back into
        // Assert.Empty(offenders) and deletes Markup.cs alongside it — see Markup.cs's own doc
        // comment, which names the same ten call sites from the other direction.
        string[] stillOwnedByLaterTasks = ["DiffCommand.cs"];
        Assert.Equal(stillOwnedByLaterTasks.OrderBy(f => f, StringComparer.Ordinal).ToArray(), offenders);
    }

    [Fact]
    public void FailureVocabularyIsNeverInfo()
    {
        // THE DEFAULT IS WHAT MAKES THIS POSSIBLE. `Say("could not save…")` compiles and arrives as
        // Info, so a forgotten severity silently turns a warning into an aside — the one thing the
        // old two-method split could not get wrong. The default stays because ordinary lines are the
        // common case; this test is the price of keeping it.
        //
        // Say(, NOT Said(. The observer method is Said(Message message); Core's own call sites never
        // call that directly — they go through Session.Say, which forwards to the sink. A pattern
        // written against "Said(" would match nothing under cxagent.Core/Core and pass vacuously,
        // guarding nothing.
        //
        // RULING 12 — TWO SHAPES, ONE BUG, CHECKED PER-CALL RATHER THAN BY A SINGLE ANCHORED REGEX.
        // This test originally required the failure word to sit immediately after `Say(` — which
        // matched the bare `Say($"could not…")` idiom this task REMOVED from Core, but could not see
        // `Say(new Message($"could not…"))`, the shape this task INTRODUCED and the one every later
        // task now writes. Message's severity parameter defaults to Info, so a forgotten second
        // argument compiles clean and reads as an aside — exactly the bug this test exists to catch,
        // now in the one shape the old regex was blind to.
        //
        // EACH `Say(...)` CALL IS EXTRACTED WHOLE (non-greedy up to its closing `);`) rather than
        // matched by one monolithic pattern, because "does this call mention a failure word AND omit
        // Severity." is a property of the whole call, not of a fixed-position prefix — the severity
        // argument can be many characters, and lines, after the failure wording once the message is
        // built with `new Message(...)`. WHOLE-LINE `//` COMMENTS ARE STRIPPED FIRST: a multi-line
        // `Say(new Commands.SkillsCommand(...))` call in Session.Commands.cs carries an explanatory
        // comment between its arguments that happens to contain the word "cannot" as ordinary prose
        // — without stripping it, extracting the call's full text flags a call that never says
        // anything is a failure.
        var core = Path.Combine(RepoRoot(), "cxagent.Core", "Core");
        var commentLine = new System.Text.RegularExpressions.Regex(@"^\s*//.*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var callPattern = new System.Text.RegularExpressions.Regex(@"Say\(.*?\);",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var failureWord = new System.Text.RegularExpressions.Regex(
            @"(could not|failed|refused|unavailable|cannot)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var severityWord = new System.Text.RegularExpressions.Regex(@"Severity\.");

        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var stripped = commentLine.Replace(File.ReadAllText(f), "");
                return callPattern.Matches(stripped)
                    .Any(m => failureWord.IsMatch(m.Value) && !severityWord.IsMatch(m.Value));
            })
            .Select(Path.GetFileName)
            .ToList();

        // A Say(...) call carrying failure wording with no Severity argument is the failure: the
        // severity has to be stated for these, whichever of the two shapes above it is written in.
        Assert.Empty(offenders);
    }

    [Fact]
    public void ASessionListIsAMarkdownTable()
    {
        // ASSERTED ON THE MARKDOWN, NOT ON SPACING. The whole point is that Core stops padding
        // columns: a front end lays the table out, and a consumer rendering to HTML gets a real
        // <table> rather than a monospace assumption that only holds in a terminal.
        //
        // Decide, NOT Handle: the brief's sketch named a method this type does not have. Decide is
        // the real entry point — see SessionsCommand.Decide and every existing SessionsCommandTests
        // call site.
        var sessions = new List<CxAgent.Core.Storage.SessionInfo>
        {
            new(Uid: "ABCDEF0123456789", Title: "a title", WorkingDir: "/w",
                InputTokens: 100, OutputTokens: 50, Finished: false,
                UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
        };

        var reply = SessionsCommand.Decide("", sessions, TimeSpan.FromDays(7)).Reply;

        Assert.StartsWith("## Sessions", reply.Text);
        Assert.Contains("|", reply.Text);
        Assert.DoesNotContain("[grey", reply.Text);
    }

    [Fact]
    public void ATitleContainingAPipeDoesNotSplitTheTableRow()
    {
        // RULING 16 — A CONTAINS-ASSERTION WOULD PASS ON A BROKEN ROW. `Md.Escape`'s Special set
        // is `\`*_[]` — no pipe — because its contract is a markdown SENTENCE, where a pipe is
        // ordinary punctuation (`/sessions resume <number|id>` must keep its pipe). A table CELL is
        // the opposite: the pipe IS the column delimiter, so an unescaped one in a session title
        // splits the row into more cells than the header declares and Markdig drops the overflow.
        // Asserting cell count, not "Contains", is the only way to catch that — a contains-assertion
        // is exactly how this got through Task 6 in the first place.
        var sessions = new List<CxAgent.Core.Storage.SessionInfo>
        {
            new(Uid: "ABCDEF0123456789", Title: "fix a|b parser", WorkingDir: "/w",
                InputTokens: 100, OutputTokens: 50, Finished: false,
                UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
        };

        var reply = SessionsCommand.Decide("", sessions, TimeSpan.FromDays(7)).Reply;

        var headerLine = reply.Text.Split('\n').First(l => l.StartsWith("| #"));
        var rowLine = reply.Text.Split('\n').First(l => l.Contains("fix a"));

        // COUNT UNESCAPED PIPES ONLY. A raw `|` character count does not change once escaped — `\|`
        // still contains the character '|', just preceded by a backslash a markdown renderer treats
        // as "not a delimiter". The bug this test exists to catch is about DELIMITERS, so an escaped
        // pipe must not be counted as one.
        static int Delimiters(string line)
        {
            var count = 0;
            for (var i = 0; i < line.Length; i++)
                if (line[i] == '|' && (i == 0 || line[i - 1] != '\\'))
                    count++;
            return count;
        }

        Assert.Equal(Delimiters(headerLine), Delimiters(rowLine));
    }

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "cxagent.Core")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new DirectoryNotFoundException("repository root not found from " + AppContext.BaseDirectory);
    }
}
