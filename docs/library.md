# Library

The CLI is a thin formatter over this API: everything it prints comes from the calls below.

## Getting started

```sh
dotnet new console -o Drill && cd Drill    # net10.0 or newer, run with a .NET 10+ SDK installed
dotnet add package SharpDrill.Roslyn       # brings SharpDrill.Core; add SharpDrill.Export.Mermaid for diagrams
```

The input must be restored (`dotnet restore MyApp.slnx`); the library never restores or builds it. Then in `Program.cs`:

```csharp
using SharpDrill.Core;
using SharpDrill.Roslyn;

var program = await RoslynProgramLoader.OpenAsync("MyApp.slnx");    // load + index; restore the input first

foreach (var entry in program.EntryPoints())                         // actions, Main, a library's public API
    Console.WriteLine($"{entry.Kind}  {entry.Method.DisplaySignature}");

var report = program.Exceptions();                                   // every entry point, defaults
foreach (var escape in report.Uncaught)
    Console.WriteLine($"{escape.ExceptionType} from {escape.Thrown.Evidence.Path}:{escape.Thrown.Evidence.StartLine}"
        + $" escapes {string.Join(", ", escape.EscapesFrom.Select(x => x.DisplaySignature))}");
```

`dotnet run` prints the entry points and each exception that may escape them. The SDK's MSBuild is located on first
load (`MSBuildBootstrap.RegisterSdk()` does it explicitly, before any MSBuild type is touched).

`program` is a `ProgramIndex`: the immutable facts of one load plus lookups. Build it once; every query reads it.
`RoslynProgramLoader.LoadAsync(new AnalysisRequest(path, configuration, framework, ...))` gives the raw `ProgramSnapshot`
when you need load options (`new ProgramIndex(snapshot)` then indexes it).

## Entry points

`program.EntryPoints()` returns `EntryPoint(Method, Kind)`, ordered by signature:

| Kind | What |
| --- | --- |
| `ControllerAction` | Public instance methods of `ControllerBase` descendants or `[Controller]` types, minus `[NonAction]`. |
| `Main` | A static `Main` the compiler accepts, or top-level statements (`Program.<Main>$`). |
| `PublicApi` | Public methods, constructors and accessors of public types in a library project that no loaded project references, and that has no actions or `Main` of its own. |

`program.EntryPoint("OrdersController.Get")` finds one by ID, exact signature or a unique case-insensitive part of a
signature, and throws `ArgumentException` listing candidates otherwise.

## Exceptions

`program.Exceptions(options)` returns an `ExceptionReport`. Each view is computed on first read and shares one analysis.

| `ExceptionOptions` | Default | |
| --- | --- | --- |
| `From` | null (all) | One entry point, as for `EntryPoint(name)`. |
| `FollowImplementations` | true | Follow interface/virtual calls into source implementations (marked as assumed). |
| `ThirdPartyCalls` | false | Fill `UnguardedCalls`. |
| `Budget` | 100000 | Work limit; `BudgetExhausted` says when it ran out. |

| `ExceptionReport` | Items |
| --- | --- |
| `Uncaught` | `UncaughtException(Thrown, Rethrown?, ExceptionType, EscapesFrom, GlobalHandlers, Example)`, global-handler ones last. |
| `Crashes` | `CrashingException(Thrown, Rethrown?, ExceptionType?, AsyncVoidMethod, EscapesFrom, Example)`. |
| `Caught` | `CaughtException(Thrown, Catch, IsCertain, Example)`. |
| `Miscaught` | `MiscaughtException(Thrown, ExceptionType?, MissedBy, Clauses, Then, EscapesFrom, GlobalHandlers, Example)` with `Undetermined`. |
| `UnguardedCalls` | `UnguardedCall(Call, EscapesFrom, Example)`. |
| `Thrown` | Every explicit `ThrowSiteFact` in source. |
| `NotFollowed` | `NotFollowed(Reason, Places)`: where the analysis stopped, by `ExceptionReason`. |
| `EntryPoints`, `IsComplete`, `BudgetExhausted` | |

Every finding carries resolved facts (`ThrowSiteFact`, `CatchFact`, `CallSiteFact`, `CallableFact`), each with
`SourceEvidence` (path, line, column, checksum). `Example` is an `ExceptionPath`: `Steps` from the entry point down, each a
`PathStep(Method, Call, Implementations)` with `IsAssumed` when it follows an interface or virtual call; `Locations` for
the evidence along it; `IsAssumed` when any step did.

## Diagrams, projects and graphs

```csharp
using SharpDrill.Export.Mermaid;

var entry = program.EntryPoint("OrdersController.Get").Method.Id;
var diagram = CallDiagramQuery.Build(program, entry, new DiagramOptions());   // nodes, edges, boundaries, limits
var markdown = MermaidExporter.Markdown(diagram);
var projects = ProjectMap.Build(program.Snapshot);                             // MSBuild reference tree and settings

var calls = program.GetCallGraph();             // SharpDrill.Graph: edges per call site, Direct/Declared/Candidate
var types = program.GetDependencyGraph();
```

`CallGraph.Resolve(site)` says what one call site leads to (source, library, dispatch candidates, unresolved), the same
answer every query uses. `LayerModel`/`LayerMap`/`LayerGraph` and `LayerRules.AllowedDependencies` check your own layer
mapping; a rule is a plain function returning `Finding(RuleId, Verdict, Subject, Message, Evidence)`, with
`Verdict.Unknown` kept apart from `Pass`.

## Facts and identity

`ProgramSnapshot` holds plain immutable records (`Facts.cs`): compilations, types, callables, call sites, throws,
catches, try regions, implementation candidates, dependencies, global-handler registrations, suppressions and gaps. It
has no Roslyn objects and serializes to JSON. A call site records its declared target only; `ImplementationFact`s are
candidates, never claims about what runs. IDs (`CallableId`, `ThrowSiteId`, ...) are local to one load; `Key` is the
portable identity (`<project>|<documentation ID>`).
