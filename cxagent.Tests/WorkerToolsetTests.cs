using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class WorkerToolsetTests
{
    private static ToolCall Call(string name, object args) =>
        new() { Name = name, Arguments = JsonSerializer.SerializeToElement(args), Id = "call-1" };

    [Fact]
    public void For_GeneratesSchemasFromTheLIVEPluginSchema()
    {
        // Never hand-write the tool schema. FileJobPlugin requires "action", and a hand-written example
        // once said "operation" — the model followed it faithfully and every file job failed validation.
        var tools = WorkerToolset.For(new[] { WorkerTool.WriteFile }, PluginRegistry.CreateWithBuiltins());

        // Assert on the PARSED structure, not a substring of the serialized blob: "action" would match
        // a description reading "…the file action…", so a substring test passes on a hand-written
        // schema that merely mentions the right words.
        var props = tools.Single().InputSchema.GetProperty("properties");

        // `action` is pinned by the tool NAME, so it must NOT be offered to the model.
        var real = PluginRegistry.CreateWithBuiltins().All
            .Single(p => p.TypeName == "file").GetSchema().Params.Select(p => p.Name).ToList();

        Assert.Contains("action", real);            // guards the premise: if the plugin drops `action`,
                                                    // the pinning is silently wrong.
        Assert.False(props.TryGetProperty("action", out _));

        // Every param this tool DOES offer must be one the plugin really accepts. The rule used to be
        // "offer all of them", which is why read_file advertised content, dest and replacement; a
        // tool now SELECTS from the live schema. Selecting is allowed, inventing is not —
        // BuildDefinition throws on a name the plugin does not accept, and this pins the direction
        // that matters: nothing reaches the model that the plugin would reject.
        foreach (var offered in props.EnumerateObject())
            Assert.Contains(offered.Name, real);

        // write_file's own params, which the model must fill in.
        Assert.True(props.TryGetProperty("path", out _));
        Assert.True(props.TryGetProperty("content", out _));
    }

    [Fact]
    public void For_OffersOnlyTheAllowedTools()
    {
        var tools = WorkerToolset.For(new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins());
        Assert.Single(tools);
        Assert.DoesNotContain(tools, t => t.Name.Contains("write", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void For_PinnedParamsAreExcludedFromRequiredToo()
    {
        // `action` is Required:true in FileJobPlugin's schema AND pinned by the tool name, so it must
        // be absent from BOTH `properties` and `required`. Listing it as required while never offering
        // it emits a schema demanding a param the model cannot supply. That holds today only because
        // the pinned-param `continue` precedes the required-add — true by construction, not by test,
        // until now.
        var schema = WorkerToolset.For(new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins())
            .Single().InputSchema;

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.DoesNotContain("action", required);
        Assert.Contains("path", required);   // the guard: a real required param IS still listed
    }

    [Fact]
    public void NamesFor_MatchesTheToolsActuallyOffered()
    {
        // The orchestrator is TOLD these names (CreatePlanTool advertises them per role); a worker is
        // OFFERED For()'s definitions. If the two ever diverge the failure is silent — the orchestrator
        // plans against a tool name the worker cannot call. Assert they are the same strings, in the
        // same order, for the full set.
        var all = new[] { WorkerTool.ReadFile, WorkerTool.WriteFile, WorkerTool.RunShell, WorkerTool.HttpRequest };

        Assert.Equal(
            WorkerToolset.For(all, PluginRegistry.CreateWithBuiltins()).Select(t => t.Name),
            WorkerToolset.NamesFor(all));
    }

    [Fact]
    public void For_NoTools_ReturnsEmpty_NotNull()
    {
        // Empty rather than null so the CALL SITE needs no null check. Note the two are identical on
        // the wire — both OpenAiWire.cs:53 and AnthropicWire.cs:64 gate on `Count > 0`, so an empty
        // list emits no `tools` key at all. This is an API-ergonomics guarantee, not a protocol one.
        Assert.Empty(WorkerToolset.For(Array.Empty<WorkerTool>(), PluginRegistry.CreateWithBuiltins()));
    }

    [Fact]
    public async Task InvokeAsync_RunsTheToolThroughItsPlugin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "HELLO");

        var result = await WorkerToolset.InvokeAsync(
            Call("read_file", new { action = "read", path }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.Contains("HELLO", result);
        File.Delete(path);
    }

    [Fact]
    public async Task InvokeAsync_RefusesATOOLTheRoleDoesNotHave()
    {
        // The enforcement point. A reviewer that talks its way into write_file must be refused HERE,
        // not merely un-offered — a model can emit a call for a tool it was never shown.
        var result = await WorkerToolset.InvokeAsync(
            Call("write_file", new { action = "write", path = "/tmp/nope.txt", content = "x" }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists("/tmp/nope.txt"));

        // The refusal names what the role CAN use, so the model can correct itself in one turn
        // instead of guessing another name and burning turns against the cap.
        Assert.Contains("read_file", result);
    }

    [Fact]
    public async Task InvokeAsync_AWorkerCannotSpawnAnotherWorker()
    {
        // No sub-sub-agents. The property holds STRUCTURALLY — llm_agent is simply absent from
        // WorkerToolset.Specs, so there is no WorkerTool value that maps to it — but nothing pinned
        // it, and an absence is exactly the kind of invariant that vanishes when someone later adds
        // "one more useful tool" to that table.
        //
        // A worker that can spawn workers spawns them until the token budget dies, and each one is
        // billed. Asserted here against the FULL tool set, so it cannot pass merely because the role
        // under test happened to be read-only.
        var all = new[] { WorkerTool.ReadFile, WorkerTool.WriteFile, WorkerTool.RunShell, WorkerTool.HttpRequest };

        Assert.DoesNotContain("llm_agent", WorkerToolset.NamesFor(all));

        var result = await WorkerToolset.InvokeAsync(
            Call("llm_agent", new { prompt = "spawn a helper", role = "implementer" }),
            all, RegistryWithLlmAgent(), new TestJobContext(), CancellationToken.None);

        // Refused even though the plugin IS registered and reachable — the registry below has it.
        Assert.Contains("no such tool", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A registry that DOES contain llm_agent, so the test above proves the refusal comes from the
    /// tool table rather than from the plugin merely being absent.
    /// </summary>
    private static PluginRegistry RegistryWithLlmAgent()
    {
        var providers = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["local"] = new MockLlmProvider() }, "local");
        return PluginRegistry.CreateWithBuiltins(providers, PermissionGate.AllowAll, fanOut: true);
    }

    [Fact]
    public async Task InvokeAsync_AnUnknownToolReadsDifferentlyFromARefusedOne()
    {
        // Two different conditions the model must respond to differently: "no such tool" means pick a
        // real one; a role refusal means STOP asking. One shared string invites a retry loop.
        var unknown = await WorkerToolset.InvokeAsync(
            Call("delete_everything", new { }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        var refused = await WorkerToolset.InvokeAsync(
            Call("write_file", new { action = "write", path = "/tmp/nope2.txt", content = "x" }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.Contains("no such tool", unknown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no such tool", refused, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists("/tmp/nope2.txt"));
    }

    [Fact]
    public async Task InvokeAsync_APluginFailure_ReturnsTextNotAnException()
    {
        // The result is fed back to the model as a tool message. A throw here would kill the job over a
        // bad path, when the worker could have read the error and tried something else.
        var result = await WorkerToolset.InvokeAsync(
            Call("read_file", new { action = "read", path = "/definitely/not/here.txt" }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task InvokeAsync_MissingRequiredParam_ReturnsTheValidationError()
    {
        // NOT the same case as a bad path. `FileJobPlugin.ExecuteAsync` reads Get<string>("action") and
        // Get<string>("path") ABOVE its try/catch (FileJobPlugin.cs:36-37), and JobParameters.Get<T>
        // indexes Values[key] (JobParameters.cs:16) — so a model that emits read_file with no `path`
        // throws KeyNotFoundException straight out of the plugin and kills the job. Validate first.
        var result = await WorkerToolset.InvokeAsync(
            Call("read_file", new { }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        // The model must be told WHICH param it omitted, or it retries the same malformed call.
        Assert.Contains("path", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_TruncatesAHugeResult()
    {
        // read_file on a large file feeds the whole thing into the NEXT ChatAsync, every turn, for the
        // rest of the loop. Unbounded here means a context blowout mid-job.
        var path = Path.Combine(Path.GetTempPath(), $"wt-big-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, new string('x', WorkerToolset.MaxToolResultChars * 4));

        var result = await WorkerToolset.InvokeAsync(
            Call("read_file", new { action = "read", path }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.True(result.Length < WorkerToolset.MaxToolResultChars * 2);
        Assert.Contains("elided", result, StringComparison.OrdinalIgnoreCase);  // the cut must be VISIBLE
        File.Delete(path);
    }

    [Fact]
    public async Task InvokeAsync_AnElidedReadTELLSTheWorkerHowToPage()
    {
        // The loop this closes: a worker read a 36KB file, the cap elided the middle, and with no
        // hint in the RESULT it re-issued the identical call and got the identical elision until
        // the turn cap killed the job. The model cannot see the tool schema at the moment it is
        // staring at a hole -- the way out has to travel with the cut.
        var path = Path.Combine(Path.GetTempPath(), $"wt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, new string('x', WorkerToolset.MaxToolResultChars * 2));

        var result = await WorkerToolset.InvokeAsync(
            Call("read_file", new { action = "read", path }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.Contains("offset", result);
        Assert.Contains("limit", result);
        File.Delete(path);
    }

    [Fact]
    public async Task InvokeAsync_AnUNelidedReadGetsNoPagingAdvice()
    {
        // The guard against nagging every small read with instructions it does not need.
        var path = Path.Combine(Path.GetTempPath(), $"wt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "small");

        var result = await WorkerToolset.InvokeAsync(
            Call("read_file", new { action = "read", path }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None);

        Assert.DoesNotContain("too large", result);
        File.Delete(path);
    }

    [Fact]
    public void For_ReadFileExposesOffsetAndLimit()
    {
        // The params must reach the MODEL. They are generated from the plugin's JobSchema, so a
        // schema change is the only thing that puts them on the wire -- and the pinned-action
        // filter has dropped params from this surface before.
        var def = WorkerToolset.For(new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins())
            .Single(d => d.Name == "read_file");
        var json = def.InputSchema.ToString();

        Assert.Contains("offset", json);
        Assert.Contains("limit", json);
        Assert.DoesNotContain("\"action\"", json); // still pinned by the tool name
    }

    [Fact]
    public void EveryFileTool_PinsItsAction_SoNoneCanBeTurnedIntoADelete()
    {
        // The pinned-action property, re-pinned now that four tools share the file plugin: a worker
        // calling list_files must not be able to pass action: "delete".
        var registry = PluginRegistry.CreateWithBuiltins();
        foreach (var name in new[] { "read_file", "write_file", "list_files", "search_files", "replace_in_file" })
        {
            var def = WorkerToolset.For(Enum.GetValues<WorkerTool>(), registry)
                .SingleOrDefault(d => d.Name == name);
            Assert.NotNull(def);
            Assert.DoesNotContain("\"action\"", def!.InputSchema.GetRawText());
        }
    }

    private static string SchemaFor(string toolName) =>
        WorkerToolset.For(Enum.GetValues<WorkerTool>(), PluginRegistry.CreateWithBuiltins())
            .Single(d => d.Name == toolName).InputSchema.GetRawText();

    [Fact]
    public void ReadFile_ShowsOnlyTheParamsAReadCanUse()
    {
        // It used to show NINE: the whole FileJobPlugin schema minus the pinned action, so a read
        // advertised content, dest, replacement, regex and glob. A tool with nine optional-looking
        // params and one required has no shape the model can read -- a live drive produced nine
        // consecutive `read_file {}` calls with empty arguments before its first good one.
        var json = SchemaFor("read_file");

        Assert.Contains("\"path\"", json);
        Assert.Contains("\"offset\"", json);
        Assert.Contains("\"limit\"", json);
        foreach (var absent in new[] { "content", "dest", "replacement", "regex", "glob" })
            Assert.DoesNotContain($"\"{absent}\"", json);
    }

    [Fact]
    public void WriteAndReplace_MARKTheirRealRequirements()
    {
        // FileJobPlugin serves six actions from one schema, so it can only mark `action` and `path`
        // required -- `content` cannot be required there because `read` does not use it. With action
        // pinned away, every file tool said "path is all you need", which the plugin then rejects
        // for a write. Requiredness is a property of the TOOL, not of the plugin.
        var write = SchemaFor("write_file").Replace(" ", "");
        Assert.Contains("\"required\":[\"path\",\"content\"]", write);

        var replace = SchemaFor("replace_in_file").Replace(" ", "");
        Assert.Contains("\"required\":[\"path\",\"pattern\",\"replacement\"]", replace);
    }

    [Fact]
    public void SearchFiles_RequiresSomethingToSearchFor()
    {
        var json = SchemaFor("search_files").Replace(" ", "");
        Assert.Contains("\"required\":[\"path\",\"pattern\"]", json);
    }

    [Fact]
    public void EveryToolStillProjectsRealPluginParams()
    {
        // The anti-drift property that generating-from-schema existed for: selecting is allowed,
        // inventing is not. BuildDefinition throws on a name the plugin does not accept, so simply
        // building every tool is the assertion.
        var tools = WorkerToolset.For(Enum.GetValues<WorkerTool>(), PluginRegistry.CreateWithBuiltins());
        Assert.Equal(7, tools.Count);
    }

    // ---- ARGUMENT TYPE TOLERANCE -------------------------------------------
    // Models routinely stringify scalars. Each slip used to become a JsonException whose message
    // named a JSON path ("Path: $ | LineNumber: 0") rather than the parameter, so the model knew
    // something was wrong but not which argument to change -- and retried the same shape.

    [Fact]
    public async Task Invoke_AcceptsANumberSentAsAString()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "f.txt");
            File.WriteAllText(f, "one\ntwo\nthree\nfour\n");

            var r = await WorkerToolset.InvokeAsync(
                Call("read_file", new { path = f, offset = "2", limit = "1" }),
                new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
                new CollectingContext(), CancellationToken.None);

            Assert.Contains("two", r);
            Assert.DoesNotContain("LineNumber", r);   // no raw JsonException text
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Invoke_AcceptsABoolSentAsAString()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\n");

            var r = await WorkerToolset.InvokeAsync(
                Call("search_files", new { path = dir, pattern = "al.ha", regex = "true" }),
                new[] { WorkerTool.SearchFiles }, PluginRegistry.CreateWithBuiltins(),
                new CollectingContext(), CancellationToken.None);

            Assert.Contains("alpha", r);   // regex actually took effect
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Invoke_TreatsAnExplicitNullAsAbsent()
    {
        // A model with nothing to say for an optional arg emits null rather than omitting the key.
        // TryGetValue then succeeded, the default was never applied, and list_files threw a
        // NullReferenceException -- "Object reference not set to an instance of an object", which
        // tells the model nothing it can act on.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");

            var r = await WorkerToolset.InvokeAsync(
                Call("list_files", new { path = dir, pattern = (string?)null }),
                new[] { WorkerTool.ListFiles }, PluginRegistry.CreateWithBuiltins(),
                new CollectingContext(), CancellationToken.None);

            Assert.Contains("a.txt", r);
            Assert.DoesNotContain("Object reference", r);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Invoke_DoesNotThrowWhenAnArgumentIsAnArray()
    {
        // Argv-array form is natural -- many shell tools take one. This threw a JsonException out of
        // Validate, which sits OUTSIDE the try/catch, past both call sites and killed the turn.
        var r = await WorkerToolset.InvokeAsync(
            Call("run_shell", new { command = new[] { "ls", "-l" } }),
            new[] { WorkerTool.RunShell }, PluginRegistry.CreateWithBuiltins(),
            new CollectingContext(), CancellationToken.None);

        Assert.Contains("command", r);        // names the offending argument
        Assert.DoesNotContain("LineNumber", r);
    }

    [Fact]
    public async Task Invoke_NamesAMisspelledArgumentInsteadOfClaimingItIsAbsent()
    {
        // "'path' is required" contradicts what the model just sent: it DID supply a path, under the
        // wrong name. Faced with a message asserting an absence it can see is untrue, its cheapest
        // move is to resend the same shape.
        var r = await WorkerToolset.InvokeAsync(
            Call("read_file", new { file_path = "/tmp/x.txt" }),
            new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
            new CollectingContext(), CancellationToken.None);

        Assert.Contains("file_path", r);
        Assert.Contains("did you mean 'path'", r);
    }

    [Fact]
    public async Task Invoke_SaysNothingAboutUnknownArgsWhenTheCallSUCCEEDS()
    {
        // Only on failure. A stray key on a call that worked is not worth a lecture appended to a
        // good result -- that would put noise on the common path to fix the rare one.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "f.txt");
            File.WriteAllText(f, "hello\n");

            var r = await WorkerToolset.InvokeAsync(
                Call("read_file", new { path = f, encoding = "utf8" }),
                new[] { WorkerTool.ReadFile }, PluginRegistry.CreateWithBuiltins(),
                new CollectingContext(), CancellationToken.None);

            Assert.Contains("hello", r);
            Assert.DoesNotContain("Unrecognised", r);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
