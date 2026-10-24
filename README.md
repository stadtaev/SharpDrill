# SharpView

SharpView is a C# research tool for understanding ASP.NET Core Web APIs from source:

- **Project map:** the real `<ProjectReference>` tree, with each project's framework, nullable settings and compiler warnings.
- **Exceptions:** every explicit throw, and which ones go uncaught, get caught, or pass through a `try` whose catches don't match them.
- **Call paths and reachability** from controller actions, project by project.
- **Mermaid diagrams** of the project tree or of one controller action.

Results carry source evidence (file:line:column), and anything the analysis cannot decide is reported as *Unknown* instead of being hidden. It uses the .NET SDK and Roslyn; Visual Studio is not required.

**New here? Read [How SharpView works](docs/overview.md)** — pipeline, principles, what each command answers, and how to write your own rule.

## Install

```sh
dotnet tool install -g SharpView   # requires a .NET 10 or newer SDK
cd MyApi && sharpview projects   # the .sln/.slnx (or single .csproj) in the current directory; or pass a path
```

Update with `dotnet tool update -g SharpView`. The examples below run from source with `dotnet run --project src/SharpView.Cli --`; with the tool installed, use `sharpview` instead.

## Quick start (from source)

Requires a .NET 10 SDK compatible with `global.json`.

```sh
dotnet restore sharpview.slnx
dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx   # sample Web API used below
dotnet test sharpview.slnx
```

Try it on the sample (`F` is the input):

```sh
F=tests/Fixtures/ResearchSolution/ResearchSolution.slnx

dotnet run --project src/SharpView.Cli -- projects $F
dotnet run --project src/SharpView.Cli -- exceptions $F
dotnet run --project src/SharpView.Cli -- exceptions $F --miscaught
dotnet run --project src/SharpView.Cli -- reach $F --root 'Research.Api.OrdersController.Get()'
dotnet run --project src/SharpView.Cli -- diagram $F
```

Analysing your own solution: restore it first (SharpView never restores, builds or edits analysed code). Loading evaluates MSBuild projects and may run project tooling and source generators, so it is not a sandbox.

## Commands

| Command | Question it answers | Default roots |
| --- | --- | --- |
| `projects` | How do the projects reference each other, and how is each one built (framework, nullable, warnings as errors, compiler warnings)? `--mermaid` also saves the tree as a diagram. | — |
| `exceptions` | Where is each explicit exception thrown (method, type, project)? | — |
| `exceptions --uncaught` | Which throws may escape an action because no catch is on the way? One example path per origin. | controller actions |
| `exceptions --caught` | Which throws reach a matching catch, and where are they thrown and caught? | controller actions |
| `exceptions --miscaught` | Which throws pass through a `try` whose catch clauses don't match their type? | controller actions |
| `reach` | What can each action reach: once per method, with distance, project and shortest path? | controller actions (or any `--root`) |
| `paths` | What are all call paths from each action (or any `--root` method), with project and transitions? | controller actions |
| `diagram` | Export a Mermaid call graph in Markdown, grouped by project. | pick one controller action by number |
| `inspect` | Extraction summary; `--methods` lists callables with their IDs (to pick a `--root`); `--json` writes the full fact snapshot. | — |

For practical use cases of each command, such as impact analysis, CI gating, finding roots or debugging extraction, see [When to use which command](docs/overview.md#when-to-use-which-command). Sample outputs for each are in [docs/samples](docs/samples/README.md). Run `--help` for the complete option list.

### Common options

- **Input:** `.csproj`, `.sln` or `.slnx`. Use `--framework` for multi-target projects and `--configuration` (default `Debug`) when needed.
- **Test projects** (xUnit, NUnit, TUnit, MSTest, SpecFlow/Reqnroll, LightBDD, Cucumber, Pact, or anything with `IsTestProject=true`) are skipped by default, before restore or framework checks. `--include-tests` loads them like any other project.
- **Roots:** `--root <id or exact signature>`; copy either from `inspect --methods`. An exact ID match wins, so use the ID when signatures collide. `reach` and `paths` accept any source method; `exceptions` and `diagram` need a controller action.
- **Dispatch:** interface, virtual, delegate, dynamic and unresolved calls are visible boundaries. `reach --follow-candidates` and `diagram --follow-candidates` also follow implementation candidates, marked `(candidate)`. `paths` stops at the declared target. Exception views follow interface/virtual calls into their source implementations by default (the usual case with dependency-injected interfaces) and mark such results as possibilities, never as proven runtime paths; `exceptions --declared-only` stops at the declaration and reports *Unknown* instead.
- **Limits:** `--budget` (default 100,000 examined call sites) everywhere; `reach`, `paths` and `diagram` also take `--max-depth` (default 20 transitions), `paths` takes `--max-paths` (default 1,000). `exceptions` has one knob only, `--budget`: its depth is generous and its witness cap scales with the budget. Reaching a limit is always reported, with what to raise.
- **Strict options:** each command accepts only its own options (`--help` lists them per command). Anything else fails before loading, naming the command.
- **Evidence:** `--locations` prints call-site locations for paths and reach witnesses.
- **Output:** `inspect --json` writes schema version 3 (IDs are plain strings) to stdout or `--output <new file>` (never overwrites). `projects --mermaid` and `diagram` save Markdown to `--output` or to `diagrams_exports` (see below). Diagnostics go to stderr. Ctrl+C cancels cleanly.

### Export a diagram

```sh
# Lists numbered controller actions; enter a number, or q to cancel.
dotnet run --project src/SharpView.Cli -- diagram tests/Fixtures/ResearchSolution/ResearchSolution.slnx

# Select directly for scripts. --root also accepts an action ID from inspect --methods.
dotnet run --project src/SharpView.Cli -- diagram tests/Fixtures/ResearchSolution/ResearchSolution.slnx \
  --root 'Research.Api.OrdersController.Get()' --output /tmp/orders-get.md
```

Default exports go to `diagrams_exports` in the current directory (created if needed). After the write succeeds, stdout contains `Saved diagram: /full/absolute/path.md`.
Existing files are never overwritten. EOF or `q` exits without exporting. Custom output paths require an existing parent directory.

The Markdown contains a Mermaid `flowchart TB`, project groups, call-site evidence, and analysis boundaries. Direct calls
use solid arrows; declared dispatch, unresolved calls and optional implementation candidates use dotted arrows with explicit
labels. Branches, shared callees, parallel call sites and recursive edges survive the projection. Project groups do not imply
architectural layers or exact runtime ordering. Flowchart markup is generated with [Mermaid.NET.Flowcharts](https://github.com/JansthcirlU/Mermaid.NET.Flowcharts) 6.0.0.
Display names are escaped before being used as diagram labels, with extra handling to protect the Markdown fence.

Defaults are 20 call transitions, 200 nodes, 1,000 edges and 100,000 examined sites. Adjust `--max-depth`, `--max-nodes`,
`--max-edges` or `--budget`; truncation is visible in both Markdown and stdout. Boundary nodes count toward the node
budget. These limits apply to the export projection; Roslyn loading and whole-program graph construction occur first.
A saved file can return exit code 1 because it contains useful but partial results.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Completed within the model, with no gaps, boundaries or limits; also a cancelled diagram selection or no recognised actions. `projects` returns 0 when every reference was loaded. |
| 1 | Usable output, but with gaps, Unknown results, boundaries or limits; for `projects`, unloaded references or project-load problems. The sample fixture returns 1 on purpose for code analysis (top-level statements and external bodies are out of scope). |
| 2 | Invalid request or fatal load error. |
| 130 | Cancelled. |

## Reading the results

- **Paths are static.** `A() --> B() --> C()` is a call relationship in source, not an execution trace. Branches produce separate paths; transitions count edges, not stack depth.
- **Declared vs candidate vs runtime.** A call is recorded at its declared target (e.g. an interface method). Implementations of it are *candidates*, shown only on request and always marked; the actual runtime target is never claimed.
- **Projects.**
  - Everything comes from the MSBuild evaluation of each project, including inherited `Directory.Build.props`: `<ProjectReference>` items, `TargetFramework`, `Nullable`, `TreatWarningsAsErrors` and `WarningsAsErrors`. Code-level dependencies between projects are not consulted.
  - *Nullable warnings as errors* is yes when `TreatWarningsAsErrors` is true or `WarningsAsErrors` contains `nullable`.
  - Warning and error counts are compiler (Roslyn) diagnostics after severity options; analyzer and NuGet warnings are not included.
  - A project referenced from several places is described once; later occurrences say `(described above)`. References to projects outside the loaded workspace are listed as unloaded.
- **Exceptions.**
  - Reports cover explicit `throw`s, throw expressions, rethrows and catches: static type vs exact constructed type, filters, protected regions and locations.
  - The default view lists every explicit throw and rethrow in source, independent of any action. The other three views trace from controller actions (or `--root`).
  - **Uncaught** (`--uncaught`) uses the bottom-up summary below; **caught** and **miscaught** use per-path witnesses, deduplicated by origin and handler or `try` block.
  - **Caught** lists every catch an exception reaches with a matching type, with the handler's outcome, read from its control flow: handles it, rethrows it, handles some cases and rethrows others, or throws a different exception instead. A catch with a filter, or whose match depends on the runtime type, is marked as a possible catch and the exception is followed past it.
  - **Miscaught** means the exception passes through a `try` whose catch clauses all have non-matching types. It is reported even when a catch further up handles it later; `Then:` shows what happens next. A catch whose match cannot be decided is a possible catch, never miscaught.
  - Each witness unwinds the origin through its own `try` blocks and every caller's protected call site. An unrelated catch can never intercept it.
  - Statuses:
    - **Intercepted** by a matching handler. This does not prove recovery.
    - **Rethrown or replaced**, followed by the subsequent status.
    - **May escape** the controller action. It is never called "unhandled": middleware may still handle it.
    - **Unknown**, with a reason. Causes include dispatch, missing bodies, a `finally` that can throw, async void, tasks that are never awaited, and depth limits. The CLI sums them up in one `Not followed:` line.
  - Directly awaited task-producing calls propagate.
  - One action can show an intercepted origin next to Unknown results for other calls; each witness stands alone.
  - The `--uncaught` summary computes a finite fixed point over direct-call strongly connected components. Recursion alone does not make a summary unknown. Origins remain distinct even when their exception types match; call-site catches transform each incoming effect separately.
  - Ownerless extraction gaps are returned once in `GlobalGaps`, not repeated as action witnesses/unknown effects, and do not make a result incomplete. The CLI mentions only those that matter (minimal API endpoints it does not analyse); `inspect` lists every gap.
  - Summary effects and uncertainty coexist. Unknown effects propagate conservatively, including through catch-all handlers, because a missing body or extraction gap may represent more than a normally thrown exception. A summary does not prove the absence of runtime exceptions.
  - A summary witness is the shortest example path for an escaping effect, reconstructed on demand.
  - Implicit exceptions and branch feasibility are not modelled.
  - **Strict mode** (`--uncaught --strict`) also treats every call into a third-party assembly as an origin and lists, in a section of its own, the calls that no catch meets on the way from the action. Third-party means a referenced assembly that no .NET framework reference pack supplies (NuGet packages and directly referenced DLLs; `Microsoft.*.App.Ref`, `NETStandard.Library` and the shared runtime are .NET itself). Any catch clause around the call or around a caller's call site wraps it, whatever its type; a catch that only rethrows, or whose filter may decline, does not. Global handlers do not count: the rule asks for a catch around the call.
  - Exceptions that escape an action whose host registers `UseExceptionHandler`, `UseDeveloperExceptionPage` or a global MVC exception filter (`Filters.Add`/`AddService` of an `IExceptionFilter`/`IAsyncExceptionFilter`) are listed last, under a `Handled by a global exception handler:` line naming the registrations. The host is the action's project or a project that references it. `AddExceptionHandler` alone handles nothing. This is registration evidence: it does not prove middleware order, environment conditions (for example a developer page registered only in Development) or what the handler returns. Exception filters applied as attributes on a controller or action are not modelled. See Microsoft's [exception-handling documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).

## Releasing

Every push to `main` that changes code runs [release.yml](.github/workflows/release.yml): restore, build the fixture, test, then publish all five packages (see [Packages](#packages)) with the same version to nuget.org, tag `v<version>` and create a GitHub release with the packages attached. Pull requests and other branches run [ci.yml](.github/workflows/ci.yml) (test and pack only). Changes limited to `docs/` or `README.md` do not publish; they ship with the next code change.

The version is computed by [github-tag-action](https://github.com/mathieudutour/github-tag-action) (pinned to a commit) from [Conventional Commit](https://www.conventionalcommits.org/) subjects since the last `v*` tag. The tag is created only after nuget.org accepts the package:

| Commit message | Bump | Example |
| --- | --- | --- |
| anything (`fix:`, `chore:`, `docs:`, untyped) | patch | `fix: handle unloaded project references` → 0.1.0 → 0.1.1 |
| `feat:` or `feat(scope):` | minor | `feat(exceptions): add --miscaught` → 0.1.1 → 0.2.0 |
| `!` after the type, or a `BREAKING CHANGE:` footer | major | `feat!: remove layers command` → 0.2.0 → 1.0.0 |

The highest bump among the new commits wins. Before the first tag the baseline is 0.0.0, so a `feat:` commit makes the first release 0.1.0. The subject must start with the type: `feat: ...` counts, while "feat" only in the description does not. Add `[skip-release]` to a commit message to leave it out of the version calculation. Local builds report `0.0.0-dev`.

Publishing uses nuget.org [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), so no API key is stored: the workflow exchanges its GitHub OIDC token for a short-lived key. One-time setup on nuget.org (Trusted Publishing page): a policy for repository owner `stadtaev`, repository `sharpview`, workflow file `release.yml`, glob pattern `SharpView*` (covers all five package IDs). The workflow's `NuGet login` step names the nuget.org account (`user: stadtaev`). Optionally reserve the `SharpView.` ID prefix so nobody else can publish packages under it.

## Packages

| Package | Purpose |
| --- | --- |
| `SharpView` | The `sharpview` .NET tool (`dotnet tool install -g SharpView`). |
| `SharpView.Core` | Fact model, `ProgramIndex` and the queries: reachability, paths, exceptions, project map, layer rules. No Roslyn dependency. |
| `SharpView.Roslyn` | Loads solutions and projects with MSBuild and Roslyn into a fact snapshot. |
| `SharpView.Graph` | Generic immutable directed multigraph with search and SCC algorithms. |
| `SharpView.Export.Mermaid` | Mermaid/Markdown formatting for project maps and call diagrams. |

Every package gets the same version. The libraries are pre-1.0 and their API may still change between minor versions.

## Library use

```csharp
using System.Collections.Immutable;
using SharpView.Core;
using SharpView.Export.Mermaid;
using SharpView.Roslyn;

MSBuildBootstrap.RegisterSdk(); // call at process startup; LoadAsync also registers if needed
var snapshot = await RoslynProgramLoader.LoadAsync(new AnalysisRequest("Example.slnx"), cancellationToken);
var index = new ProgramIndex(snapshot); // build once; queries and rules share it

var actions = AspNetConventions.ControllerActions(index, cancellationToken).Select(x => x.Method).ToImmutableArray();
var reach = ReachabilityQuery.Analyze(index, actions, new ReachOptions(), cancellationToken);
var paths = CallChainQuery.Enumerate(index, actions, new ChainOptions(), cancellationToken);
var exceptions = ExceptionQuery.Analyze(index, actions, new ExceptionOptions(), cancellationToken);
var summaries = ExceptionSummaryQuery.Analyze(index, actions, new ExceptionSummaryOptions(), cancellationToken);
var caught = ExceptionHandlingQuery.Caught(exceptions);       // origin, catch, certainty, example witness
var miscaught = ExceptionHandlingQuery.Miscaught(exceptions); // origin, try block that did not match, outcomes
var projects = ProjectMap.Build(snapshot);                     // MSBuild reference graph and build settings
var projectMarkdown = MermaidExporter.Markdown(projects);
if (!actions.IsEmpty)
{
    var diagram = CallDiagramQuery.Build(index, actions[0], new DiagramOptions(), cancellationToken);
    var markdown = MermaidExporter.Markdown(diagram);
}

// Library only (no CLI command): map code to your own layers and check allowed dependencies.
var model = new LayerModel(
    [new(LayerScope.Project, "Orders.Api", "API"), new(LayerScope.Project, "Orders.Infrastructure", "Infrastructure")],
    [new("API", "Infrastructure")]);
var graph = LayerGraph.Build(index, new LayerMap(index, model), cancellationToken);
var findings = LayerRules.AllowedDependencies(graph, model); // Finding(RuleId, Verdict, Subject, Message, Evidence)
```

- Mermaid/Markdown exports live in `SharpView.Export.Mermaid`; reference it and import `SharpView.Export.Mermaid` when calling `MermaidExporter`. Core retains the diagram model/query and depends only on `SharpView.Graph`.
- `index.GetCallGraph(cancellationToken)` and `index.GetDependencyGraph(cancellationToken)` lazily publish complete cached graphs; the tokenless properties remain available. They expose the whole program as graphs (`SharpView.Graph`: out/in edges, BFS shortest paths, reachability, quotient by group, strongly connected components).
- `CallEdge.Id` identifies a specific site/kind/target relationship. Quotients retain every group, original node membership and supporting edge, including isolated groups. Quotients are presentation/aggregation models; collapsing groups can invent apparent paths, so method exception analysis uses the original graph.
- `Where` and `Subgraph` materialize new graphs; they share payload objects. Generic graph consumers must keep node equality/hash codes and edge endpoints stable.
- Reach stores shared parent links and constructs witness arrays only when requested. Completed searches report shortest allowed distances; truncated searches retain consistent admitted paths. `ViaCandidate` means no bounded direct-only route was established, while `WitnessUsesCandidate` describes the selected shortest witness. A shorter candidate witness can coexist with a longer direct route. `DirectDistance` (nullable) and lazy `DirectWitness` preserve that direct-only proof; direct-call rules must use them. Null means no direct route was established within the bounds, not proof none exists.
- Snapshots contain no Roslyn symbols and stay usable after the workspace is disposed.
- A rule is a plain function returning `Finding`s. Keep `Verdict.Unknown` separate from `Pass`. See the worked example in the [overview](docs/overview.md#writing-your-own-rule).
- Exception witnesses carry a machine-readable `ExceptionReason` next to the human-readable `Reason`.
- MSBuild Locator finds an installed SDK. Host selection and the input's `global.json` are not guaranteed to match every shell configuration.

To analyse an existing `inspect --json` export without loading Roslyn, reference only `SharpView.Core`:

```csharp
await using var input = File.OpenRead("snapshot.json");
var document = await SnapshotJson.ReadAsync(input, cancellationToken);
var index = new ProgramIndex(document.Snapshot);
var actions = AspNetConventions.ControllerActions(index, cancellationToken)
    .Select(x => x.Method).ToImmutableArray();
var reach = ReachabilityQuery.Analyze(index, actions, new ReachOptions(), cancellationToken);
```

`SnapshotJson.WriteAsync(stream, snapshot, producerVersion, cancellationToken)` writes the same envelope as the CLI.
The reader accepts schema 3 only: unsupported versions throw `NotSupportedException`; malformed JSON or envelopes throw
`JsonException`. There are no automatic migrations. The reader checks the envelope, not every graph reference or fact;
use snapshots from a trusted producer. Both methods leave the stream open. The CLI still takes source project/solution
inputs; offline loading is a library API. `CallIndex` is now internal; library consumers should use `ProgramIndex` and
`CallChainQuery`.

## Documentation

| Document | Read it for |
| --- | --- |
| [How SharpView works](docs/overview.md) | The approach: pipeline, principles, commands, custom rules, glossary. |
| [Coverage and limits](docs/coverage.md) | What is and is not modelled. |
| [Model reference](docs/model.md) | Facts, identities, keys and query semantics in detail. |
| [Validation](docs/validation.md) | Which tests prove which behaviour; regression comparison. |
| [Todo](docs/todo.md) | Planned work: analytical graph for agents and visualisation, open items. |
| [Sample reports](docs/samples/README.md) | Full outputs on the sample Web API. |
| [Research plan](docs/superpowers/plans/2026-10-12-csharp-call-chain-research-plan.md), [consumer model plan](docs/superpowers/plans/2026-10-12-consumer-semantic-model-plan.md) | Design history and decisions. |

Status: validated on the repository's sample Web API (reproducible). The external Web API attempt is blocked on package
restore; see [external validation](docs/validation.md#external-web-api-attempt-2026-10-17).
