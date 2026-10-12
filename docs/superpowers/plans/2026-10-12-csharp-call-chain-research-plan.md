# C# Semantic Library and Call-Chain CLI — Codex Implementation Plan

> For agentic workers: use superpowers:executing-plans to implement this plan task by task. The user has chosen C# and requested stages 1–5 as a small research baseline. This document supplies the design and execution instructions; creating it does not execute the implementation. Do not delegate to subagents unless the user authorises that separately.

**Goal:** Create a small .NET semantic library and console application that load existing C# code and print method call chains using ` --> `, with source evidence and visible analysis limitations.

**Architecture:** Three production projects: a Roslyn-independent model/query library, a Roslyn loading/extraction library, and a thin CLI. Extract immutable source-backed facts once, then run typed queries over them. Keep extraction, bounded analysis and formatting separate; include explicit-origin exception propagation in the first increment.

**Tech stack:** C#, .NET 10 host, Roslyn CSharp.Workspaces and Workspaces.MSBuild, Microsoft.Build.Locator, System.Collections.Immutable, System.Text.Json, xUnit. Pin compatible stable versions during scaffold creation after inspecting the available SDK and package documentation.

**Spec:** The design and acceptance contract in this document. This supersedes the initial `2026-10-12-semantic-analysis-foundation.md` plan for the first implementation.

## Updated acceptance contract — 2026-10-12

This section takes precedence over older stage details below. The user has requested implementation and autonomous routine decisions; execute inline in the existing checkout without further design gates or delegation.

**Purpose:** investigate ASP.NET Core controller actions, their static calls through user-configured layers, and explicit exceptions that might reach those actions. Escaping an action is never called globally unhandled.

**Increment order:**
1. Immutable semantic model and small three-project ASP.NET fixture; test infrastructure.
2. SDK workspace loading and semantic extraction, including controller identification, throws, catches, protected regions, await observation and boundary registrations.
3. Bounded deterministic call paths and explicit-origin exception analysis, tested against real fixture extraction.
4. CLI commands, integration checks and documented assumptions/sample reports.

**Model additions:** call/throw IDs reference ordered enclosing protected-region IDs. Catch facts retain caught static type, filter text, body evidence and conservative handler outcome (intercepts, rethrows, replaces, unknown). Regions retain try evidence, ordered catches and finally uncertainty. Throw facts distinguish static type from known exact construction type. Task-producing calls retain direct-await evidence. Boundary evidence records semantically bound UseExceptionHandler, AddExceptionHandler and UseDeveloperExceptionPage registrations separately. It does not prove registration order or runtime coverage.

**Controller convention:** public instance ordinary methods declared on source classes semantically deriving from ASP.NET Core ControllerBase or carrying the Controller attribute; exclude NonController, NonAction, static, generic and nonpublic methods. ASP.NET route/HTTP attribute evidence is retained. Inherited action enumeration and minimal APIs are outside this initial supported set and reported as coverage limits.

**Exception algorithm:** traverse declared call relationships from actions within depth/output/work budgets. Analyse explicit throw origins along each witness by unwinding lexical protected regions in the origin and at each caller site. Never borrow catches from unrelated calls. Match catch types using semantic type ancestry. Nonexact thrown values, conditional filters, uncertain dispatch, unavailable bodies, unsupported regions and asynchronous observations yield scoped Unknown when they prevent a decision. A rethrow retains the original exception and handler evidence; a simple unconditional replacement continues with the replacement origin/type. Conditional/complex handler flow is Unknown. Direct awaited calls can propagate explicit origins; calling without observing a task is Unknown. This structural may-analysis does not prove branch feasibility or model implicit exceptions.

**CLI additions:** `controllers` prints paths from action roots; `exceptions` prints the complete throw/catch landscape plus per-action origin witnesses. Both accept ordinary traversal options. Repeatable `--layer project:<prefix>=<name>` or `--layer namespace:<prefix>=<name>` maps layers explicitly, using longest prefix and project preference on ties. No naming heuristic defines architectural truth. `inspect --format json` serialises all facts in schema version 2.

**Fixture acceptance:** controller → application → infrastructure; caught caller exception, unrelated protected call, incompatible catch, rethrow, replacement, conditional filter, interface dispatch, recursion, directly awaited/caught operation, unobserved task, throw expression, static-type throw and action escape with separate boundary evidence.

**Validation:** meaningful extraction, graph-budget, exception and fresh-process CLI tests; SDK build and full test run; restore only repository-owned fixtures explicitly. Preserve source evidence and all call sites. User-code experiment remains pending unless an input is supplied; analysing Sharpview itself is a loader smoke check only.

**Scope decisions:** reuse existing projects and retain the public model/query naming from section 5 where useful. Consolidate test projects and related small fact files to keep the research increment small. Defer optional local-tool packaging and editor tasks until the research behaviour is established. Do not commit/push/publish automatically; leave a reviewable workspace diff.

**Execution update:** the initial checkout already contains partial model, extraction, query and fixture work. Preserve and complete that work. The user subsequently authorised .NET skills and subagents for quality: assign disjoint extraction/loading, core analysis/tests, and review tasks while completing CLI formatting and integration in the primary agent. This overrides the earlier no-delegation guidance. Validate exception replacement followed by an outer handler, unsupported async observations and declared dispatch boundaries before claiming the research increment complete.

**Implementation record:** the four updated increments above are implemented in the existing three production projects, with one shared test project. The repository fixture builds with the SDK; host build has zero warnings/errors; the expanded suite has 74 passing tests. CLI sample reports, model/coverage notes and requirement-to-test evidence are in `docs/samples`, `docs/model.md`, `docs/coverage.md` and `docs/validation.md`. Loading `.csproj`, `.sln` and `.slnx`, output safeguards and no implicit restore are tested. Missing assets currently fail the whole selected input rather than returning partial compilations; requested frameworks apply across selected projects. Optional packaging, editor tasks and a separate generator fixture remain deferred. Sharpview self-analysis is only a smoke check; the external Web API experiment is pending input selection. Changes remain uncommitted for review.

## 1. What success looks like

From a terminal or VS Code terminal:

```sh
dotnet run --project src/Sharpview.Cli -- chains tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.Controller.Get()'
```

Expected stdout includes:

```text
Research.Api.Controller.Get() --> Research.Application.Service.Load() --> Research.Persistence.Repository.Read() --> Research.Persistence.Database.Execute()
```

The same operation must be possible through a C# library call. The CLI must not contain graph traversal or extraction logic.

Also support:

```sh
dotnet run --project src/Sharpview.Cli -- methods ./ExistingSolution.slnx
dotnet run --project src/Sharpview.Cli -- chains ./ExistingSolution.slnx
dotnet run --project src/Sharpview.Cli -- inspect ./ExistingSolution.slnx --format json --output snapshot.json
```

`methods` lists source callables and identifiers. `chains` without a root attempts chains starting at every source callable, in deterministic order and within explicit budgets. This ensures mutually recursive components are included even when they have no incoming-edge-free root.

The first release is a research tool to inspect semantics. No NuGet publication, public repository creation, marketplace extension or deployment is included.

## 2. Global constraints

- C# is the chosen implementation language. No F# project in this increment.
- All build/test/run commands use the .NET SDK. No Visual Studio installation, VSIX, DTE, Visual Studio SDK or proprietary tooling dependency for supported inputs.
- The analyser host targets .NET 10. Input projects may target other frameworks when their installed SDK/reference packs support them.
- Support SDK-style C# `.csproj`, `.sln` and `.slnx` inputs. Test `.slnx` loading with the selected Roslyn/MSBuild versions; upgrade compatible dependencies if needed. Legacy .NET Framework project systems and non-C# source analysis are outside the tested input contract.
- Input dependencies must already be restored. Do not automatically run restore/build on user input or write to analysed source files. Repository fixture setup may explicitly restore its own projects.
- Project evaluation and source generation may execute project-supplied tooling. Never describe analysis of untrusted projects as sandboxed or as simple text parsing.
- Analyse one explicit compilation variant per selected project. Record configuration and target framework; do not silently combine variants.
- Consumers use IDs and typed facts, not Roslyn symbols or syntax trees.
- Retain declaration/source evidence and call sites even if output collapses repeated method relationships.
- Every unresolved/unsupported scenario is represented by a scoped gap or an explicit call-site status. Metadata-only body absence is a normal boundary, not a project-load failure.
- No database, general graph framework, custom rule language, plugin infrastructure, interprocedural data-flow engine or editor extension.
- Implement only the five stages in this plan. VS Code terminal use works immediately; editor tasks can be added as a small part of Stage 5.
- Implement in the existing Sharpview repository (`/home/stan/source/repos/sharpal` on the current machine). Never change synced `sources/` files in the separate ChatGPT reference mirror.

## 3. Call-chain semantics — implement this exactly

### 3.1 A chain is a static relationship path

If `A()` calls `B()` and `C()` and `B()` calls `D()`, print:

```text
A() --> B() --> D()
A() --> C()
```

Do not print `A() --> B() --> C()`: that invents a call relationship. Output does not claim runtime execution order, path feasibility, invocation count or stack depth. Calls in mutually exclusive/unreachable branches remain structural facts for now.

### 3.2 Default traversal: declared bindings

Default `--dispatch declared` follows the member to which Roslyn binds each supported call. This is useful for inspecting interfaces without pretending to know runtime implementations.

```text
Controller.Get() --> Service.Load() --> IRepository.Read() [interface target]
```

Interface and virtual targets are terminal boundaries in this first traversal, even if the declared virtual method has a body. Do not follow override candidates or accidentally present the base body as the body that executes.

Optional `--dispatch direct` follows only sites whose dispatch is classified direct. Emit a terminal marker for excluded dispatch rather than dropping the branch silently:

```text
Service.Load() --> [interface call: IRepository.Read()]
```

Classification rules:
- Static/nonvirtual calls, constructors and explicit `base.Member()` calls are direct.
- Interface calls are interface dispatch.
- Virtual/override calls are virtual unless a clearly tested rule establishes direct dispatch; first increment need not optimise sealed cases.
- Delegate invocation is delegate dispatch; do not follow `Func.Invoke` as if it resolves the delegate target.
- Dynamic invocation is dynamic dispatch.
- Function-pointer invocation is an unsupported target boundary initially.
- Ordinary extension calls are direct calls to the static extension declaration; normalise reduced symbols correctly.

### 3.3 Termination and limits

Enumerate maximal paths under the selected policy, ending at:
- a source callable with no outgoing supported call facts;
- a metadata-only target;
- interface/virtual/delegate/dynamic/unsupported dispatch;
- an already visited callable on the current path;
- the configured depth limit;
- a global work/output budget.

Use path-local cycle detection. A global visited set would wrongly hide valid shared subpaths.

```text
A() --> B() --> A() [cycle]
A() --> B() [max depth]
A() --> External.Library.Read() [metadata body unavailable]
A() --> [dynamic invocation]
Leaf() [no outgoing calls]
```

`--max-depth` counts call transitions, not number of printed callable names. At zero, emit the root with a depth marker only if further traversal exists. Default 20.

Default `--max-chains` is 1,000 across the entire command. Default `--max-expansions` is 100,000 examined outgoing call sites across all roots. A budget stop must produce a stderr summary and a nonzero completion status. Never promise exhaustive enumeration: simple path counts can grow exponentially.

### 3.4 Deduplication and ordering

- Preserve all original call sites in the snapshot.
- For chain output, collapse duplicate relationships with the same owner, bound target and dispatch kind. Keep all supporting call-site IDs on each collapsed edge.
- Never collapse distinct unresolved sites solely because both have a null target.
- Sort roots and outgoing edges by canonical display signature, then ID/source span. Sort before traversal, not after materialising an unbounded result.
- Use qualified signatures with parameter types so overloads are distinguishable. If distinct assemblies contain identical display signatures, append assembly disambiguation.
- Default includes constructors/accessors as labelled callable kinds. `methods` documents that it lists executable callables, including local functions/lambdas.
- Initialisers/top-level statements without a modelled callable owner produce a scoped unsupported gap. Do not invent false caller ownership.

## 4. Repository and file responsibilities

Use the existing `Sharpview` name and `sharpview.slnx` solution. Preserve the current Core, Roslyn and CLI projects; do not recreate them.

```text
global.json                         # Selected installed stable SDK
Directory.Build.props               # net10.0, nullable, deterministic
Directory.Packages.props            # Pinned compatible package versions
sharpview.slnx
src/Sharpview.Core/
  Model/Identifiers.cs
  Model/Declarations.cs
  Model/UsageFacts.cs
  Model/CompilationFact.cs
  Model/ProgramSnapshot.cs
  Queries/CallIndex.cs
  Queries/CallChainQuery.cs
  Queries/CallChainResult.cs
  Queries/DependencyQueries.cs
src/Sharpview.Roslyn/
  Loading/AnalysisRequest.cs
  Loading/MSBuildBootstrap.cs
  Loading/WorkspaceLoader.cs
  RoslynProgramLoader.cs
  Identity/DeclarationIdentity.cs
  Extraction/DeclarationExtractor.cs
  Extraction/OperationExtractor.cs
  Extraction/SourceEvidenceFactory.cs
src/Sharpview.Cli/
  Program.cs
  Arguments.cs
  Commands.cs
  Formatting/ChainTextWriter.cs
  Formatting/SnapshotJsonWriter.cs
tests/Sharpview.Core.Tests/
  CallChainTests.cs
  DependencyQueryTests.cs
tests/Sharpview.Roslyn.Tests/
  LoadingTests.cs
  IdentityTests.cs
  ExtractionTests.cs
tests/Sharpview.Cli.Tests/
  CliProcessTests.cs
tests/Fixtures/ResearchSolution/
  ResearchSolution.slnx
  Research.Api/
  Research.Application/
  Research.Persistence/
tests/Fixtures/MultiTarget/
tests/Fixtures/GeneratedSource/
docs/model.md
docs/coverage.md
README.md
.vscode/tasks.json
```

Create no additional production projects without a demonstrated requirement. Keep rules as ordinary C# functions over facts; a rule interface/scheduler is unnecessary for the first experiment.

## 5. Public API contract

Use these types and signatures consistently. Implement records with immutable collections. Adapt additional private helpers freely.

```csharp
public readonly record struct CompilationId(string Value);
public readonly record struct TypeId(string Value);
public readonly record struct CallableId(string Value);
public readonly record struct CallSiteId(string Value);

public enum CallableKind { Method, Constructor, Getter, Setter, EventAdder, EventRemover, LocalFunction, Lambda }
public enum DispatchKind { Direct, Interface, Virtual, Delegate, Dynamic, Unknown }
public enum BodyStatus { Source, MetadataOnly, NoBody, Unsupported }
public enum NullabilityKind { NotAnnotated, Annotated, Oblivious }
public enum DependencyKind { Signature, Inheritance, Constraint, Attribute, Construction, BodyUse }
public enum ThrowKind { Explicit, Rethrow }
public enum GapKind { ProjectLoad, Compilation, Binding, Unsupported, ExcludedSource }

public sealed record SourceEvidence(
    string Path, string Checksum, int SpanStart, int SpanLength,
    int StartLine, int StartColumn, bool IsGenerated);

public sealed record TypeUse(
    TypeId? Declaration, string DisplayName, NullabilityKind Nullability,
    ImmutableArray<TypeUse> Components);

public sealed record ParameterFact(string Name, TypeUse Type, string RefKind);

public sealed record TypeFact(
    TypeId Id, CompilationId Compilation, string DisplayName,
    string Accessibility, ImmutableArray<TypeUse> BaseTypes,
    ImmutableArray<string> Attributes, ImmutableArray<SourceEvidence> Declarations);

public sealed record CallableFact(
    CallableId Id, CompilationId Compilation, TypeId? DeclaringType,
    CallableId? ContainingCallable, CallableKind Kind, string DisplaySignature,
    string Accessibility, TypeUse ReturnType,
    ImmutableArray<ParameterFact> Parameters,
    ImmutableArray<string> Attributes, BodyStatus Body,
    ImmutableArray<SourceEvidence> Declarations);

public sealed record CallSiteFact(
    CallSiteId Id, CallableId Caller, CallableId? BoundTarget,
    string TargetDisplay, DispatchKind Dispatch,
    ImmutableArray<TypeUse> TypeArguments,
    SourceEvidence Evidence, bool IsImplicit);

public sealed record DependencyFact(
    TypeId? OwningType, CallableId? OwningCallable,
    TypeUse ReferencedType, DependencyKind Kind, SourceEvidence? Evidence);

public sealed record ThrowSiteFact(
    CallableId Owner, ThrowKind Kind, TypeUse? StaticExceptionType,
    SourceEvidence Evidence);

public sealed record AnalysisGap(
    GapKind Kind, CompilationId? Compilation, CallableId? Owner,
    string Message, SourceEvidence? Evidence);

public sealed record CompilationFact(
    CompilationId Id, string ProjectPath, string AssemblyIdentity,
    string TargetFramework, string Configuration, string LanguageVersion,
    string NullableContext, ImmutableArray<string> PreprocessorSymbols,
    ImmutableArray<string> Diagnostics);

public sealed record ProgramSnapshot(
    ImmutableArray<CompilationFact> Compilations,
    ImmutableArray<TypeFact> Types,
    ImmutableArray<CallableFact> Callables,
    ImmutableArray<CallSiteFact> CallSites,
    ImmutableArray<DependencyFact> Dependencies,
    ImmutableArray<ThrowSiteFact> ThrowSites,
    ImmutableArray<AnalysisGap> Gaps);
```

`TypeUse.Components` holds generic arguments, array element types or pointer element types as appropriate; `DisplayName` retains the type shape. Type parameters can have a null declaration and a qualified display name. This is a lightweight initial type-use model, not a complete type algebra. Use `void` as a type use for constructors/setters where appropriate.

Attribute strings are canonical attribute type names, not an interpreted contract language. Preserve nullable annotations now; conditional nullable attribute arguments and interprocedural interpretation are future work. Document this limitation.

Source spans are zero-based; displayed lines/columns are one-based. No mutable aliases or live Roslyn objects survive in the snapshot. Compile-time error diagnostics also produce appropriate compilation gaps; warnings alone do not make extraction incomplete.

Roslyn adapter:

```csharp
public sealed record AnalysisRequest(
    string InputPath,
    string Configuration = "Debug",
    string? TargetFramework = null,
    bool IncludeGenerated = true);

public static class MSBuildBootstrap
{
    public static void RegisterSdk();
}

public static class RoslynProgramLoader
{
    public static Task<ProgramSnapshot> LoadAsync(
        AnalysisRequest request, CancellationToken cancellationToken = default);
}
```

Register MSBuild explicitly in the CLI before invoking loader code that can load MSBuild assemblies. Library callers must call `RegisterSdk()` once before loading; it must be idempotent for a compatible registration and reject conflicts clearly. Document the SDK discovery/global.json policy rather than claiming it always matches a shell's chosen SDK automatically.

Query contract:

```csharp
public enum ChainDispatchPolicy { Declared, Direct }
public enum ChainEnd { Leaf, Metadata, NoBody, DispatchBoundary, UnknownTarget, Cycle, DepthLimit }

public sealed record ChainOptions(
    ChainDispatchPolicy Dispatch = ChainDispatchPolicy.Declared,
    int MaxDepth = 20, int MaxChains = 1000, int MaxExpansions = 100000);

public sealed record ChainStep(
    CallableId Caller, CallableId? Target, string TargetDisplay,
    DispatchKind Dispatch, ImmutableArray<CallSiteId> EvidenceSites);

public sealed record CallChain(
    CallableId Root, ImmutableArray<ChainStep> Steps, ChainEnd End);

public sealed record ChainSearchResult(
    ImmutableArray<CallChain> Chains, bool OutputLimitReached,
    bool WorkLimitReached, int ExpandedSites);

public static class CallChainQuery
{
    public static ChainSearchResult Enumerate(
        ProgramSnapshot snapshot, ImmutableArray<CallableId> roots,
        ChainOptions options, CancellationToken cancellationToken = default);
}

public static class DependencyQueries
{
    public static IEnumerable<DependencyFact> SignatureUses(
        ProgramSnapshot snapshot, CallableId callable);
}
```

If an explicit root is interface/virtual with a source body, it may be inspected as that declaration's own body; calls reaching it through uncertain dispatch remain boundaries. Describe this distinction in help.

## 6. Fixture contract

Create fixture source intentionally simple enough that output can be checked by inspection. Use static direct calls for the main chain; avoid implicit instance constructors contaminating the principal acceptance example.

```csharp
// Research.Api, references Application
namespace Research.Api;
public static class Controller
{
    public static string Get() => Research.Application.Service.Load();
}

// Research.Application, references Persistence
namespace Research.Application;
public static class Service
{
    public static string Load() => Research.Persistence.Repository.Read();
}

// Research.Persistence
namespace Research.Persistence;
public static class Repository
{
    public static string Read() => Database.Execute();
}
public static class Database
{
    public static string Execute() => "result";
}
```

Add focused fixture types:

```csharp
public static class Branches
{
    public static void Root() { Left(); Right(); }
    public static void Left() => Tail();
    public static void Right() { }
    public static void Tail() { }
    public static void Twice() { Left(); Left(); }
}
public static class Recursion
{
    public static void A() => B();
    public static void B() => A();
}
public interface IRepository { string Read(); }
public static class InterfaceConsumer
{
    public static string Load(IRepository repository) => repository.Read();
}
public static class Failures
{
    public static void Throw() => throw new InvalidOperationException("fixture");
    public static void Rethrow()
    {
        try { Throw(); }
        catch (InvalidOperationException) { throw; }
    }
}
```

Add overloads, extension methods, generic methods, partial declarations, a local function, a lambda with an internal call, a getter/setter and an event accessor in separate source files. For nullable/type dependencies include a public signature exposing `List<PersistenceType?>` to validate nested type uses.

Generate a separate source-generator fixture using a minimal local generator project. It emits one method calling another generated method; inclusion/exclusion and evidence must be tested. Do not require network framework packages for the main fixture.

## 7. Stage 1 — repository, model and fixtures

Scaffold already present: `Sharpview.Core`, `Sharpview.Roslyn` and `Sharpview.Cli`, with CLI → Core/Roslyn and Roslyn → Core references. SDK pinning, nullable settings and `sharpview.slnx` already exist. Review and extend them; do not scaffold replacements. Roslyn NuGet dependencies and extraction implementation remain work for the stages below.

**Files:** scaffold and model files listed in Section 4, fixture projects, `docs/model.md` and model tests.

**Deliverable:** buildable library contracts and a reproducible three-project input solution.

- [ ] Inspect the normal repository, its `AGENTS.md`, installed SDKs and existing conventions. Use this existing checkout; do not initialise a replacement repository.
- [ ] Reuse the existing three production projects and add three test projects. Centralise package versions; choose a compatible stable xUnit version and matching test tooling rather than mixing major versions.
- [ ] Pin the SDK with `global.json`; enable nullable reference types and deterministic compilation. Do not choose a licence or publish packages without a user decision.
- [ ] Implement the Section 5 model contracts; normalise collection values to non-default immutable arrays when constructing snapshots.
- [ ] Write the fixture source from Section 6 and project references; explicitly restore and build this repository-owned fixture.
- [ ] Add a model test that mutates an input builder after snapshot creation and verifies the snapshot is unchanged.
- [ ] Add tests confirming source evidence coordinate conventions and that unresolved targets can be represented without fabricated IDs.
- [ ] Document the structural meaning of a call chain before implementing traversal.
- [ ] Run `dotnet build sharpview.slnx` and `dotnet test tests/Sharpview.Core.Tests`.
- [ ] Commit if the product checkout is a normal Git repository and commits are within the authorised workflow.

**Acceptance:** fixture and host build using only the SDK; public model has no Roslyn dependency and retains the data needed for printed chains.

## 8. Stage 2 — SDK-only project loading

**Files:** `Loading/AnalysisRequest.cs`, `MSBuildBootstrap.cs`, `WorkspaceLoader.cs`, `RoslynProgramLoader.cs`, `LoadingTests.cs`.

**Consumes:** Stage 1 model. **Produces:** internal project compilation contexts and public snapshots containing compilation facts and load gaps.

- [ ] Add SDK-only registration before any MSBuild-dependent code loads. Use a separate bootstrap/type boundary; verify it in a fresh process, not only an already-initialised test process.
- [ ] Avoid shipping conflicting MSBuild runtime assemblies. Follow Microsoft.Build.Locator package guidance and inspect the executable output/dependency graph.
- [ ] Validate input file existence/extension and load `.sln`, `.slnx` or `.csproj` with `MSBuildWorkspace`. Project input includes source project references.
- [ ] Apply configuration and optional framework selection. For a multi-target project without an explicit selection, fail clearly and list choices; do not silently pick one. Detect unsupported mixed selections and name the affected project.
- [ ] Record the actual loaded framework/options, source/metadata project references, workspace failures and compiler diagnostics.
- [ ] If one solution project fails but others can be analysed, return partial results with project-scoped gaps. A fatal inability to load any selected input is an exception translated by the CLI.
- [ ] Ensure cancellation is passed to loading/compilation operations and resources are disposed on failure.
- [ ] Test `.csproj`, `.sln` and `.slnx` input, project-reference inclusion, invalid paths, ambiguous multi-target input, explicit framework selection, missing references and cancellation.
- [ ] Restore a repository-owned fixture and then analyse it. Use an isolated fixture copy with unavailable assets to test the no-silent-restore policy.
- [ ] Run `dotnet test tests/Sharpview.Roslyn.Tests --filter LoadingTests` and the fresh-process bootstrap check.
- [ ] Commit the independently testable loader.

**Acceptance:** compilation selection is observable; load failure cannot be mistaken for an empty complete solution; no Visual Studio components are required.

## 9. Stage 3 — identity and semantic extraction

**Files:** identity/extraction files, `IdentityTests.cs`, `ExtractionTests.cs`, `docs/coverage.md`.

**Consumes:** compilations from Stage 2. **Produces:** fully populated initial snapshots.

### 3A. Declaration identity

- [ ] Construct canonical identities using compilation variant, assembly identity and declaration signature. Include nested names, generic arity, parameter/ref types and explicit interface implementation distinctions. Do not use display text as the identity.
- [ ] Maintain a registry linking referenced source symbols to already extracted source declarations. Referenced source methods must use the target project's selected compilation identity, not the caller's identity.
- [ ] Canonicalise original generic definitions, reduced extension methods and partial implementation/definition pairs. Retain constructed type arguments at each site.
- [ ] Give local functions/lambdas snapshot-scoped identities based on containing callable and source position. Stability after arbitrary source edits is not promised.
- [ ] Add referenced metadata declaration/type stubs lazily for observed targets. Do not enumerate the entire framework metadata universe.
- [ ] Test overloads, same display name in different assemblies, partial methods, source-project references and generic instantiations before extracting chains.

### 3B. Body operations and ownership

- [ ] Extract declaration signatures, attributes, type-use components and nullability using symbols. Preserve all partial declaration source locations.
- [ ] Walk supported method/constructor/accessor bodies through `IOperation`. Handle expression-bodied members too.
- [ ] For nested local/lambda bodies, switch callable ownership and extract them separately. Merely declaring a lambda/local function must not create a call from its parent.
- [ ] Extract invocations, explicit construction/constructor initialisers, property getters/setters and event add/remove operations. A compound property operation may execute both getter and setter; test it.
- [ ] Classify dispatch using Section 3.2. Record dynamic/delegate/function-pointer boundaries; preserve source evidence even without a known target.
- [ ] Treat await's underlying invocation as a call fact but make no exception-observation or scheduler claim. Record unsupported implicit await/enumeration/disposal calls when encountered; do not imply semantic completeness for sugar you do not model.
- [ ] Associate each source call site with a deterministic ID and document checksum. Implicit sites at the same span need an ordinal/kind to avoid ID collisions.
- [ ] Record unsupported initialisers and top-level ownership explicitly rather than attaching their calls to arbitrary methods.
- [ ] Extract explicit throw statements/expressions and rethrows. Store static type if known; do not infer exception handling or escape.
- [ ] Extract typed dependencies for signatures, base types/interfaces, generic constraints, attributes, construction and body type/member use. Signature dependencies include nested generic components.
- [ ] Include source-generator documents by default and preserve generated evidence. Exclusion is explicit and creates scoped excluded-source information.
- [ ] Dispose extraction-owned workspace state; snapshots must remain queryable after disposal.

### 3C. Extraction tests

Use assertions with exact semantic expectations, for example:

```csharp
var snapshot = await LoadResearchFixtureAsync();
var service = Assert.Single(snapshot.Callables,
    c => c.DisplaySignature == "Research.Application.Service.Load()");
var call = Assert.Single(snapshot.CallSites, s => s.Caller == service.Id);
var target = Assert.Single(snapshot.Callables, c => c.Id == call.BoundTarget);
Assert.Equal("Research.Persistence.Repository.Read()", target.DisplaySignature);
Assert.Equal(DispatchKind.Direct, call.Dispatch);
Assert.Equal(BodyStatus.Source, target.Body);
Assert.True(call.Evidence.StartLine > 0);
```

`LoadResearchFixtureAsync` is a test helper that uses the real fixture path and the public loader; implement it once in the extraction test project.

- [ ] Verify the four-method cross-project chain has three correctly linked call sites.
- [ ] Verify interface binding points to the interface member, with no invented implementation call.
- [ ] Verify a lambda's internal calls belong to the lambda and not the enclosing method.
- [ ] Verify repeated calls remain two call sites, even if later printing collapses them.
- [ ] Verify constructor/getter/setter/event calls and explicit base dispatch.
- [ ] Verify source locations for generated source and source exclusion coverage.
- [ ] Verify throw versus rethrow and declared nullable annotations without claiming exception/null-state propagation.
- [ ] Verify snapshots still work after workspace disposal.
- [ ] Run all Roslyn tests and commit.

**Acceptance:** supported source operations are represented with correct owners/targets or visible gaps; cross-project IDs resolve correctly.

## 10. Stage 4 — call-chain enumeration and typed queries

**Files:** `CallIndex.cs`, `CallChainQuery.cs`, `CallChainResult.cs`, `DependencyQueries.cs`, core query tests.

**Consumes:** snapshots only. **Produces:** bounded deterministic paths and signature dependency selection.

Implement the traversal with an explicit DFS stack, not recursive calls proportional to user-supplied depth. Each frame holds the current callable, ordered outgoing edges and next edge index. Maintain a path-local callable set and path steps. Check cancellation/work/output limits during expansion.

Required algorithm:

```text
validate options and root IDs
build callable lookup and ordered, collapsed outgoing edge index
for each distinct root in deterministic order:
    initialise path-local visited set with root
    descend one relationship at a time
    emit a path when a terminal condition is reached
    include the closing edge when reporting a cycle
    unwind path state before exploring the next sibling
    stop all roots when the output/work budget prevents further search
return emitted chains and accurate limit status
```

Mark a global limit as reached only when additional work/results were prevented. Exactly reaching `MaxChains` at the natural end must not report truncation. Depth-limited paths have explicit ends even though global budgets may remain unexhausted.

- [ ] Write tests with manually assembled minimal snapshots so graph logic is independent of Roslyn loading.
- [ ] Validate positive chain/work limits and nonnegative depth; reject unknown explicit roots with a clear error.
- [ ] Implement the call index retaining all supporting call-site IDs.
- [ ] Implement declared/direct policies and termination precedence. Apply unknown/dispatch/cycle boundaries before descending; depth stopping occurs when the next direct transition would exceed the limit.
- [ ] Implement path-local traversal and deterministic global budgets across roots.
- [ ] Implement `SignatureUses` by dependency reason; test nested generic references, avoiding namespace-name heuristics inside the library.
- [ ] Keep text formatting outside the query library: results contain IDs, steps and end status, not preformatted arrows.
- [ ] Run `dotnet test tests/Sharpview.Core.Tests` and commit.

Essential expectations:

| Input | Expected |
|---|---|
| A → B → C | One chain with two transitions |
| A calls B and C; B calls D | A → B → D and A → C |
| A calls B twice | One displayed relationship path; both evidence sites retained |
| Diamond A → B/C → D | Both paths retained |
| A → B → A | One chain including closing A, cycle end |
| Root has no calls | Singleton root, leaf end |
| Interface call, declared mode | Interface target displayed; stop |
| Interface call, direct mode | Excluded dispatch boundary displayed |
| Metadata call | Metadata target displayed; stop |
| Depth zero, root with calls | Root only, depth end |
| Exact output cap equals natural results | No false truncation |
| More results than output cap | Truncation reported |
| Every source callable as root | Recursive components and leaf methods included |

**Acceptance:** chain enumeration is meaningful, finite, reproducible and independent of Roslyn.

## 11. Stage 5 — CLI, arrows and hands-on experiment

**Files:** CLI files, `CliProcessTests.cs`, README and optional VS Code tasks.

### 5A. Command contract

```text
sharpview methods <input> [--configuration Debug] [--framework <tfm>]
sharpview chains <input> [--root <signature> | --root-id <id>]
                     [--dispatch declared|direct]
                     [--max-depth 20] [--max-chains 1000] [--max-expansions 100000]
                     [--configuration Debug] [--framework <tfm>] [--locations]
sharpview inspect <input> [--format text|json] [--output <path>]
                      [--configuration Debug] [--framework <tfm>]
```

All commands accept `--exclude-generated`. No root means all source callables. `--root` is an exact displayed signature copied from `methods`; if ambiguous, report matching IDs and require `--root-id`. No fuzzy matching silently chooses a root. Root selection supports one root in this increment; multiple roots are already supported by the library API.

- [ ] Use a small explicit argument parser with exhaustive command tests; no plugin/command discovery infrastructure. A stable CLI parsing package is acceptable only if it simplifies this exact contract.
- [ ] Implement `--help` without invoking SDK bootstrap or loading a project.
- [ ] Validate numeric options, conflicting root options, invalid dispatch/format values and missing arguments before loading.
- [ ] Register SDK then load once per command; pass cancellation throughout.
- [ ] `methods`: print source callable signatures and IDs; exclude metadata stubs from this default list.
- [ ] `chains`: call the library query and format ` --> ` separated qualified signatures. Add the Section 3 markers. `--locations` prints source call evidence on indented following lines.
- [ ] `inspect --format text`: print compilation/fact counts, dispatch counts, explicit throw sites and coverage summary.
- [ ] `inspect --format json`: wrap snapshot in an envelope with integer schema version 1 and producer version. Sort fact collections; preserve ordered signature components/parameters.
- [ ] Send results to stdout or the explicit output file; progress, gaps and truncation summary to stderr. JSON stdout must remain valid JSON.
- [ ] Summary names the declared/direct policy, root count, printed chain count and any gaps/limits. Do not report metadata boundaries alone as extraction failures.
- [ ] Define exit codes: 0 completed without extraction gaps or traversal limits; 1 usable output with gaps/depth/work/output truncation; 2 invalid request or fatal analysis failure; 130 cancellation. Unknown dispatch boundaries also produce a visible incompleteness summary and exit 1. Help exits 0.

### 5B. Real process tests

- [ ] Launch the CLI as a child process for tests, capture stdout/stderr separately and assert exit codes. Use process cancellation with a bounded test timeout; do not leave orphan processes.
- [ ] Assert exact principal fixture output:

```text
Research.Api.Controller.Get() --> Research.Application.Service.Load() --> Research.Persistence.Repository.Read() --> Research.Persistence.Database.Execute()
```

- [ ] Verify branch output contains two valid paths and no sibling-to-sibling invented edge.
- [ ] Verify recursive roots terminate with `[cycle]` and default all-root mode includes their methods.
- [ ] Verify interface, metadata and dynamic markers and stderr incompleteness policy.
- [ ] Verify depth/global caps cannot hang or silently pretend completeness.
- [ ] Verify `methods` IDs can be passed back through `--root-id`.
- [ ] Parse JSON stdout with `JsonDocument` and assert schema version and source evidence fields.
- [ ] Verify paths with spaces, absent root, ambiguous signatures, invalid numeric values and missing files.
- [ ] Verify generated exclusion, broken-project partial output and cancellation status.

### 5C. Use it as a research tool

- [ ] Add README SDK-only restore/build/test/run commands and a four-line explanation of declared chains versus runtime execution.
- [ ] Add optional VS Code tasks invoking those same commands; no extension authoring. Debug configuration is optional and must not delay the console experiment.
- [ ] Run the tool against the real repository-owned multi-project fixture and save sample output under docs.
- [ ] Pack the CLI as a local .NET tool only after process tests pass; test local package installation without publishing it.
- [ ] Run on an existing solution supplied by the user. If none is available, complete fixture validation and explicitly mark the user-code experiment as pending; do not claim it occurred.
- [ ] Record load gaps, surprising targets and the first model changes justified by actual code. Add focused regression fixtures for demonstrated bugs.
- [ ] Run `dotnet build sharpview.slnx`, `dotnet test sharpview.slnx` and the CLI smoke command. Report what works and remaining gaps.
- [ ] Commit the five-stage baseline in the normal product repository.

**Acceptance:** an SDK-only terminal run prints correct arrow chains across projects; the same capabilities are callable from C#; output remains honest about boundaries and limits.

## 12. Research deliberately postponed

- Resolving interface/virtual targets using hierarchy, points-to analysis or DI configuration.
- General exception/data-flow analysis, implicit exception origins, task alias tracking, finally execution modelling and complete middleware coverage. Explicit origins, lexical protected regions, rethrow/replacement witnesses and directly awaited calls are in scope now.
- Interprocedural nullable state or contract inference.
- Persisted snapshot import, graph databases and incremental caching.
- General rule execution infrastructure, SARIF publishing and VS Code extension.
- Cross-platform CI matrix: a useful follow-up, not required to start inspecting chains on the user's machine.

The first increment adds small lexical exception-region facts rather than a second AST. Unsupported behaviours produce scoped unknown results.

## 13. Codex execution guidance

Implement stages in order. Within each stage, write the semantic test first where it defines meaningful behaviour, run it to see the expected failure, then implement and rerun it. Do not add tests that merely mirror record constructors or formatting internals.

Resolve routine code choices yourself. Stop for a user decision only when the product repository is missing, required permissions block work, or a design change affects the public semantics. Read applicable repository instructions first. Do not ask for approval after every checkbox.

At the end of each stage, report the delivered capability, tests run and unresolved limitations; continue to the next stage when implementing the authorised five-stage request.

Suggested initial `AGENTS.md` content for the product repository:

```text
Implement the C# call-chain research plan, stages 1–5.
Use the .NET SDK and CLI; no Visual Studio integrations.
Keep Roslyn types inside Sharpview.Roslyn.
Keep traversal in Sharpview.Core, formatting/arguments in Sharpview.Cli.
Never modify user projects being analysed.
Call chains represent static declared relationships, not execution traces.
Preserve all call-site evidence even when display relationships are collapsed.
Unknown dispatch and missing bodies must remain visible.
Bound every path enumeration by depth, work and output limits.
Use path-local cycle detection and deterministic output.
Do not add a database, DSL, plugin framework or editor extension.
Run the relevant tests before claiming a stage works.
```

## 14. Self-review and final checklist

- [ ] C# choice applied throughout; no remaining language decision gate.
- [ ] Exactly five implementation stages; initial console experiment is the main acceptance path.
- [ ] Every supported/unsupported call category has extraction and/or boundary expectations.
- [ ] Cross-project identity and overload disambiguation tested.
- [ ] Declared binding never masquerades as resolved runtime dispatch.
- [ ] Branches, cycles, duplicates and limits have concrete tests.
- [ ] Public API types in later tasks match Section 5.
- [ ] Generated source, missing references and cancellation are covered.
- [ ] Build/run require no Visual Studio installation for supported inputs.
- [ ] No publication or user-input mutation included.

Planning review: the document provides concrete API shapes, source fixtures, command contracts, file ownership and acceptance cases. Implementation-specific helper code belongs in the stage that owns it. The product repository is already available. The first external user-code experiment still requires an input solution selected by the user.
