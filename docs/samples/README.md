# Fixture reports

These reports were produced from the repository-owned three-project Web API fixture after its explicit SDK build. Paths and line numbers are source evidence from this checkout, not portable runtime traces.

Reproduce with the built CLI:

```sh
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll controllers tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()' --layer project:Research.Api=API --layer project:Research.Application=Application --layer project:Research.Infrastructure=Infrastructure --locations
dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll exceptions tests/Fixtures/ResearchSolution/ResearchSolution.slnx --root 'Research.Api.OrdersController.Get()'
```

`controllers.txt` records the principal path, layers, transitions and source evidence. `exceptions.txt` records the complete throw/catch landscape and the selected action's explicit-origin witnesses. Their `.stderr.txt` companions contain gap and budget summaries. Fixture exit code **1** is expected: top-level callable ownership and external exception origins remain outside scope.

The fixture exercises caught, unrelated and incompatible catches, rethrow/replacement, filters, recursion, interface dispatch, awaited/caught operations, unobserved tasks and separately reported ASP.NET exception middleware registration. Additional edge cases are tested through independent Core snapshots and in-memory semantic extraction.

The host's own `sharpview.slnx` was also inspected as a loader smoke check. An external Web API experiment remains pending; these samples establish fixture behavior only.
