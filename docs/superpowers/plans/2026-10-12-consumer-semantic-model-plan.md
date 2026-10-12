# Consumer-centric semantic model — implementation plan

**Spec:** the approved architecture review (2026-10-12), summarised in "Goal" below. The earlier research plan (`2026-10-12-csharp-call-chain-research-plan.md`) remains the authority for analysis semantics (declared vs inferred bindings, honest Unknown, evidence, determinism, bounded traversal).

## Goal

Move Sharpview from "two hard-wired reports" to a reusable semantic base that consumers (rules, layer overviews, evaluations) can build on cheaply:

1. One shared `ProgramIndex` instead of per-consumer dictionaries and linear scans.
2. Layers as a first-class model (`LayerModel` → `LayerMap` → aggregated `LayerGraph` with witnesses) plus an allowed-dependency evaluation.
3. Reachability with one shortest witness per reached callable (alongside, not instead of, path enumeration).
4. Facts consumers need: namespace/kind/modifiers, attribute arguments, overrides, field/property dependencies, implementation *candidates* (never presented as resolved runtime targets).
5. Stable portable keys, typed IDs, structured reason codes, a common `Finding` result.
6. Framework conventions (controller actions) classified in Core from facts, so the snapshot does not grow one array per framework concept.
7. Remove quadratic lookups in extraction.

## Global Constraints (bind every task)

- **No regressions.** All existing tests keep passing. Existing tests may only be changed mechanically (constructor/property shape, renamed types); assertions must never be weakened or deleted. If an assertion must change, the report must justify it as a deliberate, listed behaviour change.
- **Output comparison protocol (every task).** After implementation, from the repo root:
  1. `dotnet build sharpview.slnx -v q` and `dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx -v q` — 0 errors, no new warnings.
  2. `dotnet test sharpview.slnx` — all pass; report the count (baseline: 74).
  3. `bash .superpowers/compare/capture.sh .superpowers/compare/task-<N>` then `diff -r .superpowers/compare/baseline .superpowers/compare/task-<N>`. Every difference must be one the task lists under "Expected output changes". Any other difference is a regression to fix. Paste the diff summary (or "identical") and the timing lines into the report. Self-analysis runs (`self-*`) must not become noticeably slower (> 15 %).
- Sharpview's text formatting of existing commands (`methods`, `chains`, `controllers`, `exceptions`, `inspect` text) stays byte-identical unless a task explicitly lists a change.
- Architecture: Core stays Roslyn-free. Extraction (Roslyn) ↔ analysis (Core) ↔ formatting (CLI) stay separate. No database, DSL, plugin framework, rule scheduler or editor extension. Rules are plain C# functions returning `Finding`s.
- Determinism (ordinal ordering everywhere), cancellation checks in loops, bounded traversal with explicit limit flags, honest uncertainty (never silently omit unsupported constructs; implementation candidates are never labelled as runtime targets).
- Code style: match surrounding code — file-scoped namespaces, records, `ImmutableArray`, compact expression style, sparse comments that explain *why*.
- Do not modify the fixture under `tests/Fixtures/` (it anchors the baseline). Use in-memory extraction tests (see `ExtractionTests.cs`) or Core snapshot tests (see `CoreTests.cs`) for new cases.
- Commit at the end of each task with a descriptive message ending in `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

### Task 1: Remove quadratic lookups in extraction

**Files:** `src/Sharpview.Roslyn/Extraction/FactExtractor.cs`

`RegisterAsync` runs `bodies.Any(x => Key(x.Symbol) == Key(method))` + `RemoveAll` for every declaration node, and `RegisterMethod` runs `bodies.FirstOrDefault(x => Key(x.Symbol) == key)`. `Key()` builds a documentation-ID string each time, so extraction is quadratic in method count.

- Keep an ordered body collection plus a `Dictionary<string, …>` keyed by `Key(...)`. Preserve the **exact** iteration-order semantics of today's list, including the "remove then append" move when a later declaration has a body (dependency ordering in the snapshot depends on insertion order among ties).
- Optionally memoise `Key(IMethodSymbol)` with `SymbolEqualityComparer.Default`.
- No other behaviour change.

**Tests:** existing suite. Add one extraction test covering a partial method (definition + implementation in separate parts) that asserts a single callable fact with `BodyStatus.Source` and the implementation's call sites — if an equivalent test already exists, name it in the report instead.

**Expected output changes:** none. All capture files, including `inspect-json.txt`, byte-identical.

---

### Task 2: Evolvable snapshot, typed exception IDs, string JSON IDs

**Files:** `src/Sharpview.Core/Model/Facts.cs`, `src/Sharpview.Core/Queries/ExceptionQuery.cs`, `src/Sharpview.Roslyn/Extraction/FactExtractor.cs`, `src/Sharpview.Cli/*`, tests.

1. `ProgramSnapshot` becomes a `sealed record` with `init` properties (same names, same declaration order), each defaulting to an empty `ImmutableArray`, so adding a fact family no longer breaks every constructor call. Update all construction sites (extractor, tests) to object-initialiser syntax.
2. Add typed IDs `ThrowSiteId`, `CatchId`, `ExceptionRegionId` (`readonly record struct X(string Value)`, like `CallableId`). Use them for: `ThrowSiteFact.Id`, `ThrowSiteFact.CatchId`, `ThrowSiteFact.ProtectedRegions`, `CallSiteFact.ProtectedRegions`, `CatchFact.Id`, `CatchFact.SubsequentThrow`, `ExceptionRegionFact.Id`, `ExceptionRegionFact.Catches`, `ExceptionWitness.Origin`, `ExceptionWitness.SubsequentOrigin`, `ExceptionWitness.HandlerIds`. ID string values stay exactly as today.
3. All ID structs (`CompilationId`, `TypeId`, `CallableId`, `CallSiteId` and the three new ones) serialise to/from JSON as plain strings via a `System.Text.Json` converter declared in Core (attribute on the struct). Bump JSON `schemaVersion` to 3 (CLI `Commands.cs`, help text, README mention).

**Tests:** existing suite (mechanically updated). Add a test that round-trips a small snapshot through `JsonSerializer` (CamelCase options as in `Commands.cs`) and asserts IDs are JSON strings and values survive.

**Expected output changes:** only `inspect-json.txt`: `schemaVersion` 2→3 and ID fields rendered as strings instead of `{ "value": … }` objects. Everything else byte-identical. Verify by normalising (e.g. a small `jq` or script that rewrites `{"value": X}` → `X`) and showing the normalised diff contains only the schemaVersion line.

---

### Task 3: Richer type/callable facts, attribute arguments, stable keys

**Files:** `Facts.cs`, `FactExtractor.cs`, CLI only if needed to keep output identical, tests.

Add to Core:
- `enum DeclaredTypeKind { Class, Record, Struct, RecordStruct, Interface, Enum, Delegate, Other }`
- `[Flags] enum TypeModifiers { None = 0, Abstract = 1, Sealed = 2, Static = 4, Generic = 8 }`
- `[Flags] enum CallableModifiers { None = 0, Static = 1, Abstract = 2, Virtual = 4, Override = 8, Sealed = 16, Generic = 32, Extension = 64, Operator = 128, ExplicitImplementation = 256, Finalizer = 512 }` (`Operator` = user-defined operator or conversion).
- `sealed record AttributeFact(string Name, ImmutableArray<string> Arguments, ImmutableArray<NamedArgumentFact> NamedArguments)` and `sealed record NamedArgumentFact(string Name, string Value)`. `Name` is exactly today's attribute string; values rendered with `TypedConstant.ToCSharpString()`.

Extend facts (append new members; keep existing ones and their order):
- `TypeFact`: `Namespace` (string, `""` for global), `Kind`, `Modifiers`, `TypeId? BaseClass` (the base class only; `BaseTypes` keeps today's content/order), `TypeId? ContainingType`, `string Key`. `Attributes` becomes `ImmutableArray<AttributeFact>` (ordered by Name, then rendered arguments, ordinal).
- `CallableFact`: `Modifiers`, `CallableId? Overrides` (directly overridden member, registered via `RegisterMethod` so the chain is walkable), `string Key`. `Attributes` becomes `ImmutableArray<AttributeFact>`.
- `ProgramSnapshot.SourceRoot` (string; full path of the input file's directory, set by the loader) and a Core helper `SourceEvidence.RelativeTo(string root)` returning a `/`-separated relative path (or the original path when outside root).

Stable keys: `"<ProjectName>|<documentation comment ID>"`, where ProjectName is the project file name without extension for source, the assembly simple name for metadata. Callables without a documentation ID (local functions, lambdas) use `"<ProjectName>|<containing callable doc ID>|<local|lambda>:<name or ordinal>@<relative path>:<line>:<column>"`. Keys must contain no absolute paths, configuration, framework, or assembly version, and must be unique within a snapshot (add `#2`, `#3` suffixes deterministically only if a collision is proven possible — report it).

Update all code that reads `Attributes` as strings (extractor's controller detection, any CLI usage) to use `.Name` — behaviour identical.

**Tests:** extraction tests for: namespace/kind/modifiers (class, abstract class, static class, record, interface, generic type, nested type → `ContainingType`); callable modifiers (static, virtual, override with `Overrides` pointing to the base declaration, abstract, generic, extension, operator, explicit interface implementation); attribute arguments (`[Route("orders")]`-style positional + named arguments — use a locally declared attribute in the in-memory source); keys stable and free of absolute paths/version for a method, a local function and a lambda; `RelativeTo`.

**Expected output changes:** `inspect-json.txt` gains the new fields; `inspect.txt` / `self-inspect.txt` `Types:`/`callables:`/`dependencies:` counts may grow **only** because overridden members (and their types) are now registered — list the before/after counts in the report and explain them. All other text outputs byte-identical.

---

### Task 4: Member dependencies and implementation candidates

**Files:** `Facts.cs`, `FactExtractor.cs`, tests.

1. `DependencyKind` gains `Field` and `Property` (append). For every source type: each declared non-implicit field → `DependencyFact(OwningType, null, Use(field.Type), Field, evidence of declarator)`; each declared property → `Property` with the property declaration evidence. Skip compiler-generated backing fields.
2. Add `enum ImplementationKind { Interface, Override }` and `sealed record ImplementationFact(CallableId Declared, CallableId Implementation, ImplementationKind Kind)`; `ProgramSnapshot.Implementations` (sorted by Declared, then Implementation, ordinal).
   - Interface: for each source non-interface type, for each method member (including accessors) of `AllInterfaces`, `FindImplementationForInterfaceMember`; record the pair when the implementation is a source method. Register the interface member via `RegisterMethod`.
   - Override: for each source method with `Overrides`, record `(overridden, override)`; this covers abstract and virtual members. Do this for the whole override chain step-by-step (each step one fact), not transitively flattened.
   - Deduplicate. These are **candidates** for consumers to opt into; the model doc/comment must say they are not inferred runtime targets.

**Tests:** in-memory extraction: interface with two implementations → two facts; explicit interface implementation; abstract base + override; virtual base + override of override (two facts); property accessor implementation; field and property dependencies with kinds and evidence; backing fields absent. Fixture-level test: the fixture's interface-dispatched call has at least one candidate implementation fact (assert concrete IDs by display signature).

**Expected output changes:** `inspect-json.txt` new data; `inspect.txt`/`self-inspect.txt` `dependencies:`/`callables:`/`types:` counts may grow — list before/after and why. All other text outputs byte-identical.

---

### Task 5: ProgramIndex and query/report refactor

**Files:** new `src/Sharpview.Core/Index/ProgramIndex.cs`; `CallChainQuery.cs`, `ExceptionQuery.cs`, `Layers.cs`, `src/Sharpview.Cli/TextReport.cs`, `Commands.cs`; tests.

`public sealed class ProgramIndex` built once from a `ProgramSnapshot` (O(n) construction, dictionary lookups after). Public surface (adapt names to code style, keep it small and documented):
- `Snapshot`; `Callable(CallableId)` / `TryGetCallable`; `Type(TypeId)` / `TryGetType`; `Compilation(CompilationId)`; `ProjectName(CompilationId)` (project file name without extension; `"metadata"` when unknown — same as `TextReport.Project` today); `DeclaringType(CallableId)`.
- `Outgoing(CallableId)` → call sites ordered by `TargetDisplay`, then `Id` (ordinal); `Incoming(CallableId)` → sites whose `BoundTarget` is the callable, ordered by `Id`.
- `ThrowSites(CallableId owner)`, `Catches(CallableId owner)`, `ThrowSite(ThrowSiteId)`, `Catch(CatchId)`, `Region(ExceptionRegionId)`, `Gaps(CallableId owner)`.
- `Ancestors(TypeId)` (transitive `BaseTypes`, deterministic, cycle-safe), `Members(TypeId)` (callables by declaring type).
- `Implementations(CallableId declared)` and `ImplementedBy`/`Declarations(CallableId implementation)` from `ImplementationFact`s.

Refactor `CallChainQuery`, `ExceptionQuery`, and `TextReport` to use the index (no `snapshot.Types.FirstOrDefault(...)` / `Compilations.FirstOrDefault(...)` per line). Add `Enumerate(ProgramIndex, …)` and `Analyze(ProgramIndex, …)` overloads; keep the existing `ProgramSnapshot` overloads as one-line delegations. `CallIndex` stays public (build it from the index or keep as is — no behavioural change). `Commands.cs` builds the index once per run.

**Tests:** unit tests for each index lookup on a small Core snapshot (ordering, missing IDs, ancestor cycle safety, implementations both directions). Existing query tests unchanged and passing.

**Expected output changes:** none — every capture file byte-identical. Report self-analysis timings before/after.

---

### Task 6: Structured reason codes

**Files:** `ExceptionQuery.cs`, tests (CLI untouched).

Add `enum ExceptionReason` with one member per distinct reason the query emits today, e.g. `InterceptedByHandler`, `MayEscape`, `ChangedMayEscape`, `MissingBody`, `UnresolvedTarget`, `DispatchBoundary`, `Cycle`, `DepthLimit`, `ExtractionGap`, `OriginInHandler`, `AsyncUnsupported`, `UnobservedTask`, `FinallyUnsupported`, `HandlerTypeUncertain`, `CatchFilter`, `HandlerFlowUnsupported`, `ReplacementUnavailable`, `UnboundExceptionType` (derive the exact list from the code; one code per current message, no merging of different meanings). Add `ExceptionReason ReasonCode` to `ExceptionWitness` (append, keep `Reason` text identical). For `DispatchBoundary` keep the dispatch kind visible: add `DispatchKind? Dispatch` to the witness, or document how consumers obtain it.

**Tests:** extend existing CoreTests that assert on `Reason.Contains(...)` to also assert the code; add a test ensuring every emitted witness in the fixture run has a code consistent with its status (Intercepted ↔ InterceptedByHandler, MayEscape ↔ MayEscape/ChangedMayEscape, Unknown ↔ an Unknown code).

**Expected output changes:** none.

---

### Task 7: Layer architecture model, layer graph, allowed-dependency rule, `layers` command

**Files:** new `src/Sharpview.Core/Architecture/` (`LayerModel.cs`, `LayerMap.cs`, `LayerGraph.cs`), new `src/Sharpview.Core/Rules/` (`Finding.cs`, `LayerRules.cs`); remove `src/Sharpview.Core/Queries/Layers.cs`; CLI `Arguments.cs`, `Commands.cs`, `TextReport.cs`; tests.

Core:
- `enum LayerScope { Project, Namespace }`; `LayerMapping(LayerScope Scope, string Prefix, string Layer)`; `AllowedDependency(string From, string To)`; `LayerModel(ImmutableArray<LayerMapping> Mappings, ImmutableArray<AllowedDependency> Allowed)`.
- `LayerMap` (built from `ProgramIndex` + `LayerModel`, cached): `LayerOf(TypeId)`, `LayerOf(CallableId)` (callable uses its compilation's project name and its declaring type's namespace). Precedence identical to today's `LayerQuery`: longest prefix wins, project wins a tie. Project matching unchanged (`StartsWith(prefix, Ordinal)` on project name). **Namespace matching uses `TypeFact.Namespace`**: `ns == prefix || ns.StartsWith(prefix + ".")` (documented semantics; today it matched the type's display name, which additionally matched type-name prefixes — that accidental behaviour is dropped; call it out in docs).
- `LayerGraph.Build(ProgramIndex, LayerMap, CancellationToken)` → edges keyed by `(From, To, Kind)` with `enum LayerEdgeKind { DirectCall, DeclaredCall, CandidateCall, TypeDependency }`:
  - `DirectCall`: call sites with `Dispatch == Direct` and a bound target.
  - `DeclaredCall`: non-direct sites with a bound (declared) target — the declared binding's layer.
  - `CandidateCall`: for non-direct sites, caller layer → each implementation candidate's layer (from `Implementations`). Marked candidate, never runtime.
  - `TypeDependency`: `DependencyFact`s from owning type (or owning callable's declaring type) to the referenced declaration.
  - Each edge: `Count`, and one deterministic witness (minimum by evidence path, then span start) carrying source evidence and the source/target IDs.
  - Source-code elements with no mapping → layer `"(unmapped)"`. Metadata (non-source) targets with no mapping are excluded from edges and counted in `ExternalReferences` (never silently dropped). Same-layer edges are included.
  - Deterministic order: From, To, Kind (ordinal).
- `Finding(string RuleId, Verdict Verdict, string Subject, string Message, ImmutableArray<SourceEvidence> Evidence)` with `enum Verdict { Violation, Pass, Unknown }`.
- `LayerRules.AllowedDependencies(LayerGraph, LayerModel)`: when `Allowed` is empty → no findings. Otherwise for each edge with `From != To` and kind ≠ `CandidateCall`: both mapped and allowed → no finding (passes are implicit; do not emit noise); both mapped and not allowed → `Violation`; either side `"(unmapped)"` → `Unknown`. RuleId `"layer-dependency"`.

CLI:
- `--layer` parsing produces `LayerScope` (same accepted syntax). New repeatable `--allow <From>=<To>[,<To>…]`.
- New command `layers <input> --layer … [--allow …]` (requires ≥1 `--layer`; rejects root/traversal options). Text format (stdout):
  ```
  Layer edge: API --> Application | DirectCall | 2 | Evidence: <path>:<line>:<col>
  ...
  Finding [layer-dependency] Violation: Infrastructure --> API (TypeDependency) | Evidence: <path>:<line>:<col>
  ```
  stderr summary line: layers, edges, external references, violations, unknown. Exit 1 when gaps, violations or unknown findings exist, else 0 (consistent with other commands). Update `--help`.
- `TextReport`'s `Layer:` column uses `LayerMap` (identical output on the fixture).

**Tests:** LayerMap precedence (longest prefix, project tie-break, nested namespaces, global namespace, metadata types); LayerGraph on a Core snapshot (each edge kind, counts, witness determinism, unmapped, external references); rule verdicts (allowed, violation, unknown, candidate excluded, empty Allowed → none); CLI parsing of `--allow` / `layers`; fixture run of `layers` with the README's three mappings asserting at least `API --> Application` and `Application --> Infrastructure` call edges.

**Expected output changes:** none for existing commands. Add `layers` runs to `capture.sh` is **not** allowed (baseline has none); instead paste the fixture `layers` output into the report.

---

### Task 8: Reachability with shortest witnesses, `reach` command

**Files:** new `src/Sharpview.Core/Queries/ReachabilityQuery.cs`; CLI `Arguments.cs`, `Commands.cs`, `TextReport.cs`; tests.

Core:
- `ReachOptions(bool FollowCandidates = false, int MaxDepth = 20, int MaxExpansions = 100000)` with `Validate()`.
- `ReachabilityQuery.Analyze(ProgramIndex, ImmutableArray<CallableId> roots, ReachOptions, CancellationToken)` → `ReachResult(ImmutableArray<RootReach> Roots, bool WorkLimitReached, int ExpandedSites)`.
  - Per root, BFS. `RootReach(CallableId Root, ImmutableArray<ReachedCallable> Reached, ImmutableArray<ReachBoundary> Boundaries, bool DepthLimitReached)`.
  - `ReachedCallable(CallableId Callable, int Distance, ImmutableArray<ReachStep> Witness, bool ViaCandidate)`; `ReachStep(CallSiteId Site, CallableId Target, bool Candidate)`. Witness = first shortest path found with neighbours visited in `ProgramIndex.Outgoing` order (deterministic). `Distance` = transitions (not stack depth).
  - Edges: `Direct` sites with a known bound target. Non-direct sites: always recorded as a `ReachBoundary(CallableId Caller, CallSiteId Site, ReachBoundaryKind Kind, DispatchKind Dispatch, CallableId? DeclaredTarget, int CandidatesFollowed)`; when `FollowCandidates`, also traverse to each implementation candidate (for virtual non-abstract declared targets with a source body, the declared target itself is a candidate too) and mark those steps `Candidate = true`.
  - Unresolved targets → boundary `Unresolved`; reached callables without a source body are listed as reached and also produce a `MissingBody` boundary (not expanded); depth limit → `DepthLimitReached` and boundary `DepthLimit`; budget counts examined sites (like `CallChainQuery`) and sets `WorkLimitReached`.
  - Reached list ordered by Distance, then display signature, then ID.
- CLI `reach <input>`: roots default to controller actions (like `controllers`), `--root`/`--root-id`, `--max-depth`, `--max-expansions`, `--layer`, `--follow-candidates`, `--locations`. Output per root:
  ```
  Root: Research.Api.OrdersController.Get()
    [1] Research.Application.Service.Load() | Project: Research.Application | Layer: Application
    [2] Research.Infrastructure.Repository.Read() | Project: … | Layer: … (candidate)
      Path: A --> B --> C
    Boundary: Interface dispatch --> IRepo.Read() | <path>:<line>:<col>
  ```
  (`Path:` lines always printed for distance ≥ 2; `--locations` adds per-step evidence.) stderr summary; exit codes as other commands. Update `--help`.

**Tests:** diamond (shared node reached once, distance 2, deterministic witness); cycle terminates; depth limit; work limit; unresolved/missing body boundaries; interface boundary without follow; with follow → candidate reached and `ViaCandidate`; virtual-with-body candidate includes declared target; fixture CLI run (`reach --root 'Research.Api.OrdersController.Get()'`) contains the Service and Repository callables.

**Expected output changes:** none for existing commands. Paste the fixture `reach` output into the report.

---

### Task 9: Controller-action classification in Core

**Files:** new `src/Sharpview.Core/Conventions/AspNetConventions.cs`; `Facts.cs`, `FactExtractor.cs`, `Commands.cs`, tests, README library snippet.

- `AspNetConventions.ControllerActions(ProgramIndex)` → `ImmutableArray<ControllerActionFact>` computed purely from facts, replicating `FactExtractor.IsControllerAction` exactly: source callable of kind `Method` without `Operator | ExplicitImplementation | Finalizer | Static | Generic` modifiers and with `Accessibility == "Public"`; no `Microsoft.AspNetCore.Mvc.NonActionAttribute` on it or any member of its `Overrides` chain; declaring type not abstract, not generic; no `NonControllerAttribute` on the type or its `BaseClass` chain; type or a `BaseClass`-chain ancestor is `Microsoft.AspNetCore.Mvc.ControllerBase` or carries `Microsoft.AspNetCore.Mvc.ControllerAttribute`. Same `Convention` text, same ordering as today's snapshot array.
- First add a test that, on the fixture and on every in-memory extraction case that exercises controller detection, the Core classification equals the extraction-time `ControllerActions` array. Only once that passes: delete extraction-time detection and `ProgramSnapshot.ControllerActions`; CLI and tests use `AspNetConventions.ControllerActions`. `ControllerActionFact` remains as the result type. `BoundaryHandlingFact` stays in the snapshot (it is invocation evidence that needs Roslyn).
- If a fact needed for exact equivalence is missing (e.g. metadata base-type attributes), add it at extraction rather than approximating; report what was added.

**Tests:** the equivalence test (kept, now comparing against a hard-coded expected set since the extraction array is gone), plus Core snapshot tests: NonAction on overridden base, NonController on base class, abstract/generic controller, static/generic/operator/explicit-implementation methods excluded, `[Controller]` POCO included.

**Expected output changes:** `inspect-json.txt` loses `controllerActions`; `inspect.txt` `actions:` count unchanged (computed). All other text outputs byte-identical.

---

### Task 10: Documentation, samples, final comparison

**Files:** `README.md`, `docs/model.md`, `docs/coverage.md`, `docs/validation.md`, `docs/samples/*`, `docs/superpowers/plans/2026-10-12-csharp-call-chain-research-plan.md` (append a short "Consumer model increment" section pointing to this plan).

- README: commands list (`layers`, `reach`, `--allow`, `--follow-candidates`), JSON schema 3, library snippet using `ProgramIndex`, `AspNetConventions`, `LayerMap`/`LayerGraph`/`LayerRules`, `ReachabilityQuery`.
- model.md: new facts (namespace/kind/modifiers, attributes with arguments, overrides, implementation candidates and why they are candidates, field/property dependencies, stable keys vs local IDs, `SourceRoot`), `ProgramIndex`, layer semantics (incl. namespace-matching change), `Finding`/`Verdict`, reachability vs path enumeration, reason codes.
- coverage.md / validation.md: what is covered, new tests, and a "Regression comparison" subsection summarising the final `diff -r baseline final` result.
- Regenerate `docs/samples/*` with the commands in `docs/samples/README.md` (must be identical to the committed ones apart from anything listed in earlier tasks) and add `layers.txt` and `reach.txt` samples (plus `.stderr.txt`), documenting the commands in `docs/samples/README.md`.

**Tests:** full suite; final capture compared with baseline; only differences accumulated from earlier tasks' "Expected output changes".
