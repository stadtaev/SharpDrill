# Fixture reports

These reports were produced from the repository-owned three-project Web API fixture after its explicit SDK build. Paths and line numbers are source evidence from this checkout, not portable runtime traces.

Reproduce with the built CLI (`F=tests/Fixtures/ResearchSolution/ResearchSolution.slnx`, `R='Research.Api.OrdersController.Get()'`):

```sh
CLI="dotnet src/Sharpview.Cli/bin/Debug/net10.0/Sharpview.Cli.dll"
$CLI projects $F                                   # projects.txt
$CLI projects $F --mermaid --output projects.md    # projects.md
$CLI exceptions $F                                 # exceptions.txt
$CLI exceptions $F --uncaught --root "$R"          # exceptions-uncaught.txt
$CLI exceptions $F --caught                        # exceptions-caught.txt
$CLI exceptions $F --miscaught                     # exceptions-miscaught.txt
$CLI reach $F --root "$R" --locations              # reach.txt
$CLI paths $F --root "$R" --locations              # paths.txt
$CLI diagram $F --root "$R" --output diagram.md    # diagram.md
```

- `projects.txt` is the `<ProjectReference>` tree with each project's evaluated settings. The fixture inherits `Nullable=enable` from the repository's `Directory.Build.props`; `Research.Infrastructure` sets `WarningsAsErrors=nullable`. `projects.md` is the same map as a Mermaid diagram with a settings table.
- `exceptions.txt` lists every explicit throw and rethrow with method, type, project and location.
- `exceptions-uncaught.txt` is the bottom-up summary for one action: possible escapes with one example path each, and unknown effects.
- `exceptions-caught.txt` lists, over all actions, each origin and the catch it reaches, with the handler's outcome (handles, rethrows, replaces) and filtered or uncertain matches flagged.
- `exceptions-miscaught.txt` shows the fixture's `Service.Incompatible()`, whose `catch (ArgumentException)` does not match the `StorageException` passing through it.
- `reach.txt` lists each callable reachable from the action once, with distance, project and one shortest witness path. `paths.txt` records the principal path, projects, transitions and source evidence.

Their `.stderr.txt` companions contain gap and budget summaries. Code-analysis exit code **1** is expected on the fixture: top-level callable ownership and external exception origins remain outside scope. `projects` exits 0 because every reference was loaded.

The fixture exercises caught, unrelated and incompatible catches, rethrow/replacement, filters, recursion, interface dispatch, awaited/caught operations, unobserved tasks and separately reported ASP.NET exception middleware registration. Additional edge cases are tested through independent Core snapshots and in-memory semantic extraction.

The host's own `sharpview.slnx` was also inspected as a loader smoke check. An external Web API experiment remains pending; these samples establish fixture behavior only.

The diagram file includes project groups, per-edge source evidence, missing-body boundaries and extraction gaps. In every exception view, ownerless extraction gaps are reported once as global caveats on stderr.
