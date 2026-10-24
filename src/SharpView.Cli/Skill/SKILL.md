---
name: sharpview
description: Analyse an ASP.NET Core Web API with the sharpview CLI. Finds exceptions that may escape controller actions, catches that miss, third-party calls with no catch, what an endpoint reaches, call paths and the project reference map. Use for questions about exception handling, error paths, endpoint impact or the structure of a .NET Web API, and for SV0001-SV0004 build warnings.
# Written by `sharpview skill` {{version}}. Rerun it after `dotnet tool update -g SharpView.Cli` to refresh this file.
---

# SharpView

`sharpview` reads the C# source with Roslyn and reports facts with `file:line` evidence. Paths are call relationships in source, not runtime traces.

## Before running

- Install it with `dotnet tool install -g SharpView.Cli`. It needs a .NET 10 or newer SDK.
- Restore the solution first (`dotnet restore`). Build it too: code that does not compile gives incomplete results, and stderr says so.
- Run it in the folder that holds the `.sln`/`.slnx` (or the only `.csproj`), or pass the path right after the command: `sharpview exceptions path/to/App.slnx --uncaught`.
- Each run loads the whole solution, which takes seconds to minutes. Pick the right view instead of running everything.

## Which command

| Question | Command |
| --- | --- |
| Which exceptions can leave an endpoint with no catch on the way? | `sharpview exceptions --uncaught` |
| Is every call into a NuGet package or DLL wrapped in a catch? | `sharpview exceptions --uncaught --strict` |
| Which `try` blocks catch the wrong exception type? | `sharpview exceptions --miscaught` |
| Where is each exception caught, and what does the handler do? | `sharpview exceptions --caught` |
| Where is anything thrown? | `sharpview exceptions` |
| What can an endpoint reach? Which endpoints reach this method? | `sharpview reach` (each method once, shortest path) |
| Every call path through one small endpoint | `sharpview paths --root "<signature>"` (grows exponentially; prefer `reach` on large code) |
| A picture of one endpoint | `sharpview diagram --root "<signature>"` (Mermaid, saved to a Markdown file) |
| How do the projects reference each other, and how is each built? | `sharpview projects` |
| Exact method IDs and signatures for `--root` | `sharpview inspect --methods` |

Useful options: `--root "<exact signature or ID>"` limits a run to one action (`reach`/`paths` accept any method), `--locations` adds `file:line` for every step, `--declared-only` stops at interface calls instead of following their implementations, and `--budget N` raises the work limit when stderr says the run stopped early.

## Reading the output

- Results go to stdout. Stderr has the summary line, compile errors and limits.
- `Not followed:` lists where the analysis stopped: an interface with no implementation in source, a task that is never awaited, a `finally` that may throw. Report these to the user as open questions. Do not guess what happens there.
- An exception entry shows where it is `Thrown` (and `Rethrown`), the actions it `Escapes` from, and an example `Path` from the action down to the throw.
- `Path (possible, not proven)` and `(assumed implementation)` mean the path goes through an interface or virtual call into an implementation found in source. SharpView does not read DI registrations, so it cannot know which implementation runs.
- `Handled by a global exception handler: ...` heads the findings that exception middleware or a global MVC exception filter still receives. They are lower priority, but the client still gets whatever that handler returns, often a 500.
- The `--strict` section lists third-party calls (NuGet packages and referenced DLLs, not .NET itself) that no catch meets anywhere on the way from the action. Any catch type around the call or a caller counts as wrapping it. A catch that only rethrows does not.
- `--miscaught` entries say which `try` missed (`Missed by`) and what happens next (`Then`).
- Library and framework code is a boundary: the exceptions it throws are not modelled, and neither are implicit exceptions such as a null dereference. No finding does not prove that nothing is thrown.

Exit codes: 0 complete, 1 usable but with gaps, Unknown results or limits (normal on real code), 2 invalid request or fatal, 130 cancelled.

## Build warnings (the SharpView package)

A project that references the `SharpView` NuGet package gets the findings as build warnings:

| Code | Finding | Points at |
| --- | --- | --- |
| SV0001 | An exception may escape a controller action. | the throw |
| SV0002 | The same, but a global exception handler receives it. | the throw |
| SV0003 | A `try` whose catch clauses do not match the exception. | the `try` |
| SV0004 | A third-party call with no catch on the way (`--strict`). | the call |
| SV0000 | The analysis itself failed (build with `-v:n` to see why). | |

`sharpview exceptions --format msbuild` prints the same warnings without building.

To silence one finding, put `#pragma warning disable SV000N` (with a reason) or `[SuppressMessage("SharpView", "SV000N:reason")]` on the method, where the warning points. Suppress only when the user agrees the finding is intended.

## Fixing findings

- Uncaught: catch where the code can recover or translate the exception (for example into a 404 or a ProblemDetails response). A catch-all that only logs is not a fix. If the global handler is the intended design, say so instead of adding catches.
- Miscaught: catch the type that is actually thrown, or remove a catch that can never match.
- After a change, rerun the same command and confirm the finding is gone.
