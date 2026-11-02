# Semantic Analysis Foundation Implementation Plan

> Superseded for the first implementation by [the C# call-chain research plan](2026-10-12-csharp-call-chain-research-plan.md). The user has chosen C# and stages 1–5, centred on console arrow-chain output. Use that plan for implementation.

> For agentic workers: use superpowers:executing-plans to implement this plan task by task after the user reviews the design. Steps use checkboxes for tracking. No implementation is authorised by the creation of this document.

**Goal:** Build an open-source .NET library and CLI that expose inspectable, higher-level semantic facts from existing C# projects and solutions, as the foundation for a long-term analysis research project.

**Architecture:** A Roslyn-independent public model holds immutable declarations and source-backed usage facts. A Roslyn adapter loads compilations and extracts those facts; library queries derive relationships and witness paths. A thin console host exposes the same operations without owning analysis logic.

**Tech stack:** Proposed C#, .NET 10, Roslyn CSharp.Workspaces and Workspaces.MSBuild, Microsoft.Build.Locator, System.Text.Json, xUnit. Select and pin compatible stable package versions when implementation begins; do not assume their version numbers match the runtime version.

**Spec:** The design brief below is the proposed specification for this first research increment. The user has chosen C#; the newer call-chain plan defines the current implementation scope.

## Design brief

The user wants a reusable foundation for higher-level structural and behavioural rules, not a linter product. Initial work must make the extracted semantics inspectable and easy to experiment with. Use existing code as input; never modify it during analysis. CLI is the first execution surface and VS Code is the development environment. There is no Visual Studio integration work.

The first increment answers:

- What types and callable declarations exist in each selected compilation?
- What types are referenced, and for which reason?
- What callable does each call site statically bind to?
- Which explicit exception origins exist, and where?
- Which call-site-backed paths connect selected callables under direct binding?
- What was unavailable, unresolved or unsupported?

It does not claim to know all runtime call targets, prove exception escape, or establish interprocedural null safety. Those are subsequent analyses. The representation retains enough evidence to add them without confusing observation with inference.

## Global constraints

- CLI and all builds/tests run with the .NET SDK; Visual Studio installation must not be required for supported SDK-style inputs.
- Initial input support: SDK-style C# `.csproj`, `.sln` and `.slnx` files. Verify `.slnx` support with the selected Roslyn/MSBuild versions.
- Develop and validate on Linux first; add Windows and macOS CI for SDK-style fixture inputs.
- CLI and library target .NET 10 initially. This is the analyser host target, not a restriction that analysed projects must target .NET 10.
- Restore input dependencies explicitly before analysis; the CLI does not silently restore or rewrite inputs.
- Each run selects one configuration and target framework per project. Do not merge incompatible compilation variants into one semantic identity space.
- Framework selection may be scoped per project when a solution contains different target frameworks. A global framework selector must fail clearly when incompatible with a selected project.
- Public facts and queries do not expose Roslyn symbols, syntax trees, semantic models or workspace objects.
- The Roslyn adapter is the expert extension surface; consumer rules use public facts and queries.
- Missing bodies and unknown dispatch remain visible. No result represents missing evidence as proof of absence.
- No database, custom rule language, plugin discovery, VS Code extension, whole-program data-flow engine or Visual Studio extension in this increment.
- Synced files under `sources/` in this ChatGPT project remain read-only.
- The implementation repository should be a normal user-owned checkout. Do not initialise product development inside this synced reference mirror by default.

## Language decision

Decision: C# throughout the first increment. It keeps Roslyn integration, public API examples, CLI packaging and contributor onboarding in one language. Immutable records and explicit discriminated cases using record hierarchies are enough for the initial facts.

F# is a credible alternative for analysis kernels: algebraic data types, pattern matching and immutable transformations suit abstract domains and fixed-point analyses. It does not solve dispatch, aliasing or exception semantics automatically. If the user prefers F#, the extractor and CLI can be F# while exposing a deliberately .NET-friendly API; all public signatures and examples must be tested from C#. Do not expose F# lists/options/unions indiscriminately to C# consumers.

Avoid a mixed-language solution at the beginning unless F# is an explicit preference. Reconsider an internal F# analysis project when implementing a genuinely stateful behavioural analysis.

Sources:
- [F# component design guidelines](https://learn.microsoft.com/en-us/dotnet/fsharp/style-guide/component-design-guidelines)
- [.NET SDK and MSBuild discovery](https://github.com/microsoft/MSBuildLocator/blob/main/src/MSBuildLocator/README.md)

## Repository layout

Use the existing `Sharpdrill` project names and `SharpDrill.slnx`; do not recreate or rename the scaffold.

```text
global.json
Directory.Build.props
Directory.Packages.props
SharpDrill.slnx
src/
  Sharpdrill.Core/                  # Facts, identifiers, queries, rule contracts
    Model/
    Queries/
    Rules/
  Sharpdrill.Roslyn/           # Compilation loading and extraction
    Loading/
    Extraction/
    Identity/
  Sharpdrill.Cli/              # Arguments, bootstrap, formatting, exit policy
tests/
  Sharpdrill.Core.Tests/
  Sharpdrill.Roslyn.Tests/
  Sharpdrill.Cli.Tests/
  Fixtures/ResearchSolution/     # Small real multi-project SDK solution
examples/
  ResearchRules/                 # Plain C# library consumer
docs/
  model.md
  coverage.md
  research-log.md
.vscode/
  tasks.json
  launch.json
.github/workflows/ci.yml
README.md
LICENSE
```

Three production projects are sufficient. Do not create a separate abstraction package, graph package, framework-model package or project per analysis yet. The core project contains both facts and queries, separated by responsibility in folders.

## Public contracts

These are the initial API shapes. Final record fields are fixed in Tasks 1 and 3 after fixture-based review.

```csharp
// Sharpdrill.Core
public readonly record struct CallableId(string Value);
public readonly record struct TypeId(string Value);

public sealed record SourceEvidence(
    string DocumentPath, string DocumentChecksum,
    int SpanStart, int SpanLength,
    int StartLine, int StartColumn, bool IsGenerated);

public enum DispatchKind { Direct, Virtual, Interface, Delegate, Dynamic, Unknown }
public enum GapKind { ProjectLoad, Compilation, MissingBody, Binding, Unsupported }
public sealed record AnalysisGap(GapKind Kind, string Scope, string Reason);

public sealed class ProgramSnapshot
{
    public IReadOnlyList<CompilationFact> Compilations { get; }
    public IReadOnlyList<TypeFact> Types { get; }
    public IReadOnlyList<CallableFact> Callables { get; }
    public IReadOnlyList<CallSiteFact> CallSites { get; }
    public IReadOnlyList<DependencyFact> Dependencies { get; }
    public IReadOnlyList<ThrowSiteFact> ThrowSites { get; }
    public IReadOnlyList<AnalysisGap> Gaps { get; }
}

// Sharpdrill.Roslyn
public static class RoslynProgramLoader
{
    public static Task<ProgramSnapshot> LoadAsync(
        AnalysisRequest request, CancellationToken cancellationToken = default);
}

// Sharpdrill.Core.Queries
public static class CallPaths
{
    public static PathSearchResult FindShortest(
        ProgramSnapshot snapshot, CallableId from, CallableId to,
        PathSearchOptions options);
}

// Sharpdrill.Core.Rules
public interface IProgramRule
{
    IEnumerable<Finding> Evaluate(ProgramSnapshot snapshot);
}
```

`IReadOnlyList` does not guarantee immutability alone: construct snapshots with private immutable storage and no mutable aliases. Facts use IDs, not references to a mutable object graph. Source locations use zero-based spans and one-based displayed lines/columns; document this convention.

No public generic graph traversal API is necessary initially. Expose typed facts and specific queries. Add new relations only when a concrete research question requires them.

## Review focus

1. Multiple target frameworks: selected variants must be explicit, and ambiguous selection must fail with an actionable message.
2. Source-project references observed through different compilations: one linked declaration identity, no accidentally duplicated methods or display-name collisions.
3. Interface/delegate/dynamic calls and unavailable assemblies: visible limitations, no fabricated exact runtime targets.
4. Local functions, lambdas, accessors and generated source: correct callable ownership or a specific coverage gap.
5. Broken inputs, cancellation and truncated search: distinguish incomplete analysis from a complete empty result.

## Task 1: Establish the semantic vocabulary and test fixture

**Create:** repository scaffold above; `Model/Identifiers.cs`, `SourceEvidence.cs`, `CompilationFact.cs`, `TypeFact.cs`, `CallableFact.cs`, `ProgramSnapshot.cs`; `tests/Fixtures/ResearchSolution/`.

**Produces:** immutable snapshot/declaration contracts and a fixture with API, Application and Persistence projects. It contains a controller-like entry point, interface repository, concrete repository, direct call chain, recursive pair, generic signatures, nullable annotations and a metadata-only dependency.

- [ ] Pin SDK and stable dependencies. Enable nullable reference types and deterministic builds. Use an open-source licence selected by the user before publication.
- [ ] Record assembly identity, project identity, selected framework/configuration, compiler options and diagnostics in each compilation fact.
- [ ] Define callable kinds for methods, constructors, accessors, local functions and lambdas. Separate declarations with source bodies from metadata-only declarations.
- [ ] Define type uses with generic arguments, nullability annotation and original declaration IDs; do not collapse everything to a display string.
- [ ] Add model tests asserting immutable storage and distinct IDs for overloaded methods and same-named types in different assemblies.
- [ ] Build the fixture through the SDK and run model tests.
- [ ] Commit this independently buildable skeleton.

**Acceptance:** the fixture builds without Visual Studio; the model can represent its important declarations without Roslyn objects in public signatures.

## Task 2: Load real project compilations reliably

**Create:** `Loading/AnalysisRequest.cs`, `SdkBootstrap.cs`, `WorkspaceLoader.cs`; `tests/Sharpdrill.Roslyn.Tests/LoadingTests.cs`.

**Consumes:** `AnalysisRequest`: input path, configuration, optional project selection, framework selection and include-generated flag.

**Produces:** the loader's internal compilation contexts; public `RoslynProgramLoader.LoadAsync` returns declaration-only snapshots until Task 3.

- [ ] Implement SDK-only MSBuild discovery and registration before MSBuild-dependent types load. Keep bootstrap in a separate method/type boundary and test it in a fresh process.
- [ ] Load `.sln`, `.slnx` and `.csproj` inputs with `MSBuildWorkspace`; include selected projects and required project references.
- [ ] Read workspace diagnostics and compiler diagnostics into scoped coverage information.
- [ ] Require explicit framework selection for multi-target projects; provide the available choices in the error. Record the actual loaded variant.
- [ ] Test single-project input, cross-project solution input, mixed target frameworks, missing SDK, unrestored references, missing files and cancellation.
- [ ] Verify the same fixture works from a terminal in a clean SDK-only environment.
- [ ] Commit after tests pass.

**Acceptance:** loaded contexts match the requested compilations; partially loaded solutions cannot masquerade as complete.

## Task 3: Extract source-backed facts and link identities

**Create:** `Identity/DeclarationIdentity.cs`; `Extraction/DeclarationExtractor.cs`, `CallSiteExtractor.cs`, `DependencyExtractor.cs`, `ThrowSiteExtractor.cs`; model fact records and extraction tests.

**Produces:** complete initial `ProgramSnapshot` collections.

Fact decisions:
- `CallSiteFact`: site identity, owning callable, statically bound target if present, dispatch kind, constructed type arguments, source evidence and explicit/implicit origin.
- `DependencyFact`: owning declaration, referenced type and reason: signature, inheritance, constraint, attribute, construction or body use.
- `ThrowSiteFact`: owning callable, evidence, explicit throw/rethrow kind and statically known exception type if available. A throw expression's static type is not automatically its exact runtime type.
- `CallableFact`: signature, accessibility, attributes, declared nullable contracts, source declarations and body availability.

- [ ] Build assembly-scoped declaration IDs from canonical signatures, including nested types, arity, overload parameters and ref kinds. Keep display names separate. IDs are snapshot identifiers, not a promise of stability across arbitrary edits.
- [ ] Link source project reference symbols back to their declaration facts; normalise original generic definitions while retaining each constructed use.
- [ ] Extract declarations from symbols and usage facts from `IOperation`. Preserve syntax evidence, recognising implicit operations explicitly.
- [ ] Keep nested local-function/lambda operations under their own callable owners. Do not attribute lambda bodies to the enclosing method.
- [ ] Handle ordinary invocation, construction and accessor execution. For unsupported call-like constructs, record specific gaps rather than silently ignoring them.
- [ ] Retain generated-document checksums and status, respecting the selected generated-source policy. Validate generator-produced documents in a fixture.
- [ ] Extract explicit `throw new E()`, `throw value`, throw expressions and `throw;`. Do not infer exception escape here.
- [ ] Test overloads, partial types/methods, generic calls, interface/virtual/delegate/dynamic dispatch, cross-project identity, accessors, nested functions and metadata targets.
- [ ] Confirm an interface call records the interface binding and interface dispatch, without being labelled an exact concrete execution target.
- [ ] Release workspace/semantic-model ownership after producing the snapshot where practical; add a repeated-load smoke test.
- [ ] Commit after extraction tests pass.

**Acceptance:** every relevant fixture use is represented or accounted for by a scoped gap, and every source-backed fact can be traced to the source.

## Task 4: Add two useful higher-level queries

**Create:** `Queries/CallPaths.cs`, `PathSearchResult.cs`, `DependencyQueries.cs`; rule contracts `Rules/Finding.cs`, `IProgramRule.cs`; tests for path search and dependency reasons.

**Consumes:** typed facts only; no Roslyn dependency.

**Produces:** shortest direct-call witnesses and dependency queries supporting user-owned architectural policies.

- [ ] Build immutable indexes by callable/type ID. Derived indexes must not alter extracted facts.
- [ ] Define direct traversal: statically exact target binding for supported nonvirtual calls and explicitly modelled base calls; exclude unresolved virtual/interface/delegate/dynamic dispatch.
- [ ] Implement breadth-first shortest-path search with cycle protection, maximum depth and visited-node budget.
- [ ] Return call-site witnesses, examined scope, relevant unresolved dispatch, gaps and limit status. A missing witness means no direct path found; it is not proof that no runtime path exists.
- [ ] Expose dependency reasons and generic type components so consumers can distinguish API exposure from implementation use.
- [ ] Implement one example C# rule: public API signatures must not expose a designated persistence type set. Classification is supplied by the consumer.
- [ ] Test shortest versus longer paths, recursion, self-path semantics, multiple call sites, exhausted budgets, unknown dispatch and nested generic API exposure.
- [ ] Commit after query tests pass.

**Acceptance:** consumers can express the signature rule and retrieve source-backed paths without using Roslyn.

## Task 5: Expose library capabilities through the CLI

**Create:** `Sharpdrill.Cli/Program.cs`, `Arguments.cs`, `SnapshotJsonWriter.cs`, `TextReportWriter.cs`; process-level CLI tests.

**Commands:**
```text
sharpdrill inspect ./Example.slnx --configuration Debug --format json --output snapshot.json
sharpdrill inspect ./Example.csproj --framework net10.0 --format text
sharpdrill path ./Example.slnx --from <callable-id> --to <callable-id> --max-depth 20
```

`inspect` lists declarations, call sites, typed dependencies, explicit throw sites and gaps. Its output supplies IDs for `path`. This JSON is an inspectable export with a schema version; loading persisted snapshots is not yet a feature.

- [ ] Implement help, explicit selection options, argument validation and Ctrl+C cancellation.
- [ ] Write machine-readable output to stdout or the requested file; progress and operational errors go to stderr.
- [ ] Use stable ordering and avoid nondeterministic timestamps in snapshot data. Include schema and producer versions.
- [ ] Define exit codes: 0 completed with complete requested extraction coverage; 1 completed with coverage gaps; 2 invalid request or fatal load/execution failure; 130 cancellation. Finding presence does not alter exit codes in this increment.
- [ ] Test commands as child processes, including JSON parsing, paths containing spaces, broken input, incomplete extraction and cancellation.
- [ ] Pack as a .NET tool and test local installation from the produced package.
- [ ] Commit after process tests pass.

**Acceptance:** the installed tool analyses the fixture with documented parameters and no Visual Studio installation.

## Task 6: Make research and VS Code use repeatable

**Create:** VS Code build/test/run tasks and launch configuration, CI, README, `docs/model.md`, `docs/coverage.md`, `docs/research-log.md`, and `examples/ResearchRules/`.

- [ ] Make VS Code tasks invoke the same `dotnet` commands used by CI. Debugger configuration is editor convenience only; build and execution must work without any editor extension.
- [ ] Add Linux, Windows and macOS SDK-based build/test jobs. Run CLI packaging smoke tests at least on Linux.
- [ ] Document restore prerequisites, supported inputs, framework selection, facts versus inferred relations, source coordinates, uncertainty and exit codes.
- [ ] Add an executable example using only library APIs to identify API exposure and print a direct call witness.
- [ ] Analyse one existing user-selected SDK-style solution, recording load coverage, extraction gaps, counts, elapsed time and peak memory. Do not invent universal performance thresholds from the small fixture.
- [ ] Record representation changes prompted by that experiment. Add a regression fixture only for each demonstrated semantic problem.
- [ ] Run full tests and packaging smoke check; review public API and example ergonomics.
- [ ] Commit the runnable research baseline. Publication and public repository creation are separate user decisions.

**Acceptance:** another developer can reproduce extraction and write a structural rule from the README, using the SDK and VS Code or a terminal.

## Next research increment: explicit exception escape

This is a separate design and implementation cycle, not a hidden requirement of Tasks 1–6.

1. Formulate the claim precisely: an exception originating at an explicit throw may escape a selected boundary under a stated call-target model.
2. Add a small per-callable body model retaining ordered operations, CFG blocks, call/throw positions and protected regions. Prototype against Roslyn CFG before freezing this public schema.
3. Model catch type compatibility, filters, rethrows, throws from handlers, and finally behaviour. Preserve unknowns.
4. Compute per-method escape summaries and propagate through caller sites with a monotone fixed-point algorithm for recursive components.
5. Treat async task production and exception observation at await as separate effects. Analyse synchronous cases first; classify unsupported async scenarios explicitly.
6. Return witness chains and the handlers considered. Never interpret the absence of a catch in a caller as sufficient proof of escape.
7. Compare results against executable fixtures. Tests provide evidence for supported patterns, not a universal soundness proof.

Subsequent research can add dispatch models, framework knowledge and nullable contract propagation. Keep these separate from the factual extraction layer.

## Codex working instructions

For implementation, use a normal repository and add an `AGENTS.md` stating:

```text
Read docs/model.md and the implementation plan before changing the model.
Use C# and the .NET SDK; do not add Visual Studio integrations.
Keep Roslyn types inside Sharpdrill.Roslyn.
Keep CLI code thin; put analysis in libraries.
Never modify projects being analysed.
Every derived relationship must state its assumptions.
Every unsupported construct must be reported in scoped coverage.
Implement one reviewed task at a time and run its meaningful tests.
Do not add a database, DSL, plugin framework or editor extension without a concrete requirement.
```

Start Codex with: “Read the design brief and implement Task 1 only. Use the selected C# implementation language. Keep SDK-only operation and source traceability as acceptance requirements. Report any model decisions the fixtures expose before advancing to Task 2.”

## Self-review

- All proposed first-increment requirements map to Tasks 1–6.
- Every review-focus failure mode has owning tests in Tasks 2–5.
- Exact binding and runtime dispatch are separate throughout the model, queries and CLI.
- Exception origins are included; exception propagation is explicitly a later research increment.
- The analyser host framework is distinguished from input project frameworks.
- C# is selected; F# discussion is retained only as historical background.
- No product code, dependency installation, input mutation or repository publication is performed by this planning task.
