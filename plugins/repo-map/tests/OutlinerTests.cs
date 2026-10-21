using CxAgent.Plugins.RepoMap;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

/// <summary>
/// One fixture per language family: what the outliner must find, and what it must leave alone. Each
/// fixture carries the traps that family has — a brace in a string, a call at file scope, a function
/// nested in a function — beside the declarations, because a rule that finds declarations is only
/// half right until it is shown what not to find.
/// </summary>
public class OutlinerTests
{
    private static List<string> Outline(string path, string source) =>
        Outliner.Outline(Stripper.Strip(source, Language.For(path)!), Language.For(path)!)
            .Select(s => $"{s.Line}:{s.Depth}:{s.Signature}")
            .ToList();

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

        Assert.Equal(["1:0:def clean(s)", "3:0:def later"], Outline("x.rb", """
            def clean(s)
              s.gsub(/\(/, "")
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
    /// A QUALIFIED CALL WITH A TYPE ARGUMENT IS STILL A CALL. Gradle's Kotlin scripts are made of
    /// <c>tasks.register&lt;Copy&gt;("…") {</c>, and none of those declares anything.
    /// </summary>
    [Fact]
    public void Kotlin_AQualifiedCallWithATypeArgument_IsNotADeclaration()
    {
        // The val is a declaration — a binding the script names — and is shown up to its `=`.
        Assert.Equal(["4:0:val nativeCompile"], Outline("build.gradle.kts", """
            tasks.register<Copy>("copyResourcesTemplates") {
              from("templates")
            }
            val nativeCompile = tasks.named<BuildNativeImageTask>("nativeCompile") {
            }
            """));
    }

    /// <summary>
    /// MARKUP AROUND A CALL IS NOT A RETURN TYPE. A PHP view's <c>{{ app()->version() }}</c> declares
    /// nothing, and a declaration whose signature would be empty is never shown.
    /// </summary>
    [Fact]
    public void Php_TemplateMarkupAroundACall_DeclaresNothing()
    {
        Assert.Equal(["4:0:function real($a)"], Outline("view.php", """
            <p>Laravel {{ app()->version() }}</p>
            {{ $e->request()->host() }}
            Laravel {{ app()->version() }}
            function real($a)
            {
            }
            """));
    }

    /// <summary>A BLADE VIEW IS A TEMPLATE, not PHP someone declared things in.</summary>
    [Fact]
    public void ABladeTemplate_IsNotRead() => Assert.Null(Language.For("resources/views/markdown.blade.php"));

    /// <summary>
    /// SWIFT'S AND KOTLIN'S PROPERTIES ARE DECLARATIONS, computed or stored: <c>var</c>, <c>val</c>
    /// and <c>let</c> at the top of a type or a file, shown up to their body or their <c>=</c>. A
    /// <c>let</c> in a function body stays out.
    /// </summary>
    [Fact]
    public void SwiftAndKotlin_PropertiesAreFound()
    {
        Assert.Equal(
        [
            "1:0:public struct JSONEncoding",
            "2:1:public static var `default`: JSONEncoding",
            "3:1:public let options: Options",
            "4:1:private var cache",
            "5:1:public func encode(_ request: URLRequest) throws -> URLRequest",
        ], Outline("JSONEncoding.swift", """
            public struct JSONEncoding {
                public static var `default`: JSONEncoding { JSONEncoding() }
                public let options: Options
                private var cache = Cache(size: 3)
                public func encode(_ request: URLRequest) throws -> URLRequest {
                    let local = 1
                    return request
                }
            }
            """));

        Assert.Equal(["1:0:class Test", "2:1:val request by lazy", "3:1:var count: Int"], Outline("Test.kt", """
            class Test {
              val request by lazy { Request(server.url("/")) }
              var count: Int = 0
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

    /// <summary>
    /// A C# PROPERTY WITH ITS BRACE ON THE NEXT LINE is a property: Newtonsoft writes them all that
    /// way. And <c>object</c> is C#'s type, not Kotlin's keyword — a field typed with it is a field.
    /// </summary>
    [Fact]
    public void CSharp_AnAllmanProperty_IsAProperty_AndAnObjectFieldIsNotAType()
    {
        Assert.Equal(
        [
            "1:0:class Person",
            "3:1:public string FullName",
            "7:1:public override bool CanRead",
        ], Outline("Person.cs", """
            class Person
            {
                public string FullName
                {
                    get { return First + " " + Last; }
                }
                public override bool CanRead
                {
                    get { return true; }
                }
                private readonly object _lock;
            }
            """));
    }

    /// <summary>
    /// JAVA'S OWN SHAPES: an annotation type, a generic method whose type parameters come first, an
    /// enum with methods after its constants — and a call with explicit type arguments, which is
    /// not a declaration.
    /// </summary>
    [Fact]
    public void Java_AnnotationTypesGenericMethodsAndEnumMethods_ButNotAGenericCall()
    {
        Assert.Equal(
        [
            "1:0:public @interface Marked",
            "2:0:interface Factory",
            "3:1:<T extends Number> T make(Class<T> type)",
            "5:0:public enum Impl",
            "10:1:abstract int size()",
            "11:1:static Impl parse(String s)",
            "13:0:class Holder",
        ], Outline("Impl.java", """
            public @interface Marked {}
            interface Factory {
                <T extends Number> T make(Class<T> type);
            }
            public enum Impl {
                Hash {
                    @Override
                    int size() { return 1; }
                };
                abstract int size();
                static Impl parse(String s) { return Hash; }
            }
            class Holder {
                static final Set<String> NAMES =
                    ImmutableSet.<String>builder().add("a").build();
            }
            """));
    }

    /// <summary>
    /// PHP: a helpers file declares its functions inside <c>if (! function_exists(…))</c>, and they
    /// are the file's API; a method may be named like another language's keyword; a call through
    /// <c>-&gt;</c> or <c>::</c> at the top of a script declares nothing.
    /// </summary>
    [Fact]
    public void Php_GuardedFunctionsAndKeywordNamedMethods_ButNotCalls()
    {
        Assert.Equal(
        [
            "3:0:function app($abstract = null)",
            "10:0:class Authorize",
            "12:1:public static function using($ability, ...$models)",
        ], Outline("helpers.php", """
            <?php
            if (! function_exists('app')) {
                function app($abstract = null)
                {
                    return Container::getInstance();
                }
            }
            $middleware->trustProxies(at: '*');
            Route::get('/', fn () => view('welcome'));
            class Authorize
            {
                public static function using($ability, ...$models)
                {
                }
            }
            """));
    }

    /// <summary>GO'S GROUPED TYPES: <c>type ( … )</c> declares each line inside it.</summary>
    [Fact]
    public void Go_TypesInAGroup()
    {
        Assert.Equal(
        [
            "4:0:formBinding struct",
            "5:0:Handler interface",
            "6:1:Serve() error",
            "8:0:ID int",
            "11:0:type Single struct",
        ], Outline("a.go", """
            package a

            type (
            	formBinding struct{}
            	Handler     interface {
            		Serve() error
            	}
            	ID int
            )

            type Single struct {
            	A int
            }
            """));
    }

    /// <summary>
    /// C: a return type written as a macro (<c>CJSON_PUBLIC(void)</c>) and a typedef are
    /// declarations; a variable of a struct type and a function-pointer variable are not.
    /// </summary>
    [Fact]
    public void C_MacroReturnTypesAndTypedefs_ButNotVariables()
    {
        Assert.Equal(
        [
            "1:0:typedef SRWLOCK cxp_lock",
            "2:0:typedef struct cJSON_Hooks",
            "5:0:CJSON_PUBLIC(void) cJSON_free(void *object)",
        ], Outline("cjson.c", """
            typedef SRWLOCK cxp_lock;
            typedef struct cJSON_Hooks {
                void *(*malloc_fn)(size_t sz);
            } cJSON_Hooks;
            CJSON_PUBLIC(void) cJSON_free(void *object)
            {
            }
            struct sigaction sa;
            static void (*oldHandler)(int, siginfo_t*, void*);
            """));
    }

    /// <summary>RUBY'S OPERATOR METHODS are methods: <c>def [](key)</c> is how a hash-like class reads.</summary>
    [Fact]
    public void Ruby_OperatorMethods()
    {
        Assert.Equal(
        [
            "1:0:class IndifferentHash < Hash",
            "2:1:def [](key)",
            "5:1:def []=(key, value)",
            "8:1:def ==(other)",
        ], Outline("hash.rb", """
            class IndifferentHash < Hash
              def [](key)
                super(convert_key(key))
              end
              def []=(key, value)
                super
              end
              def ==(other)
                true
              end
            end
            """));
    }

    /// <summary>
    /// A STRING SPANNING LINES, where the language allows one and this reads it — Rust, PHP — is a
    /// string to its end; in Ruby the line ends it, and still hides nothing after. Read as code past its first line, a help text's braces would hide every declaration after it.
    /// </summary>
    [Theory]
    [InlineData("a.rs", "const T: &'static str = \"\n_rg() {\n  local i\n\";\n\npub(crate) fn generate() -> String {\n}\n", "6:0:pub(crate) fn generate() -> String")]
    [InlineData("b.rs", "const T: &str = r#\"\n{ \"quoted\" }\n\"#;\n\nfn after() {\n}\n", "5:0:fn after()")]
    [InlineData("a.php", "<?php\n$sql = \"select {\n  from x\";\nfunction after()\n{\n}\n", "4:0:function after()")]
    [InlineData("a.rb", "HELP = \"usage {\n  more\"\ndef after\nend\n", "3:0:def after")]
    public void AStringAcrossLines_HidesNothingAfterIt(string path, string source, string expected)
    {
        Assert.Contains(expected, Outline(path, source));
    }

    /// <summary>
    /// A RUBY REGEX WITH QUOTES IN IT hides nothing after it: sinatra's header pattern has three
    /// double quotes, and read as a string that runs on, it would swallow the whole class below it.
    /// </summary>
    [Fact]
    public void Ruby_ARegexWithQuotes_HidesNothingAfterIt()
    {
        Assert.Equal(["1:0:class Request", "3:1:def accept", "5:1:def safe?"], Outline("base.rb", """
            class Request
              HEADER_PARAM = /\s*[\w.]+=(?:[\w.]+|"(?:[^"\\]|\\.)*")?\s*/.freeze
              def accept
              end
              def safe?
              end
            end
            """));
    }

    /// <summary>A C# INDEXER is a member, named for what it is: <c>this[…]</c>.</summary>
    [Fact]
    public void CSharp_AnIndexer_IsAMember()
    {
        Assert.Equal(["1:0:class Bag", "3:1:public object this[string key]", "4:1:protected internal object this[int index]"], Outline("Bag.cs", """
            class Bag
            {
                public object this[string key] => _items[key];
                protected internal object this[int index]
                {
                    get { return null; }
                }
            }
            """));
    }

    /// <summary>A C TYPEDEF OF A FUNCTION POINTER names a type: <c>cxp_tool_fn</c> is what a plugin's tools are.</summary>
    [Fact]
    public void C_AFunctionPointerTypedef_IsAType()
    {
        Assert.Equal(["1:0:typedef cxp_result (*cxp_tool_fn)(const cxp_call *call)"], Outline("cxp.h", """
            typedef cxp_result (*cxp_tool_fn)(const cxp_call *call);
            static int (*handler)(int);
            """));
    }

    /// <summary>
    /// SCALA, as cats writes it: generics in brackets, a def with no parameter list, several
    /// parameter lists, a body after <c>=</c> on the next line, <c>case class</c>, <c>object</c>,
    /// <c>trait</c> and type aliases. An anonymous class inside a def's body stays hidden.
    /// </summary>
    [Fact]
    public void Scala_DefsValsAndTypes()
    {
        Assert.Equal(
        [
            "3:0:final case class Kleisli[F[_], -A, B](run: A => F[B])",
            "4:1:def ap[C, D](f: Kleisli[F, C, B => D])(implicit F: Apply[F]): Kleisli[F, C, D]",
            "7:1:def dimap[C, D](f: C => A)(g: B => D)(implicit F: Functor[F]): Kleisli[F, C, D]",
            "8:1:val first: Int",
            "9:1:private[data] def local: String",
            "12:0:object Kleisli extends KleisliInstances",
            "13:1:implicit def monoid[F[_], A, B](implicit FB0: Monoid[F[B]]): Monoid[Kleisli[F, A, B]]",
            "15:1:type Id[A]",
            "18:0:sealed trait Validated[+E, +A]",
        ], Outline("Kleisli.scala", """
            package cats.data

            final case class Kleisli[F[_], -A, B](run: A => F[B]) { self =>
              def ap[C, D](f: Kleisli[F, C, B => D])(implicit F: Apply[F]): Kleisli[F, C, D] =
                Kleisli { a => F.ap(f.run(a))(run(a)) }

              def dimap[C, D](f: C => A)(g: B => D)(implicit F: Functor[F]): Kleisli[F, C, D] = ???
              val first: Int = 1
              private[data] def local: String = "x"
            }

            object Kleisli extends KleisliInstances {
              implicit def monoid[F[_], A, B](implicit FB0: Monoid[F[B]]): Monoid[Kleisli[F, A, B]] =
                new KleisliMonoid[F, A, B] { def FB: Monoid[F[B]] = FB0 }
              type Id[A] = A
            }

            sealed trait Validated[+E, +A]
            """));
    }

    /// <summary>
    /// DART, as package:http writes it: <c>mixin class</c>, members with an <c>=&gt;</c> body,
    /// getters and setters, named and factory constructors, mixins and extensions.
    /// </summary>
    [Fact]
    public void Dart_ClassesMixinsExtensionsAndTheirMembers()
    {
        Assert.Equal(
        [
            "1:0:abstract mixin class BaseClient implements Client",
            "3:1:Future<Response> head(Uri url, {Map<String, String>? headers})",
            "6:1:String get name",
            "7:1:set name(String value)",
            "8:1:BaseClient.named(this.x)",
            "9:1:factory BaseClient.create()",
            "13:0:mixin Retry on BaseClient",
            "14:1:void retry()",
            "17:0:extension Bytes on Response",
            "18:1:List<int> get bytes",
        ], Outline("base_client.dart", """
            abstract mixin class BaseClient implements Client {
              @override
              Future<Response> head(Uri url, {Map<String, String>? headers}) =>
                  _sendUnstreamed('HEAD', url, headers);

              String get name => 'base';
              set name(String value) {}
              BaseClient.named(this.x);
              factory BaseClient.create() => _Impl();
              final int x;
            }

            mixin Retry on BaseClient {
              void retry() {}
            }

            extension Bytes on Response {
              List<int> get bytes => [];
            }
            """));
    }

    /// <summary>
    /// OBJECTIVE-C, as AFNetworking writes it: <c>@interface</c> and <c>@implementation</c> blocks
    /// that end at <c>@end</c> rather than a brace, properties, and methods that start with
    /// <c>-</c> or <c>+</c> and wrap their selector across lines.
    /// </summary>
    [Fact]
    public void ObjectiveC_InterfacesImplementationsPropertiesAndMethods()
    {
        Assert.Equal(
        [
            "4:0:@interface AFHTTPSessionManager : AFURLSessionManager <NSSecureCoding, NSCopying>",
            "6:1:@property (readonly, nonatomic, strong, nullable) NSURL *baseURL",
            "8:1:+ (instancetype)manager",
            "9:1:- (instancetype)initWithBaseURL:(nullable NSURL *)url sessionConfiguration:(nullable NSURLSessionConfiguration *)configuration",
            "14:0:@implementation AFHTTPSessionManager",
            "16:1:- (instancetype)initWithBaseURL:(NSURL *)url",
        ], Outline("AFHTTPSessionManager.m", """
            @class AFSecurityPolicy;
            @protocol AFMultipartFormData;

            @interface AFHTTPSessionManager : AFURLSessionManager <NSSecureCoding, NSCopying>

            @property (readonly, nonatomic, strong, nullable) NSURL *baseURL;

            + (instancetype)manager;
            - (instancetype)initWithBaseURL:(nullable NSURL *)url
                       sessionConfiguration:(nullable NSURLSessionConfiguration *)configuration;

            @end

            @implementation AFHTTPSessionManager

            - (instancetype)initWithBaseURL:(NSURL *)url {
                self = [super init];
                return self;
            }

            @end
            """));
    }

    /// <summary>A SCALA PACKAGE OBJECT holds a package's shared types and functions — cats keeps dozens there.</summary>
    [Fact]
    public void Scala_APackageObjectsMembers()
    {
        Assert.Equal(["2:0:package object data extends ScalaVersionSpecificPackage", "3:1:type ValidatedNel[+E, +A]", "4:1:val NonEmptyMap"],
            Outline("package.scala", """
                package cats
                package object data extends ScalaVersionSpecificPackage {
                  type ValidatedNel[+E, +A] = Validated[NonEmptyList[E], A]
                  val NonEmptyMap = NonEmptyMapImpl
                }
                """));
    }

    /// <summary>DART'S <c>async</c>, <c>async*</c> AND <c>sync*</c> mark the body, not the declaration.</summary>
    [Fact]
    public void Dart_AnAsyncMarker_IsNotPartOfTheSignature()
    {
        Assert.Equal(["1:0:class Client", "2:1:Future<String> read(Uri url)", "3:1:Stream<int> ticks()"], Outline("client.dart", """
            class Client {
              Future<String> read(Uri url) async {}
              Stream<int> ticks() async* {}
            }
            """));
    }

    /// <summary>
    /// DART'S TOP-LEVEL VALUES are a library's API — status codes, a shared codec — shown up to
    /// their <c>=</c>, as TypeScript's exported consts are. A field inside a class is not listed.
    /// </summary>
    [Fact]
    public void Dart_TopLevelFinalAndConstValues()
    {
        Assert.Equal(["1:0:const normalClosure", "2:0:final HuffmanCodec http2HuffmanCodec", "6:0:class Codec"], Outline("status.dart", """
            const normalClosure = 1000;
            final HuffmanCodec http2HuffmanCodec = HuffmanCodec(
              HuffmanEncoder(_codeWords),
              HuffmanDecoder(generateHuffmanTree(_codeWords)),
            );
            class Codec {
              final int size = 1;
            }
            """));
    }
}
