# Sharpview

Sharpview is a C# research tool for understanding ASP.NET Core Web APIs from source:

- **Call paths** from controller actions down through your architectural layers.
- **Explicit exceptions** that may escape an action, and the handlers in between.
- **Mermaid diagrams** for a selected controller action, grouped by project.
- **Layer dependencies**, checked against the architecture you declare.

Results carry source evidence (file:line:column), and anything the analysis cannot decide is reported as *Unknown* instead of being hidden. It uses the .NET SDK and Roslyn; Visual Studio is not required.

**New here? Read [How Sharpview works](docs/overview.md)** — pipeline, principles, what each command answers, and how to write your own rule.

## Quick start

Requires a .NET 10 SDK compatible with `global.json`.

```sh
dotnet restore sharpview.slnx
dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx   # sample Web API used below
dotnet test sharpview.slnx
```

Try it on the sample (`F` is the input, `L` the layer mapping; works in bash and zsh):

```sh
F=tests/Fixtures/ResearchSolution/ResearchSolution.slnx
L=(--layer project:Research.Api=API --layer project:Research.Application=Application --layer project:Research.Infrastructure=Infrastructure)

dotnet run --project src/Sharpview.Cli -- reach $F --root 'Research.Api.OrdersController.Get()' "${L[@]}"
dotnet run --project src/Sharpview.Cli -- exceptions $F --root 'Research.Api.OrdersController.Get()'
dotnet run --project src/Sharpview.Cli -- diagram $F
dotnet run --project src/Sharpview.Cli -- layers $F "${L[@]}" --allow API=Application --allow Application=Infrastructure
```

Analysing your own solution: restore it first (Sharpview never restores, builds or edits analysed code). Loading evaluates MSBuild projects and may run project tooling and source generators, so it is not a sandbox.

## Commands

| Command | Question it answers | Default roots |
| --- | --- | --- |
| `reach` | What can each action reach — once per method, with distance, layer and shortest path? | controller actions (or any `--root`) |
| `paths` | What are all call paths from each action (or any `--root` method), with project, layer and transitions? | controller actions |
| `exceptions` | Which explicit exceptions may escape each action, with one witness per origin? `--paths` lists every propagation path and its handlers instead. | controller actions |
| `diagram` | Export a Mermaid call graph in Markdown, grouped by project. | pick one controller action by number |
| `layers` | Which layers depend on which; with `--allow`, which dependencies break the rules? | — |
| `inspect` | Extraction summary; `--methods` lists callables with their IDs (to pick a `--root`); `--json` writes the full fact snapshot. | — |

For practical use cases of each command, such as impact analysis, CI gating, finding roots or debugging extraction, see [When to use which command](docs/overview.md#when-to-use-which-command). Sample outputs for each are in [docs/samples](docs/samples/README.md). Run `--help` for the complete option list.

### Common options

- **Input:** `.csproj`, `.sln` or `.slnx`. Use `--framework` for multi-target projects and `--configuration` (default `Debug`) when needed.
- **Roots:** `--root <id or exact signature>`; copy either from `inspect --methods`. An exact ID match wins, so use the ID when signatures collide. `reach` and `paths` accept any source method; `exceptions` and `diagram` need a controller action.
- **Layers:** repeatable `--layer project:<prefix>=<name>`, `--layer namespace:<prefix>=<name>` (namespaces and nested namespaces) or `--layer type:<prefix>=<name>` (a type and its nested types). Longest prefix wins; a project mapping wins a tie. Layers come only from these mappings.
- **Layer rules:** repeatable `--allow <from>=<to>[,<to>...]` (with `layers`). Same-layer dependencies are always allowed.
- **Dispatch:** interface, virtual, delegate, dynamic and unresolved calls are visible boundaries. `reach --follow-candidates` and `diagram --follow-candidates` also follow implementation candidates, marked `(candidate)`. `paths` and `exceptions` stop at the declared target.
- **Limits:** `--budget` (default 100,000 examined call sites; for exception summaries it also bounds retained facts), `--max-depth` (default 20 transitions) and `--max-paths` (default 1,000 reported paths, on `paths` and `exceptions --paths`). Reaching a limit is always reported. Exception summaries take `--budget` only.
- **Strict options:** each command accepts only its own options (`--help` lists them per command). Anything else fails before loading, naming the command.
- **Evidence:** `--locations` prints call-site locations for paths and reach witnesses.
- **Output:** `inspect --json` writes schema version 3 (IDs are plain strings) to stdout or `--output <new file>` (never overwrites). Diagnostics go to stderr. Ctrl+C cancels cleanly.

### Export a diagram

```sh
# Lists numbered controller actions; enter a number, or q to cancel.
dotnet run --project src/Sharpview.Cli -- diagram tests/Fixtures/ResearchSolution/ResearchSolution.slnx

# Select directly for scripts. --root also accepts an action ID from inspect --methods.
dotnet run --project src/Sharpview.Cli -- diagram tests/Fixtures/ResearchSolution/ResearchSolution.slnx \
  --root 'Research.Api.OrdersController.Get()' --output /tmp/orders-get.md
```

Default exports go to `diagrams_exports` beside `Sharpview.Cli.dll` (or the published binary), independently of the
terminal's working directory. After the write succeeds, stdout contains `Saved diagram: /full/absolute/path.md`.
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
| 0 | Completed within the model, with no gaps, boundaries, limits or findings; also a cancelled diagram selection or no recognised actions. |
| 1 | Usable output, but with gaps, Unknown results, boundaries, limits or rule violations. The sample fixture returns 1 on purpose (top-level statements and external bodies are out of scope). |
| 2 | Invalid request or fatal load error. |
| 130 | Cancelled. |

## Reading the results

- **Paths are static.** `A() --> B() --> C()` is a call relationship in source, not an execution trace. Branches produce separate paths; transitions count edges, not stack depth.
- **Declared vs candidate vs runtime.** A call is recorded at its declared target (e.g. an interface method). Implementations of it are *candidates*, shown only on request and always marked; the actual runtime target is never claimed.
- **Exceptions.**
  - Reports cover explicit `throw`s, throw expressions, rethrows and catches: static type vs exact constructed type, filters, protected regions and locations.
  - Each witness unwinds the origin through its own `try` blocks and every caller's protected call site. An unrelated catch can never intercept it.
  - Statuses:
    - **Intercepted** by a matching handler. This does not prove recovery.
    - **Rethrown or replaced**, followed by the subsequent status.
    - **May escape** the controller action. It is never called "unhandled": middleware may still handle it.
    - **Unknown**, with a reason. Causes include dispatch, missing bodies, filters, `finally`, async void, unobserved or aliased tasks, and depth limits.
  - Directly awaited task-producing calls propagate.
  - One action can show an intercepted origin next to Unknown results for other calls; each witness stands alone.
  - The default summary mode computes a finite fixed point over direct-call strongly connected components. Recursion alone does not make a summary unknown. Origins remain distinct even when their exception types match; call-site catches transform each incoming effect separately.
  - Ownerless extraction gaps are returned once in `GlobalGaps` and reported as global caveats on stderr, not repeated as action witnesses/unknown effects. Overall `IsComplete` stays false when global caveats exist; summary method/root completeness is local to its known effects.
  - Summary effects and uncertainty coexist. Unknown effects propagate conservatively, including through catch-all handlers, because a missing body or extraction gap may represent more than a normally thrown exception. A summary does not prove the absence of runtime exceptions.
  - A summary witness is one example for an escaping effect, reconstructed on demand. `exceptions --paths` still reports intercepted origins and individual paths under its own bounds.
  - Implicit exceptions and branch feasibility are not modelled.
  - `UseExceptionHandler`, `AddExceptionHandler` and `UseDeveloperExceptionPage` registrations are listed separately as *boundary evidence*. They do not prove middleware order or coverage; see Microsoft's [exception-handling documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).
- **Layer edges.**
  - Edge kinds:
    - `DirectCall`.
    - `DeclaredCall`: an interface or virtual call, counted at its declared target.
    - `CandidateCall`: a call to an implementation candidate.
    - `TypeDependency`: signatures, base types and their type arguments, fields, properties and construction.
  - Code outside every mapping shows as `(unmapped)`. Unmapped referenced assemblies are counted as external references.
  - The `layer-dependency` rule reports a disallowed cross-layer edge as **Violation** and an edge touching unmapped code as **Unknown**. Candidate edges are not judged.

## Library use

```csharp
using System.Collections.Immutable;
using Sharpview.Core;
using Sharpview.Export.Mermaid;
using Sharpview.Roslyn;

MSBuildBootstrap.RegisterSdk(); // call at process startup; LoadAsync also registers if needed
var snapshot = await RoslynProgramLoader.LoadAsync(new AnalysisRequest("Example.slnx"), cancellationToken);
var index = new ProgramIndex(snapshot); // build once; queries and rules share it

var actions = AspNetConventions.ControllerActions(index, cancellationToken).Select(x => x.Method).ToImmutableArray();
var reach = ReachabilityQuery.Analyze(index, actions, new ReachOptions(), cancellationToken);
var paths = CallChainQuery.Enumerate(index, actions, new ChainOptions(), cancellationToken);
var exceptions = ExceptionQuery.Analyze(index, actions, new ExceptionOptions(), cancellationToken);
var summaries = ExceptionSummaryQuery.Analyze(index, actions, new ExceptionSummaryOptions(), cancellationToken);
if (!actions.IsEmpty)
{
    var diagram = CallDiagramQuery.Build(index, actions[0], new DiagramOptions(), cancellationToken);
    var markdown = MermaidExporter.Markdown(diagram);
}

var model = new LayerModel(
    [new(LayerScope.Project, "Orders.Api", "API"), new(LayerScope.Project, "Orders.Infrastructure", "Infrastructure")],
    [new("API", "Infrastructure")]);
var graph = LayerGraph.Build(index, new LayerMap(index, model), cancellationToken);
var findings = LayerRules.AllowedDependencies(graph, model); // Finding(RuleId, Verdict, Subject, Message, Evidence)
```

- Mermaid/Markdown exports live in `Sharpview.Export.Mermaid`; reference it and import `Sharpview.Export.Mermaid` when calling `MermaidExporter`. Core retains the diagram model/query and depends only on `Sharpview.Graph`.
- `index.GetCallGraph(cancellationToken)` and `index.GetDependencyGraph(cancellationToken)` lazily publish complete cached graphs; the tokenless properties remain available. They expose the whole program as graphs (`Sharpview.Graph`: out/in edges, BFS shortest paths, reachability, quotient by group, strongly connected components).
- `CallEdge.Id` identifies a specific site/kind/target relationship. Quotients retain every group, original node membership and supporting edge, including isolated groups. Quotients are presentation/aggregation models; collapsing groups can invent apparent paths, so method exception analysis uses the original graph.
- `Where` and `Subgraph` materialize new graphs; they share payload objects. Generic graph consumers must keep node equality/hash codes and edge endpoints stable.
- Reach stores shared parent links and constructs witness arrays only when requested. Completed searches report shortest allowed distances; truncated searches retain consistent admitted paths. `ViaCandidate` means no bounded direct-only route was established, while `WitnessUsesCandidate` describes the selected shortest witness. A shorter candidate witness can coexist with a longer direct route. `DirectDistance` (nullable) and lazy `DirectWitness` preserve that direct-only proof; direct-call rules must use them. Null means no direct route was established within the bounds, not proof none exists.
- Snapshots contain no Roslyn symbols and stay usable after the workspace is disposed.
- A rule is a plain function returning `Finding`s. Keep `Verdict.Unknown` separate from `Pass`. See the worked example in the [overview](docs/overview.md#writing-your-own-rule).
- Exception witnesses carry a machine-readable `ExceptionReason` next to the human-readable `Reason`.
- MSBuild Locator finds an installed SDK. Host selection and the input's `global.json` are not guaranteed to match every shell configuration.

To analyse an existing `inspect --json` export without loading Roslyn, reference only `Sharpview.Core`:

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
| [How Sharpview works](docs/overview.md) | The approach: pipeline, principles, commands, custom rules, glossary. |
| [Coverage and limits](docs/coverage.md) | What is and is not modelled. |
| [Model reference](docs/model.md) | Facts, identities, keys and query semantics in detail. |
| [Validation](docs/validation.md) | Which tests prove which behaviour; regression comparison. |
| [Todo](docs/todo.md) | Planned work: analytical graph for agents and visualisation, open items. |
| [Sample reports](docs/samples/README.md) | Full outputs on the sample Web API. |
| [Research plan](docs/superpowers/plans/2026-10-12-csharp-call-chain-research-plan.md), [consumer model plan](docs/superpowers/plans/2026-10-12-consumer-semantic-model-plan.md) | Design history and decisions. |

Status: validated on the repository's sample Web API (reproducible). The external Web API attempt is blocked on package
restore; see [external validation](docs/validation.md#external-web-api-attempt-2026-10-17).
