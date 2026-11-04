using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Jobs.Builtin;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// replace_in_file with a LIST of edits: applied in order, each to the result of the one before, and
/// written once — or, if any edit cannot be applied, not written at all. A local model that is told
/// "nothing happened, edit 3 failed because X" recovers; one that has to work out the state of a
/// half-edited file starts re-sending edits that already landed.
/// </summary>
public class ReplaceEditsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-edits-" + Guid.NewGuid().ToString("N"));
    public ReplaceEditsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static readonly CollectingContext Ctx = new();

    private string File(string text, string name = "f.txt")
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private static JsonElement Edits(params (string pattern, string replacement)[] edits) =>
        JsonSerializer.SerializeToElement(edits.Select(e => new { pattern = e.pattern, replacement = e.replacement }));

    private static JobParameters P(params (string k, object? v)[] kv) => new(kv.ToDictionary(x => x.k, x => x.v));

    private static Task<JobResult> Run(params (string k, object? v)[] kv) =>
        new FileJobExecutor().ExecuteAsync(P([("action", "replace"), .. kv]), Ctx, CancellationToken.None);

    private static JobValidation Validate(params (string k, object? v)[] kv) =>
        new FileJobExecutor().Validate(P([("action", "replace"), .. kv]));

    [Fact]
    public async Task Two_edits_apply_in_order_and_write_once()
    {
        var path = File("one A two\n");
        var r = await Run(("path", path), ("edits", Edits(("A", "B"), ("B", "C"))));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("one C two\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Second_edit_matches_text_the_first_created()
    {
        var path = File("alpha\n");
        var r = await Run(("path", path), ("edits", Edits(("alpha", "alpha\nbeta"), ("beta", "gamma"))));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("alpha\ngamma\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_not_found_writes_nothing_and_names_the_edit()
    {
        var path = File("keep this\n");
        var r = await Run(("path", path), ("edits", Edits(("keep", "kept"), ("missing", "x"))));
        Assert.False(r.Success);
        Assert.Contains("edit 2", r.ErrorMessage);
        Assert.Contains("not found", r.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep this\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_found_twice_writes_nothing_and_says_how_many()
    {
        var path = File("dup\ndup\n");
        var r = await Run(("path", path), ("edits", Edits(("dup", "one"))));
        Assert.False(r.Success);
        Assert.Contains("edit 1", r.ErrorMessage);
        Assert.Contains("2", r.ErrorMessage);
        Assert.Equal("dup\ndup\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task An_identical_edit_in_the_list_refuses_the_call_naming_it()
    {
        var path = File("a b\n");
        var r = await Run(("path", path), ("edits", Edits(("a", "x"), ("b", "b"))));
        Assert.False(r.Success);
        Assert.Contains("edit 2", r.ErrorMessage);
        Assert.Contains("identical", r.ErrorMessage);
        Assert.Equal("a b\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void Empty_edits_list_is_refused_by_validation()
    {
        var v = Validate(("path", "f.txt"), ("edits", Edits()));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("edits"));
    }

    [Fact]
    public void Edit_missing_replacement_is_refused_naming_it()
    {
        var edits = JsonSerializer.SerializeToElement(new object[]
            { new { pattern = "a", replacement = "b" }, new { pattern = "c" } });
        var v = Validate(("path", "f.txt"), ("edits", edits));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("edit 2") && e.Contains("replacement"));
    }

    [Fact]
    public async Task Empty_replacement_deletes()
    {
        var path = File("keep drop keep\n");
        var r = await Run(("path", path), ("edits", Edits((" drop", ""))));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("keep keep\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Flat_shape_runs_as_a_list_of_one()
    {
        // RESUMED HISTORY is full of these, and a model copies its own history.
        var path = File("old\n");
        Assert.True(Validate(("path", path), ("pattern", "old"), ("replacement", "new")).IsValid);
        var r = await Run(("path", path), ("pattern", "old"), ("replacement", "new"));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("new\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void Both_shapes_in_one_call_are_refused()
    {
        var v = Validate(("path", "f.txt"), ("pattern", "a"), ("replacement", "b"), ("edits", Edits(("c", "d"))));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("edits") && e.Contains("pattern"));
    }

    [Fact]
    public async Task Edits_sent_as_a_json_string_are_accepted()
    {
        // Local models often send a nested argument as a string holding its JSON.
        var path = File("one\n");
        var asString = JsonSerializer.SerializeToElement(Edits(("one", "two")).GetRawText());
        var r = await Run(("path", path), ("edits", asString));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("two\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Bom_is_kept_across_several_edits()
    {
        var path = Path.Combine(_dir, "bom.cs");
        System.IO.File.WriteAllText(path, "int a = 1;\nint b = 2;\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var r = await Run(("path", path), ("edits", Edits(("int a = 1;", "int a = 10;"), ("int b = 2;", "int b = 20;"))));
        Assert.True(r.Success, r.ErrorMessage);
        var bytes = System.IO.File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("int a = 10;\nint b = 20;\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Crlf_is_kept_across_several_edits()
    {
        var path = Path.Combine(_dir, "crlf.cs");
        System.IO.File.WriteAllText(path, "int a = 1;\r\nint b = 2;\r\n");
        var r = await Run(("path", path), ("edits", Edits(("int a = 1;", "int a = 10;"), ("int b = 2;", "int b = 20;"))));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("int a = 10;\r\nint b = 20;\r\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Indentation_is_shifted_per_edit()
    {
        var path = File("class C\n{\n    void A() { }\n        void B() { }\n}\n");
        var r = await Run(("path", path), ("edits", Edits(("void A() { }", "void A2() { }"), ("void B() { }", "void B2() { }"))));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("class C\n{\n    void A2() { }\n        void B2() { }\n}\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task A_single_edit_below_the_first_line_of_a_crlf_file_applies()
    {
        // THE MATCHER'S TOLERANT PASS ONCE TOOK LINE OFFSETS FROM CRLF-NORMALISED TEXT, so on a CRLF
        // file every line after the first was offset short, its span never equalled the exact pass's,
        // and a whole-line pattern there "appeared 2 times" — every edit below line 1 refused.
        var path = Path.Combine(_dir, "one.cs");
        System.IO.File.WriteAllText(path, "int a = 1;\r\nint b = 2;\r\nint c = 3;\r\n");
        var r = await Run(("path", path), ("pattern", "int c = 3;"), ("replacement", "int c = 30;"));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("int a = 1;\r\nint b = 2;\r\nint c = 30;\r\n", System.IO.File.ReadAllText(path));
    }
}
