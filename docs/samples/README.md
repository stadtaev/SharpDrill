# Samples

Outputs on the three-project Web API in `tests/Fixtures/ResearchSolution` (build it first). Paths and lines are source
evidence from this checkout.

```sh
F=tests/Fixtures/ResearchSolution/ResearchSolution.slnx
CLI="dotnet src/SharpDrill.Cli/bin/Debug/net10.0/SharpDrill.Cli.dll"   # run in bash
$CLI projects $F                                           # projects.txt
$CLI projects $F --mermaid --output projects.md            # projects.md
$CLI exceptions $F                                         # exceptions.txt (uncaught)
$CLI exceptions caught $F                                  # exceptions-caught.txt
$CLI exceptions miscaught $F                               # exceptions-miscaught.txt
$CLI exceptions thrown $F                                  # exceptions-thrown.txt
$CLI diagram $F --from 'OrdersController.Get()' --output diagram.md
```

Each `.stderr.txt` holds the summary line and `Not followed:`. The exception views exit 1 on purpose: the fixture has an
interface without an implementation and a task that is never awaited. It also exercises caught, unrelated and
incompatible catches, rethrow and replacement, filters, recursion, async void and `UseExceptionHandler`, whose findings
carry a `Handled:` line. `Program.<Main>$` is an entry point too, but it calls only framework code, so it adds nothing.
