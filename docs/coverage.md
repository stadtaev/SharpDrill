# Coverage and limits

What SharpDrill models today, and what it deliberately does not. "Boundary" and "Unknown" results are how the limits show up in reports; nothing outside the model is silently dropped.

## Inputs and loading

- Restored SDK-style C# projects and solutions (`.csproj`, `.sln`, `.slnx`), hosted on .NET 10, loaded through the SDK's MSBuild workspace (no Visual Studio).
- One configuration/target-framework variant per project. An explicit `--framework` applies to the whole input and must be declared by every selected project.
- Missing restore assets in a selected project make the CLI run `dotnet restore` on the input and load again (`--no-restore` aborts instead; the `SharpDrill.Roslyn` library always aborts). No build is needed unless compile errors are reported: code generated during a build is then missing. Workspace or compiler failures that still produce a compilation become visible gaps.
- Project evaluation happens before the assets check, so project tooling and source generators may execute. SharpDrill never builds or edits the analysed source.
- The CLI keeps the last analysis per input and load options in the user's local application data folder (`SHARPDRILL_CACHE_DIR` overrides it) and reuses it while nothing the load read has changed: this exact tool build, code and project files by content (documents, MSBuild imports, restore assets, `.cs`, `.cshtml`, `.razor`, project and config files anywhere under a project folder, config files in folders above it), and referenced assemblies by size and time. Results with compile errors or load failures are never kept. Not tracked: environment variables, and a new non-source file that a project's own glob turns into an additional file; `--no-cache` covers both.
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
| Normal invocations, explicit construction, source constructors | Interface and virtual calls at their declared target, including virtual members with bodies | Top-level statements (one gap per file) |
| Property/event accessors, user-defined operators and conversions | Delegates, `dynamic`, function pointers, invalid operations | |
| Reduced extension methods; local functions and lambdas as separately owned bodies | Metadata-only (library) bodies | `await` of a task held in a property or parameter, or in a field whose producing calls are not provable (below) |
| Field/property initialisers, as code of the constructors that run them (static ones in the static constructor, which no call path reaches) | | |

Implementation candidates come from source types' interface maps and overrides; dependency-injection registrations are not read. `exceptions` and `diagram` follow them by default, marked as possibilities; `--declared-only` stops at the declared target.

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
  - throws inside an `async` method returning neither `Task`, `ValueTask` nor `void` (an async iterator or custom task type). Awaiting a non-`async` method is not this case, whatever its declared return type (e.g. a generic `T` used as a `Task`): its throws surface at the call and propagate;
  - tasks that are never awaited (`var t = CallAsync(); ... await t;`, `await Task.WhenAll(t, u)` and `return CallAsync();` count as awaited; only a `Task`/`ValueTask` result can be observed later, so a string local passed as an argument inside an `await` does not make its call awaited);
  - `await` of a task in a field, unless its producing calls are provable: the field is private or readonly, every write is a call, and no such call can throw at the assignment (a non-`async` source method does). Then what those calls throw surfaces at the `await`, inside that body's `try` blocks, and not at the assignment. Returning such a field (`Task Get() => _task`) hands it to whoever observes the result. This is one implicit call site per producing call, so a call diagram also draws that edge;
  - `Task.WhenAll` of a collection (its tasks are not known); `.Result`, `.Wait()` and `Task.WaitAll` on a stored task (they throw `AggregateException`, not modelled).
  - dispatch through an interface or virtual member that source implements or overrides, when `--declared-only` is given (the default follows it);
  - an exception leaving an iterator: it surfaces where the sequence is enumerated, not at the call, so a caller's `try` does not decide it. The iterator's own `try` blocks apply, and an iterator nothing can leave is not uncertain.
- **The BCL `Task` API is modelled, not treated as an opaque library:** `await` and `GetAwaiter().GetResult()` observe a task, through `ConfigureAwait`. `Task.WhenAll(a, b)` observes each task where the `WhenAll` is observed. `await Task.WhenAny(a, b)` completes normally whatever they hold, so it observes neither. `Task.Run` runs its delegate: when its task is observed the delegate's exceptions surface there, and when it is not they reach nobody. Not modelled: `Task.Factory.StartNew`, `ContinueWith`.
- **async void:** its own `try/catch` applies as in any method. What still leaves it never reaches a caller (it is rethrown on the thread pool and ends the process), so it is a finding (`Crashes`, SD0005), not Unknown, and a `try` around the call does not catch it.
- **Recursion is not uncertainty.** All views share one fixed-point propagation, so a recursive call is followed like any other.
- **Library calls are not uncertainty.** A call to a method with no source body (framework or NuGet code) ends the trace there: its exceptions are not modelled, so it never makes a result Unknown. Exception views report how many library methods were called, and `--uncaught` lists a few per action. This applies to direct calls and to interface/virtual calls to library members that no source type overrides.
- **Compiler-provided members are known.** Implicit and record constructors, auto-property accessors and record members of source types have no explicit code, so they cannot throw explicitly and are never "missing".
- **`foreach` and `using`** record their implicit `GetEnumerator`/`MoveNext`/`Dispose` calls, bound through the receiver type like an explicit call. A `Dispose` written in source is analysed; library enumerators are library calls. `await foreach` and `await using` record the same calls (`MoveNextAsync`, `DisposeAsync`) as awaited.
- **Interface and virtual calls are followed by default** in the exception views, into their source implementation candidates (`--declared-only` stops at the declaration instead). Results that pass through one are marked as possibilities, never as proven runtime paths; a proven direct path to the same throw takes precedence. A call with no source implementation stays Unknown ("no source implementation found").
- **Not modelled:** implicit exceptions (from constructors, getters, framework calls) and branch feasibility. All branches and catch-body calls are structural facts.
- **Miscaught:** reported only when every catch clause of a crossed `try` definitely does not match the exception's type at that point. A filtered or runtime-dependent clause is a possible catch, never a miss.
- **Strict mode** (`--uncaught --strict`) also treats every call into a third-party assembly as an origin and lists, in a section of its own, the calls that no catch meets on the way from the action. Third-party means a referenced assembly that no .NET framework reference pack supplies (NuGet packages and directly referenced DLLs; `Microsoft.*.App.Ref`, `NETStandard.Library` and the shared runtime are .NET itself). Any catch clause around the call or around a caller's call site wraps it, whatever its type; a catch that only rethrows, or whose filter may decline, does not. Global handlers do not count: the rule asks for a catch around the call. Logging (Serilog, NLog, log4net) and `Microsoft.Extensions.*` calls are left out, because they are called everywhere and nobody wraps them; `--verbose` includes them. `Microsoft.Extensions.Caching.*` always stays, because a distributed cache can be a remote store.
- **Global handlers:** Exceptions that escape an action whose host registers `UseExceptionHandler`, `UseDeveloperExceptionPage` or a global MVC exception filter (`Filters.Add`/`AddService` of an `IExceptionFilter`/`IAsyncExceptionFilter`) are listed last, each with a `Handled:` line naming the registration. The host is the action's project or a project that references it. `AddExceptionHandler` alone handles nothing. This is registration evidence: it does not prove middleware order, environment conditions (for example a developer page registered only in Development) or what the handler returns. Exception filters applied as attributes on a controller or action are not modelled.
- **Wording:** a catch is never reported as successful recovery. "May escape" is scoped to the action under this model.

## Projects

- **From MSBuild:** `<ProjectReference>` items and the evaluated `TargetFramework`, `Nullable`, `TreatWarningsAsErrors` and `WarningsAsErrors` of each loaded C# project, for the selected configuration and framework.
- **From the compiler:** warning and error counts after severity options. Analyzer, NuGet and other MSBuild warnings are not counted.
- **Test projects** are skipped unless `--include-tests` is given: `IsTestProject=true` or `SonarQubeTestProject=true`, or a package reference from the xUnit, NUnit, TUnit, MSTest, SpecFlow, Reqnroll, LightBDD, Cucumber or PactNet families, or Microsoft.NET.Test.Sdk, in any target framework's evaluation, or a project reference to such a project (a shared Pact fixture library, say). Skipping is the default scope, not a gap.
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

Tests combine independently built Core snapshots, in-memory Roslyn compilations, a three-project Web API fixture and fresh CLI processes (see [validation](validation.md)). SharpDrill's own solution is used only as a loader smoke input; no external Web API analysis is claimed yet.


## Graph analysis and diagram exports

- Generic directed multigraph: both adjacency directions, parallel edges, deterministic insertion order,
  quotient groups with original memberships (including isolated nodes), and iterative SCC discovery.
- Exception summaries: one monotone worklist fixed point from explicit origins up through their callers, per-site transfers, finite fact identities,
  shortest example witnesses (breadth-first over every recorded provenance link), separate uncertainty and work/fact limits. Caught and miscaught are read from the same propagation: each transfer step records the catches it matched and the `try` blocks it passed, and a miss's outcomes are the fates of what got past it. Ownerless gaps (top-level statements, minimal APIs) belong to no root and do not make a result incomplete; owned gaps remain associated with reached methods.
  Unknown propagation is conservative; summaries do not remove arbitrary unknown effects merely because a catch-all exists.
- Mermaid: one controller action selected interactively or by exact signature/ID, project groups, branches/shared nodes/cycles,
  parallel sites, escaped labels, evidence, uncertainty boundaries and visible truncation. Default limits apply after loading
  and whole-program graph preparation. Inherited actions, minimal APIs and implicit database/framework operations remain out
  of scope; a graph ends at the bodies/bindings the extractor can establish.
