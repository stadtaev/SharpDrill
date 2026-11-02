# Fixture reports

These reports were produced from the repository-owned three-project Web API fixture after its explicit SDK build. Paths and line numbers are source evidence from this checkout, not portable runtime traces.

Reproduce with the built CLI (`F=tests/Fixtures/ResearchSolution/ResearchSolution.slnx`, `R='Research.Api.OrdersController.Get()'`):

```sh
CLI="dotnet src/SharpDrill.Cli/bin/Debug/net10.0/SharpDrill.Cli.dll"
$CLI projects $F                                   # projects.txt
$CLI projects $F --mermaid --output projects.md    # projects.md
$CLI exceptions $F                                 # exceptions.txt
$CLI exceptions $F --uncaught --root "$R"          # exceptions-uncaught.txt
$CLI exceptions $F --caught                        # exceptions-caught.txt
$CLI exceptions $F --miscaught                     # exceptions-miscaught.txt
$CLI diagram $F --root "$R" --output diagram.md    # diagram.md
```

- `projects.txt` is the `<ProjectReference>` tree with each project's evaluated settings. The fixture inherits `Nullable=enable` from the repository's `Directory.Build.props`; `Research.Infrastructure` sets `WarningsAsErrors=nullable`. `projects.md` is the same map as a Mermaid diagram with a settings table.
- `exceptions.txt` lists every explicit throw with its type, method and location (rethrows are not new exceptions).
- `exceptions-uncaught.txt` is the bottom-up summary for one action: each exception that may escape it, with one example path.
- `exceptions-caught.txt` lists, over all actions, each origin and the catch it reaches, with the handler's outcome (handles, rethrows, replaces) and filtered or uncertain matches marked as possible.
- `exceptions-miscaught.txt` shows the fixture's `Service.Incompatible()`, whose `catch (ArgumentException)` does not match the `StorageException` passing through it, and the same miss in the `async void` `Service.Fire()`, where the exception then ends the process.

Their `.stderr.txt` companions contain the one-line summary and, when the analysis stopped somewhere, a `Not followed:` line. The caught and miscaught views exit **1** on the fixture because it deliberately contains an interface without implementation and a task that is never awaited (its recursion is followed, not a gap), and the diagram does because it is partial (see its boundaries and gaps); the other samples exit 0.

The fixture exercises caught, unrelated and incompatible catches, rethrow/replacement, filters, recursion, interface dispatch, awaited/caught operations, unobserved tasks, async void methods and ASP.NET exception middleware registration (its findings carry a `Handled:` line). Additional edge cases are tested through independent Core snapshots and in-memory semantic extraction.

The host's own `SharpDrill.slnx` was also loaded as a smoke check. An external Web API experiment remains pending; these samples establish fixture behavior only.

The diagram file includes project groups, per-edge source evidence, missing-body boundaries and extraction gaps.
