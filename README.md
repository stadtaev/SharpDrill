# Sharpview

Sharpview is an initial C# research tool for understanding ASP.NET Core controller call paths and explicit exceptions. It loads restored SDK projects using the .NET SDK, preserves source evidence, and reports where static analysis stops. Visual Studio is not required.

`Sharpview.Core` contains immutable facts and bounded queries, `Sharpview.Roslyn` loads and extracts semantic facts, and `Sharpview.Cli` parses arguments and formats reports. All three existing projects remain in `sharpview.slnx`.

## Build and validate

Use a .NET 10 SDK compatible with `global.json`:

```sh
dotnet restore sharpview.slnx
dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx
dotnet test sharpview.slnx
```

The fixture build explicitly restores repository-owned projects. Sharpview itself never runs restore on analysed inputs. Restore your own input dependencies before analysis. SDK project evaluation and source generators can execute project-supplied tooling; loading is not a sandbox or simple text parsing.

## Research commands

```sh
dotnet run --project src/Sharpview.Cli -- --help
dotnet run --project src/Sharpview.Cli -- controllers tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()' --layer project:Research.Api=API --layer project:Research.Application=Application --layer project:Research.Infrastructure=Infrastructure --locations
dotnet run --project src/Sharpview.Cli -- exceptions tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()'
dotnet run --project src/Sharpview.Cli -- methods tests/Fixtures/ResearchSolution/ResearchSolution.slnx
dotnet run --project src/Sharpview.Cli -- inspect tests/Fixtures/ResearchSolution/ResearchSolution.slnx --format json --output snapshot.json
```

`--output` creates a new file and refuses to overwrite an existing file. JSON is schema version 2 and goes to stdout when no file is selected; diagnostics go to stderr. Input may be `.csproj`, `.sln` or `.slnx`. Select `--framework` for multi-target projects and `--configuration` when required.

A path looks like:

```text
Research.Api.OrdersController.Get() --> Research.Application.Service.Load() --> Research.Infrastructure.Repository.Read()
```

Paths are static call relationships, not execution traces. Branch calls produce separate paths. Call transitions count graph edges, not runtime stack depth. Layers come only from explicit project/namespace mappings; the longest prefix wins, with project preference on a tie.

`chains` defaults to every source callable; `controllers` and `exceptions` default to recognised controller actions. Use an exact signature from `methods`, or `--root-id` if signatures collide. Interface, virtual, delegate, dynamic and missing targets remain visible boundaries. `--dispatch direct` marks excluded dispatch while preserving its declared target evidence.

Traversal defaults to 20 transitions, 1,000 results and 100,000 examined call sites. Adjust `--max-depth`, `--max-chains`, and `--max-expansions`. Cancellation propagates from Ctrl+C.

Exit codes: **0** completed within the model with no extraction gaps/limits; **1** usable output with gaps, uncertain boundaries or limits; **2** invalid request/fatal load; **130** cancelled. The Web API fixture intentionally returns **1** because top-level ownership and external bodies remain outside scope. An intercepted explicit origin can appear beside Unknown results for other calls.

## Exception meaning

Reports contain all explicit throws, throw expressions, rethrows and catches, with static exception types, exact construction evidence, catch filters, protected spans and source locations. Per-action witnesses unwind the origin's lexical regions and each caller's protected call site; unrelated catches cannot intercept an origin.

The result categories are matching-handler interception, rethrow/replacement with its subsequent origin and status, possible escape from the controller action under the supported model, and Unknown. Interception does not prove successful recovery. Escape from an action does not establish global absence of handlers. `UseExceptionHandler`, `AddExceptionHandler`, and `UseDeveloperExceptionPage` registrations are reported separately as boundary evidence, without proving execution order or coverage. See Microsoft's [exception-handling documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0) for the runtime pipeline.

Directly awaiting a task-producing call supports explicit-origin propagation. Unobserved tasks, task aliases, custom awaitables, async void, filters, complex handler flow, finally behavior, missing bodies and uncertain dispatch can produce scoped Unknown. The analysis does not model implicit exceptions or branch feasibility.

## Library use

```csharp
using Sharpview.Core;
using Sharpview.Roslyn;

// Call explicitly at process startup; LoadAsync also registers if needed.
MSBuildBootstrap.RegisterSdk();
var snapshot = await RoslynProgramLoader.LoadAsync(new AnalysisRequest("Example.slnx"), cancellationToken);
var actions = snapshot.ControllerActions.Select(x => x.Method).ToImmutableArray();
var paths = CallChainQuery.Enumerate(snapshot, actions, new ChainOptions(), cancellationToken);
var exceptions = ExceptionQuery.Analyze(snapshot, actions, new ChainOptions(), cancellationToken);
```

Add `using System.Collections.Immutable;` for `ToImmutableArray`. Snapshots contain no Roslyn symbols and remain queryable after the workspace is disposed. MSBuild Locator discovers an installed SDK; host selection and input `global.json` are not promised to match every shell configuration.

See [analysis coverage](docs/coverage.md), [model notes](docs/model.md), [test evidence](docs/validation.md), [sample reports](docs/samples/README.md), and the updated [implementation plan](docs/superpowers/plans/2026-10-12-csharp-call-chain-research-plan.md). Fixture validation is reproducible; an external Web API experiment remains pending until an input is selected.
