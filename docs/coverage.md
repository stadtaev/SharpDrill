# Coverage and limits

What SharpView models today, and what it deliberately does not. "Boundary" and "Unknown" results are how the limits show up in reports; nothing outside the model is silently dropped.

## Inputs and loading

- Restored SDK-style C# projects and solutions (`.csproj`, `.sln`, `.slnx`), hosted on .NET 10, loaded through the SDK's MSBuild workspace (no Visual Studio).
- One configuration/target-framework variant per project. An explicit `--framework` applies to the whole input and must be declared by every selected project.
- Missing restore assets in any selected project abort loading. Workspace or compiler failures that still produce a compilation become visible gaps.
- Project evaluation happens before the assets check, so project tooling and source generators may execute. SharpView never builds, restores or edits the analysed source.
- Generated documents are included by default; `--exclude-generated` records each exclusion as evidence.

## Controller actions

Recognised by convention, classified in Core from extracted facts:

- **Included:** declared, public, instance, ordinary, non-generic methods on non-abstract, non-generic source types that derive from ASP.NET Core `ControllerBase` or carry `[Controller]`.
- **Excluded:** methods with inherited `[NonAction]`, types with inherited `[NonController]`.
- **Out of scope:**
  - inherited action enumeration;
  - discovering controllers by name alone;
  - application-model conventions;
  - endpoint reachability;
  - minimal APIs. Recognised minimal-API registrations produce gaps, and the CLI always prints these limits.

## Calls

| Supported (followed) | Boundary (recorded, not followed) | Gap (not modelled, listed) |
| --- | --- | --- |
| Normal invocations, explicit construction, source constructors | Interface and virtual calls, including virtual members with bodies (an explicit root may still inspect its own body) | Top-level statements (one gap per file) |
| Property/event accessors, user-defined operators and conversions | Delegates, `dynamic`, function pointers, invalid operations | Iterator bodies (one gap per iterator) |
| Reduced extension methods; local functions and lambdas as separately owned bodies | Metadata-only (library) bodies | `await` of a task held in a field, property or parameter |
| Field/property initialisers, as code of the constructors that run them (static ones in the static constructor, which no call path reaches) | | |

Implementation candidates come from source types' interface maps and overrides; dependency-injection registrations are not read.

## Exceptions

- **Origins:** explicit `throw`, throw expressions and rethrows, with semantic types. `throw new E()` has an exact construction type; `throw e` only its static type.
- **Handlers:** a catch applies only to a region that protects the throw or the call. What the handler does comes from its control flow (Roslyn's control-flow analysis), so logging before `throw;` or before `return` does not matter:
  - every exit leaves normally (fall-through, `return`, `break`): it *handles* the exception;
  - every exit is `throw;` or `throw e` of the catch variable: it *rethrows*, and the original exception continues;
  - some exits rethrow, others leave normally: it *may rethrow*, and the exception is followed further as a possibility;
  - every exit throws something else: it *replaces* the exception. The original ends there; the new `throw` is an origin of its own, like every throw inside a catch block.
- **Filters and undecided matches:** a `catch ... when (...)`, or a narrow catch of an exception whose static type is a base class, may or may not take it. It is reported as a possible catch and the exception is followed past it.
- **`finally`** is transparent unless it can throw itself (a `throw` or a call into source code); only then is the result Unknown.
- **Unknown, with a reason:**
  - a `finally` that may throw;
  - custom awaitables, async void, and tasks that are never awaited (`var t = CallAsync(); ... await t;` and `await Task.WhenAll(t, u)` count as awaited);
  - dispatch through an interface or virtual member that source implements or overrides, when `--declared-only` is given (the default follows it);
  - iterator bodies and recursion in the per-path views.
- **Library calls are not uncertainty.** A call to a method with no source body (framework or NuGet code) ends the trace there: its exceptions are not modelled, so it never makes a result Unknown. Exception views report how many library methods were called, and `--uncaught` lists a few per action. This applies to direct calls and to interface/virtual calls to library members that no source type overrides.
- **Compiler-provided members are known.** Implicit and record constructors, auto-property accessors and record members of source types have no explicit code, so they cannot throw explicitly and are never "missing".
- **`foreach` and `using`** record their implicit `GetEnumerator`/`MoveNext`/`Dispose` calls, bound through the receiver type like an explicit call. A `Dispose` written in source is analysed; library enumerators are library calls. `await foreach` and `await using` record the same calls (`MoveNextAsync`, `DisposeAsync`) as awaited.
- **Interface and virtual calls are followed by default** in the exception views, into their source implementation candidates (`--declared-only` stops at the declaration instead). Results that pass through one are marked as possibilities, never as proven runtime paths; a proven direct path to the same throw takes precedence. A call with no source implementation stays Unknown ("no source implementation found").
- **Not modelled:** implicit exceptions (from constructors, getters, framework calls) and branch feasibility. All branches and catch-body calls are structural facts.
- **Miscaught:** reported only when every catch clause of a crossed `try` definitely does not match the exception's type at that point. A filtered or runtime-dependent clause is a possible catch, never a miss.
- **Wording:** a catch is never reported as successful recovery. "May escape" is scoped to the action under this model. Registration evidence (`UseExceptionHandler`, `AddExceptionHandler`, `UseDeveloperExceptionPage`) does not prove middleware order, environment conditions, invocation, response availability or complete coverage.

## Projects

- **From MSBuild:** `<ProjectReference>` items and the evaluated `TargetFramework`, `Nullable`, `TreatWarningsAsErrors` and `WarningsAsErrors` of each loaded C# project, for the selected configuration and framework.
- **From the compiler:** warning and error counts after severity options. Analyzer, NuGet and other MSBuild warnings are not counted.
- **Test projects** are skipped unless `--include-tests` is given: `IsTestProject=true`, or a package reference from the xUnit, NUnit, TUnit, MSTest, SpecFlow, Reqnroll, LightBDD, Cucumber or PactNet families, or Microsoft.NET.Test.Sdk. Skipping is the default scope, not a gap.
- **Not covered:** package references, non-C# projects (reported as gaps) and references to projects outside the loaded workspace (listed as unloaded).

## Layers and dependencies (library only)

- Layers require explicit `project:`, `namespace:` or `type:` prefix mappings through `LayerModel`; the CLI no longer exposes them.
- **Dependency sources:**
  - call sites;
  - signatures and inheritance, where base types contribute their type arguments;
  - construction and body use;
  - field and property types.
- **Not dependency sources:** top-level statements, attributes, events and generic constraints.

## Traversal

- Distances are static call transitions from a root, not stack depth.
- Cycles and depth/work/output limits are always visible; a global limit means the result is not exhaustive.
- Cancellation propagates through loading, extraction and traversal.

## How this is verified

Tests combine independently built Core snapshots, in-memory Roslyn compilations, a three-project Web API fixture and fresh CLI processes (see [validation](validation.md)). SharpView's own solution is used only as a loader smoke input; no external Web API analysis is claimed yet.


## Graph analysis and diagram exports

- Generic directed multigraph: both adjacency directions, parallel edges, deterministic insertion order, phased shortest-path
  relaxation, quotient groups with original memberships (including isolated nodes), and iterative SCC discovery.
- Reach: shared immutable parent links, lazy witness arrays, separate direct-only distance/witness and candidate-inclusive routes, per-root work
  truncation, and explicit dispatch/unresolved/body/depth boundaries. A work stop can leave a valid nonoptimal path.
- Exception summaries: bottom-up direct-call SCC fixed points over explicit origins, per-site transfers, finite fact identities,
  reconstructed example witnesses, separate uncertainty and work/fact limits. Detailed witness enumeration is still available. Ownerless gaps (top-level statements, minimal APIs) belong to no root and do not make a result incomplete; owned gaps remain associated with reached methods.
  Unknown propagation is conservative; summaries do not remove arbitrary unknown effects merely because a catch-all exists.
- Mermaid: one controller action selected interactively or by exact signature/ID, project groups, branches/shared nodes/cycles,
  parallel sites, escaped labels, evidence, uncertainty boundaries and visible truncation. Default limits apply after loading
  and whole-program graph preparation. Inherited actions, minimal APIs and implicit database/framework operations remain out
  of scope; a graph ends at the bodies/bindings the extractor can establish.
