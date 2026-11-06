# How SharpDrill works

SharpDrill answers structural questions about an ASP.NET Core Web API by reading its C# source with the compiler (Roslyn):

- How do the projects reference each other, and how is each one built?
- Where are exceptions thrown, which ones go uncaught, which get caught where, and which slip past a `try` that doesn't match them?
- Which calls lead from a controller action down through the projects?

Every answer carries **source evidence** (file, line, column) so a person can check it, and every place where the analysis cannot decide is reported as **Unknown** rather than silently skipped.

## The pipeline

```mermaid
flowchart LR
    A[".sln / .slnx / .csproj<br/>(restored)"] --> B["Load<br/>MSBuild + Roslyn"]
    B --> C["Extract facts<br/>ProgramSnapshot"]
    C --> D["Index<br/>ProgramIndex"]
    D --> E["Analyses & models<br/>projects · exceptions · call diagram"]
    E --> F["Rules<br/>Finding + Verdict"]
    E --> G["Reports<br/>CLI text / Markdown"]
    F --> G
```

| Stage | Project | What it does |
| --- | --- | --- |
| Load | `SharpDrill.Roslyn` | Opens the solution with the .NET SDK. Never builds or edits your code; the CLI restores missing packages first and reuses the last load while its inputs are unchanged. |
| Extract facts | `SharpDrill.Roslyn` | Turns compiler symbols into small immutable records: types, callables, call sites, dependencies, throws, catches, implementation candidates, gaps. |
| Index | `SharpDrill.Core` | `ProgramIndex` builds lookups once (callers, callees, members, ancestors, candidates…) and, on first use, the whole-program `CallGraph` and type `DependencyGraph`. |
| Analyses & models | `SharpDrill.Core` | Queries over the index: project map, exception propagation and handling, call diagram, layer graph (library only). |
| Rules | `SharpDrill.Core` | Plain C# functions that turn analysis results into `Finding`s. |
| Diagram export | `SharpDrill.Export.Mermaid` | Formats the project map and call-diagram projections as Mermaid/Markdown; owns the Mermaid NuGet dependency. |
| Reports | `SharpDrill.Cli` | Formats text and saves Markdown. No analysis logic lives here. |

`SharpDrill.Core` depends only on `SharpDrill.Graph`, with no Roslyn or Mermaid dependency: once a snapshot exists, all analysis runs on plain records.

`SharpDrill.Graph` is a small generic graph library underneath Core (it knows nothing about C#): an immutable directed multigraph (`DiGraph`) with NetworkX-style operations — out/in edges, successors/predecessors, `Where` and `Subgraph` (materialized copies sharing payload), `Quotient` (collapse nodes into groups such as layers) and `StronglyConnectedComponents` (cycles). Everything keeps insertion order, so results are deterministic. The layer graph is a quotient of the call and dependency graphs, with isolated groups and original node membership retained. Node identities and edge endpoints must remain stable. Cached graph accessors accept cancellation tokens and publish complete graphs only.

## Principles

1. **Declared, candidate and runtime are different things.**
   - *Declared binding* — what the source code calls, e.g. `IRepository.Read()`. Always recorded.
   - *Implementation candidate* — a source method that implements or overrides it, e.g. `SqlRepository.Read()`. Recorded separately; followed by default in `exceptions` and `diagram` (`--declared-only` turns that off) and always marked as a possibility.
   - *Runtime target* — what actually runs (depends on DI, receiver type…). SharpDrill never claims this.
2. **Unknown is a result, not an error.** Interface dispatch, missing bodies, a `finally` that can throw, tasks never awaited, depth limits… produce explicit Unknown/boundary entries with a reason.
3. **Evidence everywhere.** Facts and results point at source locations; reports can be checked by hand.
4. **Bounded and deterministic.** Depth, output and work limits are explicit flags; the same input gives byte-identical output.
5. **Structure comes from the build, not from names.** The project map is MSBuild's own reference graph and settings; naming conventions are never treated as architecture. Custom layer mappings exist only in the library, where you declare them.

## Which command answers which question

Snippets below are from the sample Web API, with names and paths shortened; the linked files are the full outputs.

| Question | Command | Output (fixture sample) |
| --- | --- | --- |
| How do projects reference each other; how is each built? | `projects` (`--mermaid`) | [projects.txt](samples/projects.txt) |
| Where is each explicit exception thrown? | `exceptions` | [exceptions.txt](samples/exceptions.txt) |
| Which exceptions may escape an action with no catch on the way? | `exceptions --uncaught` | [exceptions-uncaught.txt](samples/exceptions-uncaught.txt) |
| Which are caught, and where? | `exceptions --caught` | [exceptions-caught.txt](samples/exceptions-caught.txt) |
| Which pass through a `try` that doesn't catch them? | `exceptions --miscaught` | [exceptions-miscaught.txt](samples/exceptions-miscaught.txt) |
| What does one action call, as a picture? | `diagram` | [diagram.md](samples/diagram.md) |
| How does a coding agent learn to use SharpDrill? | `skill` (`--user`) | `.github/skills/sharpdrill/SKILL.md`, `.claude/skills/sharpdrill/SKILL.md` |

### When to use which command

| Need | Command |
| --- | --- |
| Orientation in an unfamiliar solution | `projects`, then `exceptions` |
| Exception-handling review | `exceptions --uncaught`, `--caught`, `--miscaught` |
| Reviewing or explaining one endpoint | `diagram` |

**`projects` — how is the solution put together?** The `<ProjectReference>` tree with each project's framework, `Nullable`, whether nullable (or all) warnings are errors, and compiler warning and error counts. Values come from MSBuild's evaluation, including inherited `Directory.Build.props`.

- *First look at a solution*, for a person or an agent: entry projects at the top, what each one depends on beneath it.
- *Build hygiene:* spot projects with nullable disabled, warnings not enforced, or a warning backlog.
- *Documentation:* `--mermaid` saves the same tree as a diagram with a settings table, ready for a README or design doc.

**`exceptions` — where can things go wrong?** Four views over the same throw and catch facts:

- *Default, every throw:* each explicit `throw` with its type, method and location. A map of what the code can raise, independent of any endpoint.
- *`--uncaught`:* which exceptions may leave a controller action because no catch is on the way, each with the shortest example path. Scales to a whole API, for example to find domain exceptions that reach middleware.
- *`--uncaught --strict`:* also every call into a third-party library (NuGet package or referenced DLL, not .NET itself) that no catch meets on the way from the action, in a section of its own. Any catch around the call or a caller counts, whatever its type. Logging and `Microsoft.Extensions.*` calls (except `Microsoft.Extensions.Caching.*`) are left out unless `--verbose` is given.
- *`--caught`:* each place an exception meets a matching catch: where it is thrown, where it is caught, and what the handler does (handles, rethrows, replaces). Catches with filters or runtime-dependent matches are marked as possible.
- *`--miscaught`:* exceptions that pass through a `try` whose catch clauses don't match their type, a common sign of catching the wrong exception. Shows the clauses, and whether something further up catches it.

A `Not followed:` line on stderr says where the analysis stopped and needs a human.

*`--format msbuild`* prints the uncaught and miscaught findings (or one view's) as one MSBuild warning per line: `SD0001` may escape an action, `SD0002` may escape to a global exception handler, `SD0003` miscaught, `SD0004` third-party call with `--strict`, `SD0005` leaves an async void method and ends the process. Run it in CI (the README has a GitLab CI job) or as a VS Code task with the `$msCompile` problem matcher; the optional `SharpDrill.MSBuild` package runs it after each build instead, so the findings also land in Visual Studio's Error List. `#pragma warning disable SD000N` and `[SuppressMessage("SharpDrill", "SD000N")]` at the warning's location suppress it.

**`diagram` — a picture of one endpoint.** A Mermaid call graph for pull requests, design docs or onboarding notes, grouped by project. Shared callees are drawn once, so it stays readable on large endpoints. Interface and virtual calls lead into their source implementations, marked as candidates; `--declared-only` stops at the declaration.

### `projects` — the reference tree

```text
Projects: 3. Each project is listed with the projects it references (<ProjectReference>) beneath it.
Research.Api [net10.0 | Nullable: enable | Nullable warnings as errors: no | All warnings as errors: no | Compiler warnings: 0 | Errors: 0]
└── Research.Application [net10.0 | Nullable: enable | Nullable warnings as errors: no | ...]
    └── Research.Infrastructure [net10.0 | Nullable: enable | Nullable warnings as errors: yes | ...]
```

Each project gets one line, nested under the first project to reach it. Its other referrers name it after `->` on their own line, without the dotted prefix all projects share. Warning counts are compiler diagnostics; analyzer and NuGet warnings are not included.

### `exceptions` — four views

Every throw (default):

```text
Research.Infrastructure.StorageException  Repository.Read  Research.Infrastructure/Repository.cs:6
System.Exception (or a subclass)  Repository.StaticType  Research.Infrastructure/Repository.cs:10
```

`--caught`, where it is thrown and where it is caught. Every report that lists more than one finding starts with an
`Overview:` line and a count per exception type; findings are numbered and separated by blank lines:

```text
Overview: 7 caught exceptions
  7  Research.Infrastructure.StorageException

1. Research.Infrastructure.StorageException
   Thrown:     Repository.ReadAsync  Research.Infrastructure/Repository.cs:8
   Caught by:  Service.AwaitCaught  Research.Application/Service.cs:14
               catch (Research.Infrastructure.StorageException)
   Handler:    handles it
   Path:       OrdersController.AwaitCaught -> Service.AwaitCaught -> Repository.ReadAsync
```

`--miscaught`, a `try` that did not catch it:

```text
1. Research.Infrastructure.StorageException
   Thrown:     Repository.Read  Research.Infrastructure/Repository.cs:6
   Missed by:  Service.Incompatible  Research.Application/Service.cs:8
               try { ... } catch (System.ArgumentException)
   Then:       may escape OrdersController.Incompatible
   Handled:    by the global exception handler UseExceptionHandler  Research.Api/Program.cs:5
   Path:       OrdersController.Incompatible -> Service.Incompatible -> Repository.Read
```

`--uncaught`, one entry per exception type escaping from one method (its throw lines listed together), with an example path from an action; `Escapes:` lists every action when
there is more than the one the path starts at (one per line when they do not fit):

```text
2. Research.Infrastructure.StorageException
   Thrown:     Repository.Read  Research.Infrastructure/Repository.cs:6
   Escapes:    OrdersController.Filter, OrdersController.Get, OrdersController.Incompatible, OrdersController.Unrelated
   Handled:    by the global exception handler UseExceptionHandler  Research.Api/Program.cs:5
   Path:       OrdersController.Filter -> Service.Filter -> Repository.Read
```

A path stays on one line while it fits in 120 columns, otherwise it continues one call per line. `~>` marks a step into
an implementation found in source behind an interface or virtual call; when source has several, the step adds
`(1 of N implementations)`, since that is where the path is a guess. A path that shares at least three steps with an
earlier finding's path reads `same as 2` or `as in 2 up to <method>, then -> ...` (not under `--locations`). A throw in the action itself has no path line; its `Thrown:` line ends with
`(in the action)`. `--locations` adds a `Locations:` list.

"May escape" means no catch on the way from the action. Findings whose actions are covered by registered exception middleware or a global exception filter are listed last, each with a `Handled:` line naming the registration, so every block reads on its own (in a CI log too). A catch is *miscaught* only when every clause definitely does not match; filters and runtime-dependent matches are possible catches instead.

## Writing your own rule

A rule is a function over the index (and optionally derived models) that returns `Finding`s:

```csharp
// "Controllers must not call Infrastructure directly."
static IEnumerable<Finding> NoDirectInfrastructure(ProgramIndex index, LayerMap layers)
{
    foreach (var action in AspNetConventions.ControllerActions(index).Select(x => x.Method))
        foreach (var site in index.Outgoing(action).Where(x => x.BoundTarget is { } target && layers.LayerOf(target) == "Infrastructure"))
            yield return new("no-direct-infrastructure", Verdict.Violation,
                index.Callable(action).DisplaySignature, $"Calls {site.TargetDisplay} directly.", [site.Evidence]);
}
```

The rule reads declared call targets only; an interface call into Infrastructure is not a direct-call violation.

Building blocks: `ProgramIndex` (lookups, `CallGraph`, `DependencyGraph`), `SharpDrill.Graph` algorithms, `ProjectMap` (MSBuild references and settings), `LayerMap`/`LayerGraph` (your own layer mapping, library only), `CallDiagramQuery`, `ExceptionSummaryQuery` (with `ExceptionReason` codes), `ExceptionHandlingQuery` (caught/miscaught, where one throw ends), `AspNetConventions`. Keep `Verdict.Unknown` separate from `Pass` — it is how a rule stays honest.

## Glossary

| Term | Meaning |
| --- | --- |
| Callable | Anything with a body: method, constructor, accessor, operator, local function, lambda. |
| Call site | One call in source, with its declared target and dispatch kind (direct, interface, virtual, delegate, dynamic). |
| Transition / distance | One call edge. Not runtime stack depth. |
| Witness | A concrete path plus source locations that justifies a result. |
| Boundary | Where traversal stopped and why (dispatch, missing body, unresolved, depth/work limit). |
| Gap | Something extraction could not model (e.g. top-level statements). Commands mention one only when it affects their result. |
| Key | Portable identity (`<project>\|<documentation ID>`); IDs are local to one analysis run. |

## Further reading

- [Analysis coverage and limits](coverage.md) — what is and is not modelled.
- [Model reference](model.md) — facts, identities and query semantics in detail.
- [Validation](validation.md) — which tests prove which behaviour.
- [Sample reports](samples/README.md) — full outputs on the fixture.
- [Lessons learned](lessons.md) — why the first real-project run was noise, and the rules that came out of it.

## Summary analysis and diagram projections

`ExceptionSummaryQuery` keeps possible escape effects beside the immutable call graph. It seeds each explicit throw
in its own method and pushes finite, origin-preserving effects up through caller sites with one worklist until nothing
changes (recursion needs no special scheduling). Catch/rethrow/replacement, awaited calls and unsupported
flow use the same transfer code as the example witnesses; caught and miscaught findings are read from the recorded transfer steps. Per-call-site crossing summaries prevent a catch at one
site from hiding effects at another. Budgets bound retained facts and transfer work; uncertainty remains explicit. Ownerless gaps stay in result-level `GlobalGaps`, separate from action effects; overall `IsComplete` accounts for them. Complexity
depends on the number of distinct effects, call sites and fixed-point transfers, rather than just the node/edge count.

`CallDiagramQuery` projects a bounded action subgraph without enumerating all paths. `MermaidExporter` in `SharpDrill.Export.Mermaid` formats that projection;
it does not infer layers or analyze exception behavior. The CLI `diagram` command supplies selection and safe file output.
Project groups are labels for source ownership; collapsed project/layer graphs must never replace the method graph for
call or exception analysis. See [the implementation change document](changes/2026-10-18-graph-analysis-and-mermaid.md)
for contracts, defaults, regressions and limitations.
