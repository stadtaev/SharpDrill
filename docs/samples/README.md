# Fixture reports

These reports were produced from the repository-owned three-project Web API fixture after its explicit SDK build. Paths and line numbers are source evidence from this checkout, not portable runtime traces.

The Mermaid export is available as [diagram.md](diagram.md). Reproduce with the built CLI:

```sh
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll controllers tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()' --layer project:Research.Api=API --layer project:Research.Application=Application --layer project:Research.Infrastructure=Infrastructure --locations
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll exceptions tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()'
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll diagram tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()' --output /tmp/sharpview-get-diagram.md
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll reach tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()' --layer project:Research.Api=API --layer project:Research.Application=Application --layer project:Research.Infrastructure=Infrastructure --locations
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll layers tests/Fixtures/ResearchSolution/ResearchSolution.slnx --layer project:Research.Api=API --layer project:Research.Application=Application --layer project:Research.Infrastructure=Infrastructure --allow API=Application --allow Application=Infrastructure
```

`controllers.txt` records the principal path, layers, transitions and source evidence. `exceptions.txt` records the complete throw/catch landscape and the selected action's explicit-origin witnesses. `reach.txt` lists each callable reachable from the action once, with distance, layer and one shortest witness path. `layers.txt` shows the aggregated layer edges and the `layer-dependency` finding: the fixture's `OrdersController.Interface(IRepository)` makes the API layer depend on an Infrastructure type, which the allowed dependencies do not permit. Their `.stderr.txt` companions contain gap and budget summaries. Fixture exit code **1** is expected: top-level callable ownership and external exception origins remain outside scope.

The fixture exercises caught, unrelated and incompatible catches, rethrow/replacement, filters, recursion, interface dispatch, awaited/caught operations, unobserved tasks and separately reported ASP.NET exception middleware registration. Additional edge cases are tested through independent Core snapshots and in-memory semantic extraction.

The host's own `sharpview.slnx` was also inspected as a loader smoke check. An external Web API experiment remains pending; these samples establish fixture behavior only.

The diagram file includes project groups, per-edge source evidence, missing-body boundaries and extraction gaps. Its CLI command prints the absolute saved path; choose a new `--output` path for each invocation. `exceptions --summary` provides finite bottom-up effects and uncertainty; detailed exception samples above show the original path-report form, which now also includes ownerless extraction gaps.
