using CxAgent.Plugins.RepoMap.Parsing;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

/// <summary>
/// Today's outline fixtures, read by the tree. Each expected result is the text reader's, except
/// where the parser is plainly more correct — every such case says so beside it.
/// </summary>
public sealed class TreeOutlinerTests : IDisposable
{
    private readonly Engine _engine = Engine.Open();

    private List<string> Outline(string path, string source) =>
        TreeOutliner.Outline(path, source, _engine.ReaderFor(Grammars.For(path)!)!, TreeOutliner.ParseBudget)!.Symbols
            .Select(s => $"{s.Line}:{s.Depth}:{s.Signature}")
            .ToList();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void CSharp_TypesMembersAndProperties_WithoutBodiesOrNamespaces()
    {
        var found = Outline("Agent.cs", """
            namespace Demo.Agents;

            /// <summary>A comment with a { brace.</summary>
            [Serializable]
            public sealed class Agent : IDisposable
            {
                private readonly string _name = "a { b";
                public string Name { get; init; }
                public int Count => _items.Count;

                public Agent(string name,
                             int size = 3)
                {
                    void Local() { }
                    var x = Compute(1);
                }

                public async Task<int> RunAsync<T>(T input, CancellationToken ct) => await Task.FromResult(1);

                void IDisposable.Dispose() { }

                private sealed record Step(string What);
            }

            public interface IRunner
            {
                Task Run(string goal);
            }

            public enum Mode { Fast, Slow }
            """);

        Assert.Equal(
        [
            "5:0:public sealed class Agent : IDisposable",
            "8:1:public string Name",
            "9:1:public int Count",
            "11:1:public Agent(string name, int size = 3)",
            "18:1:public async Task<int> RunAsync<T>(T input, CancellationToken ct)",
            "20:1:void IDisposable.Dispose()",
            "22:1:private sealed record Step(string What)",
            "25:0:public interface IRunner",
            "27:1:Task Run(string goal)",
            "30:0:public enum Mode",
        ], found);
    }

    /// <summary>
    /// TOP-LEVEL STATEMENTS ARE NOT DECLARATIONS. A C# Program.cs, a test runner's <c>describe(</c>
    /// and an expression continued over lines all put a name and a parenthesis at file scope.
    /// </summary>
    [Fact]
    public void CallsAtFileScope_AreNotDeclarations()
    {
        var found = Outline("Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            Console.WriteLine("hi");
            var path = config is null
                ? Path.Combine(x, "cxagent")
                : config;
            await RunAsync();
            static void Helper(int x) { }
            """);

        Assert.Equal(["7:0:static void Helper(int x)"], found);
    }

    [Fact]
    public void Python_ClassesAndMethods_NotNestedFunctions()
    {
        var found = Outline("svc.py", """"
            """A module docstring with def fake(): inside."""
            import os

            @dataclass
            class Service(Base):
                def __init__(self, name: str):
                    self.name = name

                    def inner():
                        pass

                async def fetch(self,
                                url: str) -> bytes:
                    return b""

            def main() -> None:
                print("class Not:")
            """");

        Assert.Equal(
        [
            "5:0:class Service(Base)",
            "6:1:def __init__(self, name: str)",
            "12:1:async def fetch(self, url: str) -> bytes",
            "16:0:def main() -> None",
        ], found);
    }

    [Fact]
    public void Go_FuncsMethodsAndInterfaceMembers()
    {
        var found = Outline("server.go", """
            package server

            import (
                "fmt"
            )

            type Handler interface {
                Serve(req Request) error
            }

            type Server struct {
                name string
            }

            func (s *Server) Start(port int) error {
                go func() { fmt.Println("{") }()
                return nil
            }

            func New(name string) *Server {
                return &Server{name: name}
            }
            """);

        Assert.Equal(
        [
            "7:0:type Handler interface",
            "8:1:Serve(req Request) error",
            "11:0:type Server struct",
            "15:0:func (s *Server) Start(port int) error",
            "20:0:func New(name string) *Server",
        ], found);
    }

    /// <summary>A LIFETIME IS NOT A STRING. Read as one, <c>'a</c> would blank the rest of the line, and
    /// with it the brace that opens the function's body.</summary>
    [Fact]
    public void Rust_ImplTraitAndLifetimes()
    {
        var found = Outline("lib.rs", """
            #[derive(Debug)]
            pub struct Parser<'a> {
                input: &'a str,
            }

            impl<'a> Parser<'a> {
                pub fn new(input: &'a str) -> Self {
                    let c = '{';
                    Parser { input }
                }
            }

            pub trait Visit {
                fn visit(&self);
            }

            impl Visit for Parser<'_> {
                fn visit(&self) {}
            }

            mod tests {
                fn helper() {}
            }
            """);

        Assert.Equal(
        [
            "2:0:pub struct Parser<'a>",
            "6:0:impl<'a> Parser<'a>",
            "7:1:pub fn new(input: &'a str) -> Self",
            "13:0:pub trait Visit",
            "14:1:fn visit(&self)",
            "17:0:impl Visit for Parser<'_>",
            "18:1:fn visit(&self)",
            "22:0:fn helper()",
        ], found);
    }

    [Fact]
    public void TypeScript_ClassesFunctionsArrowsAndTypes()
    {
        var found = Outline("app.ts", """
            import { x } from "./x";

            export interface Options {
                verbose: boolean;
                log(message: string): void;
            }

            export type Id = string;

            export class App {
                private count = compute(2);
                constructor(private readonly options: Options) {}
                async start(): Promise<void> {
                    const t = `template ${"{"}`;
                }
            }

            export function boot(options: Options): App {
                return new App(options);
            }

            export const handler = async (event: Event) => {
                return 1;
            };

            describe("app", () => {
                it("works", () => {});
            });
            """);

        Assert.Equal(
        [
            "3:0:export interface Options",
            "5:1:log(message: string): void",
            "8:0:export type Id = string",
            "10:0:export class App",
            "12:1:constructor(private readonly options: Options)",
            "13:1:async start(): Promise<void>",
            "18:0:export function boot(options: Options): App",
            "22:0:export const handler = async (event: Event) =>",
        ], found);
    }

    /// <summary>
    /// A BODILESS MEMBER WITHOUT A SEMICOLON ENDS AT ITS LINE. Held open, it would claim the next
    /// declaration's brace and the class after it would vanish from the map.
    /// </summary>
    [Fact]
    public void TypeScript_InterfaceWithoutSemicolons_DoesNotSwallowTheNextClass()
    {
        var found = Outline("x.ts", """
            interface Store {
              get(key: string): string
            }
            class Memory {
              get(key: string) { return "" }
            }
            """);

        Assert.Equal(["1:0:interface Store", "2:1:get(key: string): string", "4:0:class Memory", "5:1:get(key: string)"], found);
    }

    /// <summary>
    /// A BLOCK THAT DECLARES NOTHING ITSELF IS SEEN THROUGH. <c>extern "C" {</c> wraps whole C headers,
    /// and <c>declare module "x" {</c> whole TypeScript declaration files; treated as a body, every
    /// declaration inside would vanish from the map.
    /// </summary>
    [Fact]
    public void ExternAndDeclareBlocks_AreSeenThrough()
    {
        Assert.Equal(["4:0:int cxp_ok(const char *text)"], Outline("cxp.h", """
            #ifdef __cplusplus
            extern "C" {
            #endif
            int cxp_ok(const char *text);
            #ifdef __cplusplus
            }
            #endif
            """));

        Assert.Equal(["2:0:export function parse(text: string): Node", "5:0:interface Window"], Outline("x.d.ts", """
            declare module "parser" {
              export function parse(text: string): Node;
            }
            declare global {
              interface Window { app: App }
            }
            """));
    }

    /// <summary>
    /// ONE STRAY PARENTHESIS DOES NOT COST THE REST OF THE FILE. A regex literal the stripper cannot
    /// recognise leaves a <c>(</c> open; a brace or a dedent ends whatever it seemed to start.
    /// </summary>
    [Fact]
    public void AStrayParenthesis_DoesNotHideTheRestOfTheFile()
    {
        Assert.Equal(["1:0:function clean(s)", "4:0:function later()"], Outline("x.js", """
            function clean(s) {
              return s.replace(/\(/g, "");
            }
            function later() {
            }
            """));

        // VALID RUBY, WITH clean's end: without it the parser rightly reads later as defined inside
        // clean, where the text reader went by indentation.
        Assert.Equal(["1:0:def clean(s)", "4:0:def later"], Outline("x.rb", """
            def clean(s)
              s.gsub(/\(/, "")
            end
            def later
            end
            """));
    }

    /// <summary>A CLASS HEADER WRAPPED BEFORE ITS BRACE still opens the class, in a language whose braces sit on the declaration's line.</summary>
    [Fact]
    public void AWrappedClassHeader_KeepsItsMembers()
    {
        Assert.Equal(["1:0:public class Foo extends Bar", "3:1:public void run()"], Outline("Foo.java", """
            public class Foo
                extends Bar {
                public void run() { }
            }
            """));
    }

    [Fact]
    public void C_FunctionsAndStructs_NotMacrosOrForwardDeclarations()
    {
        var found = Outline("cxp.c", """
            #include <stdio.h>
            #define MAX(a, b) ((a) > (b) ? (a) : (b))

            struct node;

            struct node {
                int value;
            };

            static struct node empty = { 0 };

            static int count(const char *text)
            {
                return printf("%s{", text);
            }

            int
            main(void) { return 0; }
            """);

        Assert.Equal(
        [
            "6:0:struct node",
            "12:0:static int count(const char *text)",
            // MORE CORRECT: the text reader missed a return type on the line before the name.
            "17:0:int main(void)",
        ], found);
    }

    [Fact]
    public void Java_MethodsWithAnnotations()
    {
        var found = Outline("Svc.java", """
            package demo;

            public class Svc {
                @Override
                public String toString() { return "}"; }

                @Inject public Svc(Repo repo) { }

                public static <T> List<T> all(Class<T> type) { return null; }
            }
            """);

        Assert.Equal(
        [
            "3:0:public class Svc",
            "5:1:public String toString()",
            "7:1:@Inject public Svc(Repo repo)",
            "9:1:public static <T> List<T> all(Class<T> type)",
        ], found);
    }

    [Fact]
    public void RubyAndShell()
    {
        // A RUBY MODULE IS A NAMESPACE, as `namespace` is in the brace languages: not shown, and no
        // deeper. Counted as a type, every class in sinatra's `module Sinatra` sat one level down,
        // where a type does not link the files that use it, and the library ranked below its specs.
        Assert.Equal(
        [
            "2:0:class Invoice",
            "3:1:def total(items)",
            "6:1:def self.build",
        ], Outline("billing.rb", """
            module Billing
              class Invoice
                def total(items)
                  items.sum # def not_this
                end
                def self.build
                end
              end
            end
            """));

        Assert.Equal(
        [
            "1:0:build()",
            "4:0:function deploy",
        ], Outline("ci.sh", """
            build() {
              echo "$#"
            }
            function deploy {
              build
            }
            """));
    }

    /// <summary>A PARAMETER LIST STARTED ON THE NEXT LINE joins without a space inside its parentheses.</summary>
    [Fact]
    public void AParameterListOnItsOwnLines_JoinsCleanly()
    {
        var found = Outline("R.cs", """
            public sealed record AgentTypeDefinition(
                string Name,
                int Turns
            );
            """);

        Assert.Equal(["1:0:public sealed record AgentTypeDefinition(string Name, int Turns)"], found);
    }

    [Fact]
    public void ALongSignature_IsCutAndSaysSo()
    {
        var parameters = string.Join(", ", Enumerable.Range(1, 30).Select(i => $"int parameterNumber{i}"));
        var found = Outline("Long.cs", $"class C\n{{\n    public void M({parameters}) {{ }}\n}}\n");

        Assert.EndsWith("…", Assert.Single(found, f => f.Contains(" M(")));
    }

    /// <summary>A FILE THE OUTLINER CANNOT MAKE SENSE OF COSTS ITS OWN DECLARATIONS, never an exception.</summary>
    [Fact]
    public void Garbage_GivesNothingAndDoesNotThrow()
    {
        var found = Outline("x.cs", "}}}} ((( \"unterminated\n{{{{ ]]] class\n");
        Assert.NotNull(found);
    }

    /// <summary>
    /// AN ARROW WHOSE PARAMETERS ARE DESTRUCTURED ACROSS LINES IS A DECLARATION — the way most React
    /// components are written. Its body's own consts stay out.
    /// </summary>
    [Fact]
    public void TypeScript_AnArrowWithDestructuredParameters_IsFound()
    {
        Assert.Equal(["1:0:export const SidebarItem = ({ item, }: { item: Item; }) =>"], Outline("item.tsx", """
            export const SidebarItem = ({
              item,
            }: {
              item: Item;
            }) => {
              const tag = 1;
              return tag;
            };
            """));
    }

    /// <summary>
    /// AN EXPORTED CONST IS PART OF THE MODULE'S API whatever its value — a component wrapped in
    /// <c>forwardRef</c>, a regex, a config object — and is shown up to its <c>=</c>, not with the
    /// value. A local const and a destructuring export name nothing a reader can look up.
    /// </summary>
    [Fact]
    public void TypeScript_ExportedConsts_AreFoundWithoutTheirValues()
    {
        Assert.Equal(
        [
            "1:0:export const cuid: RegExp",
            "2:0:export const Accordion",
            "9:0:export const config",
        ], Outline("ui.ts", """
            export const cuid: RegExp = /^[a-z]+$/;
            export const Accordion = forwardRef<
              HTMLDivElement,
              Props
            >(({ title }, ref) => {
              return title;
            });
            const local = 3;
            export const config = {
              a: 1,
            };
            export const { GET } = make();
            """));
    }

    /// <summary>
    /// A MACRO'S BODY IS NOT CODE AT FILE SCOPE. The lines a <c>#define</c> continues with a
    /// backslash are skipped with it, braces and all: <c>namespace fmt {</c> inside one opens nothing.
    /// </summary>
    [Fact]
    public void C_AMultiLineMacroBody_IsSkipped()
    {
        Assert.Equal(["4:0:int visible(int x)"], Outline("ns.h", """
            #define BEGIN_NS \
              namespace fmt { \
              struct hidden_t {};
            int visible(int x);
            """));
    }

    /// <summary>A CONSTRUCTOR'S INITIALISER LIST IS ITS BODY'S BUSINESS: the signature ends with the parameters.</summary>
    [Fact]
    public void Cpp_AConstructorsInitialiserList_IsNotInItsSignature()
    {
        Assert.Equal(
        [
            "1:0:class Value",
            "2:1:constexpr Value(short x)",
            "3:1:Value(int a, int b)",
            "5:1:auto name() const -> std::string",
        ], Outline("value.hpp", """
            class Value {
              constexpr Value(short x) : int_value(x) {}
              Value(int a, int b)
                  : first(a), second(b) {}
              auto name() const -> std::string { return n; }
            };
            """));
    }

    /// <summary>A TRAILING COMMA, the way rustfmt writes a parameter list that wraps, is not shown.</summary>
    [Fact]
    public void Rust_ATrailingCommaInAWrappedParameterList_IsDropped()
    {
        Assert.Equal(["1:0:pub fn after(&mut self, line_count: usize) -> &mut Self"], Outline("b.rs", """
            pub fn after(
                &mut self,
                line_count: usize,
            ) -> &mut Self {
                self
            }
            """));
    }

    /// <summary>A REGEX WITH A BACKTICK IN IT DOES NOT HIDE THE DECLARATIONS AFTER IT — zod's html5Email.</summary>
    [Fact]
    public void TypeScript_ARegexWithABacktick_HidesNothingAfterIt()
    {
        Assert.Equal(["1:0:export const html5Email: RegExp", "4:0:export const rfc5322Email", "6:0:export function after()"],
            Outline("regexes.ts", """
                export const html5Email: RegExp =
                  /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9]$/;

                export const rfc5322Email =
                  /^(([^<>()\[\]\\.,;:\s@"]+)|(".+"))$/;
                export function after() {}
                """));
    }

    /// <summary>
    /// MARKUP AROUND A CALL IS NOT A RETURN TYPE. A PHP view's <c>{{ app()->version() }}</c> declares
    /// nothing, and a declaration whose signature would be empty is never shown.
    /// </summary>
    [Fact]
    public void Php_TemplateMarkupAroundACall_DeclaresNothing()
    {
        // A <?php TAG BEFORE THE FUNCTION: without one the parser rightly reads the whole file as
        // markup, where the text reader took the last lines for code.
        Assert.Equal(["5:0:function real($a)"], Outline("view.php", """
            <p>Laravel {{ app()->version() }}</p>
            {{ $e->request()->host() }}
            Laravel {{ app()->version() }}
            <?php
            function real($a)
            {
            }
            """));
    }

    /// <summary>
    /// A C# PROPERTY WITH AN EXPRESSION BODY IS A PROPERTY whatever the expression calls: the call
    /// to the right of its <c>=&gt;</c> is not the declaration.
    /// </summary>
    [Fact]
    public void CSharp_AnExpressionBodiedPropertyThatCalls_IsAProperty()
    {
        Assert.Equal(
        [
            "1:0:public sealed class Call",
            "3:1:public IReadOnlyList<Report.Progress> Progress",
            "4:1:private static Expression[] NoArgs",
        ], Outline("Call.cs", """
            public sealed class Call
            {
                public IReadOnlyList<Report.Progress> Progress => [.. Reports.OfType<Report.Progress>()];
                private static Expression[] NoArgs => CollectionUtils.ArrayEmpty<Expression>();
            }
            """));
    }

    /// <summary>
    /// A SHELL GROUP AROUND THE WHOLE SCRIPT IS SEEN THROUGH. nvm.sh wraps itself in <c>{ … }</c> so
    /// a half-downloaded copy never runs; taken as a body, every function inside would vanish.
    /// </summary>
    [Fact]
    public void Shell_AGroupAroundTheScript_IsSeenThrough()
    {
        Assert.Equal(["2:0:nvm_a()", "5:0:nvm_b()"], Outline("nvm.sh", """
            { # this ensures the entire script is downloaded #
            nvm_a() {
              echo
            }
            nvm_b() {
              { echo; inner() { :; }; } > /dev/null
            }
            }
            """));
    }

    /// <summary>A RUBY SUPERCLASS KEEPS ITS SPACE: <c>class Request &lt; Rack::Request</c> is not a generic.</summary>
    [Fact]
    public void Ruby_ASuperclassKeepsItsSpacing()
    {
        Assert.Equal(["1:0:class Request < Rack::Request"], Outline("base.rb", """
            class Request < Rack::Request
            end
            """));
    }

    /// <summary>
    /// A COMMAND SUBSTITUTION INSIDE A DOUBLE-QUOTED STRING QUOTES FOR ITSELF. In
    /// <c>"$(type "${COMMAND}" | sed 's/\(//g')"</c> the inner quotes open strings of their own;
    /// read as closing the outer one, the quoting falls out of step, and a single quote — which
    /// spans lines in shell — swallowed the functions after it. Taken from nvm.sh.
    /// </summary>
    [Fact]
    public void Shell_QuotesInsideACommandSubstitution_HideNothing()
    {
        Assert.Equal(["1:0:nvm_command_info()", "21:0:nvm_has_colors()", "30:0:nvm_has_italics()"], Outline("nvm.sh", """"
            nvm_command_info() {
              local COMMAND
              local INFO
              COMMAND="${1}"
              if type "${COMMAND}" | nvm_grep -q hashed; then
                INFO="$(type "${COMMAND}" | command sed -E 's/\(|\)//g' | command awk '{print $4}')"
              elif type "${COMMAND}" | nvm_grep -q aliased; then
                # shellcheck disable=SC2230
                INFO="$(which "${COMMAND}") ($(type "${COMMAND}" | command awk '{ $1=$2=$3=$4="" ;print }' | command sed -e 's/^\ *//g' -Ee "s/\`|'//g"))"
              elif type "${COMMAND}" | nvm_grep -q "^${COMMAND} is an alias for"; then
                # shellcheck disable=SC2230
                INFO="$(which "${COMMAND}") ($(type "${COMMAND}" | command awk '{ $1=$2=$3=$4=$5="" ;print }' | command sed 's/^\ *//g'))"
              elif type "${COMMAND}" | nvm_grep -q "^${COMMAND} is /"; then
                INFO="$(type "${COMMAND}" | command awk '{print $3}')"
              else
                INFO="$(type "${COMMAND}")"
              fi
              nvm_echo "${INFO}"
            }

            nvm_has_colors() {
              local NVM_NUM_COLORS
              if nvm_has tput; then
                NVM_NUM_COLORS="$(command tput -T "${TERM:-vt100}" colors)"
              fi
              [ -t 1 ] && [ "${NVM_NUM_COLORS:--1}" -ge 8 ] && [ "${NVM_NO_COLORS-}" != '--no-colors' ]
            }

            # terminals without italics may render the italic escape as reverse video instead, so check terminfo's `sitm` capability
            nvm_has_italics() {
              nvm_has_colors && command tput -T "${TERM:-vt100}" sitm >/dev/null 2>&1
            }
            """"));
    }

    /// <summary>A PROTOTYPE IS ITS DEFINITION SEEN EARLY in a source file, and the API itself in a header.</summary>
    [Fact]
    public void APrototype_IsDroppedWhereItsDefinitionIs_AndKeptInAHeader()
    {
        const string source = "int count(int n);\nint count(int n) { return n; }\n";
        Assert.Equal(["2:0:int count(int n)"], Outline("a.c", source));
        Assert.Equal(["1:0:int count(int n)"], Outline("a.h", "int count(int n);\n"));
    }

    /// <summary>ONE DECLARATION, TWO NAMES FOR IT: <c>typedef struct X { } X;</c> is listed once, not as a struct inside its own typedef.</summary>
    [Fact]
    public void ATypedefOfAStruct_IsListedOnce()
    {
        var found = Outline("a.c", "typedef struct point {\n    int x;\n} point;\n");
        Assert.Single(found);
        Assert.StartsWith("1:0:typedef struct point", found[0]);
    }

    /// <summary>NOTHING INSIDE A BODY: a struct or class declared in a function is that function's business.</summary>
    [Fact]
    public void ATypeInsideAFunction_IsHidden()
    {
        Assert.Equal(["1:0:void run()"], Outline("a.cpp", "void run() {\n    struct Local { int x; };\n    class Inner { void m() {} };\n}\n"));
        Assert.Equal(["1:0:def run()"], Outline("a.py", "def run():\n    class Inner:\n        def m(self):\n            pass\n"));
        Assert.Equal(["1:0:export function run()"], Outline("a.ts", "export function run() {\n  class Inner { m() {} }\n  const f = () => 1;\n}\n"));
    }

    /// <summary>A SYNTAX ERROR COSTS THE BROKEN PART, not the file: Tree-sitter recovers around it.</summary>
    [Fact]
    public void ABrokenMethod_KeepsTheRestOfTheFile()
    {
        var found = Outline("A.java", "public class A {\n    void broken( {\n    }\n    public int ok() { return 1; }\n}\n");
        Assert.Contains("1:0:public class A", found);
        Assert.Contains("4:1:public int ok()", found);
    }

    /// <summary>
    /// THE BINDING INDEXES UTF-16 CODE UNITS, as C# strings do. A line or a signature computed from
    /// those indexes must not drift after text outside ASCII — an accented comment, an emoji's
    /// surrogate pair.
    /// </summary>
    [Fact]
    public void NonAsciiText_KeepsLinesAndSignaturesAligned()
    {
        var found = Outline("a.py", "# café — 🎉 déjà vu\nclass Ünïcode:\n    def naïve(self, ça: str):\n        pass\n");
        Assert.Equal(["2:0:class Ünïcode", "3:1:def naïve(self, ça: str)"], found);
    }

    /// <summary>A COMMENT INSIDE A SIGNATURE IS NOT PART OF WHAT IT SAYS.</summary>
    [Fact]
    public void ACommentInsideASignature_IsLeftOut()
    {
        Assert.Equal(["1:0:int add(int a, int b)"], Outline("a.c", "int add(int a, /* second */ int b) { return a + b; }\n"));
    }

    /// <summary>
    /// A NUL CHARACTER IS END OF INPUT TO TREE-SITTER'S LEXER, so one inside a string literal would end
    /// the file there. It is read as a space instead: same length, so no line or offset moves.
    /// </summary>
    [Fact]
    public void ANulCharacter_HidesNothingAfterIt()
    {
        var found = Outline("A.cs", "class A\n{\n    string K() => $\"a\0b\";\n    void After() { }\n}\n");
        Assert.Equal(["1:0:class A", "3:1:string K()", "4:1:void After()"], found);
    }

    /// <summary>OPERATORS, INDEXERS AND DESTRUCTORS ARE MEMBERS a reader looks for — <c>JToken</c>'s conversions are most of its API.</summary>
    [Fact]
    public void CSharp_OperatorsIndexersAndDestructors_AreMembers()
    {
        var found = Outline("Id.cs", """
            public sealed class Id
            {
                public static implicit operator Id(string id) => new();
                public static Id operator +(Id a, Id b) => a;
                public object this[string key] => key;
                ~Id() { }
            }
            """);
        Assert.Equal(
        [
            "1:0:public sealed class Id",
            "3:1:public static implicit operator Id(string id)",
            "4:1:public static Id operator +(Id a, Id b)",
            "5:1:public object this[string key]",
            "6:1:~Id()",
        ], found);
    }

    [Fact]
    public void Cpp_OperatorsAndSpecialisations_AreMembers()
    {
        var found = Outline("a.h", """
            struct checker {
              auto operator()(int value) -> bool { return true; }
            };
            template <> inline auto find<false, char>(const char* first) -> bool { return false; }
            """);
        Assert.Equal(
        [
            "1:0:struct checker",
            "2:1:auto operator()(int value) -> bool",
            "4:0:template <> inline auto find<false, char>(const char* first) -> bool",
        ], found);
    }

    [Fact]
    public void Go_TypeAliases_AreTypes()
    {
        Assert.Equal(["3:0:type byte = uint8"], Outline("b.go", "package builtin\n\ntype byte = uint8\n"));
    }

    /// <summary>AN IMPL FOR A TYPE THAT IS NOT A PATH — a slice, a primitive, a reference — is an impl all the same; so is a trait's associated type.</summary>
    [Fact]
    public void Rust_ImplsForAnyTypeAndAssociatedTypes()
    {
        var found = Outline("lib.rs", """
            impl std::ops::Index<Match> for [u8] {
                fn index(&self, m: Match) -> &[u8] { &self[m.start..m.end] }
            }
            impl<'a, S: Sink> Sink for &'a mut S {}
            pub trait Sink {
                type Error: SinkError;
            }
            """);
        Assert.Equal(
        [
            "1:0:impl std::ops::Index<Match> for [u8]",
            "2:1:fn index(&self, m: Match) -> &[u8]",
            "4:0:impl<'a, S: Sink> Sink for &'a mut S",
            "5:0:pub trait Sink",
            "6:1:type Error: SinkError",
        ], found);
    }
}
