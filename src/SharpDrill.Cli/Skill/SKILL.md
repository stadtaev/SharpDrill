---
name: sharpdrill
description: Analyse an ASP.NET Core Web API with the sharpdrill CLI. Finds exceptions that may escape controller actions, catches that miss, third-party calls with no catch, call diagrams of an endpoint and the project reference map. Use for questions about exception handling, error paths or the structure of a .NET Web API, and for SD0001-SD0005 build warnings.
---
<!-- Written by `sharpdrill skill` {{version}}. Rerun it after `dotnet tool update -g SharpDrill` to refresh this file. -->

# SharpDrill

`sharpdrill` reads the C# source with Roslyn and reports facts with `file:line` evidence. Paths are call relationships in source, not runtime traces.

## Before running

- Install it with `dotnet tool install -g SharpDrill`. It needs a .NET 10 or newer SDK.
- Missing packages are restored automatically (`--no-restore` turns that off, and a "not restored" error then means it was needed). No build is needed, unless stderr reports compile errors: code generated during a build is then missing, and results around it are incomplete.
- Repeat runs reuse the last analysis while nothing it read has changed, so running several commands in a row is cheap. `--no-cache` analyses again.
- Run it in the folder that holds the `.sln`/`.slnx` (or the only `.csproj`), or pass the path right after the command: `sharpdrill exceptions path/to/App.slnx --uncaught`.
- Each run loads the whole solution, which takes seconds to minutes. Pick the right view instead of running everything.

## Which command

| Question | Command |
| --- | --- |
| Which exceptions can leave an endpoint with no catch on the way? | `sharpdrill exceptions --uncaught` |
| Is every call into a NuGet package or DLL wrapped in a catch? | `sharpdrill exceptions --uncaught --strict` |
| Which `try` blocks catch the wrong exception type? | `sharpdrill exceptions --miscaught` |
| Where is each exception caught, and what does the handler do? | `sharpdrill exceptions --caught` |
| Where is anything thrown? | `sharpdrill exceptions` |
| What does one endpoint call, as a picture? | `sharpdrill diagram --root "<signature>"` (Mermaid, saved to a Markdown file) |
| How do the projects reference each other, and how is each built? | `sharpdrill projects` |

Useful options: `--root "<exact signature or ID>"` limits a run to one controller action (an action name from `exceptions` output such as `OrdersController.Get` is not exact, but the error lists the matching full signatures to copy), `--locations` adds `file:line` to every path step in `exceptions`, `--declared-only` stops at interface calls instead of following their implementations (`exceptions` and `diagram`), and `--budget N` raises the work limit when stderr says the run stopped early.

## Reading the output

- Results go to stdout. Stderr has the summary line, compile errors and limits.
- `Not followed:` lists where the analysis stopped: an interface with no implementation in source, a task that is never awaited, a `finally` that may throw. Report these to the user as open questions. Do not guess what happens there.
- A report with more than one finding opens with `Overview:` and a count per exception type. Findings are numbered (`1.`, `2.`, ...) and separated by blank lines.
- An exception entry shows where it is `Thrown` (and `Rethrown`) and an example `Path` from the action down to the throw. `Escapes` lists the actions when there is more than the one the path starts at. `(in the action)` after `Thrown` means the throw is in the action itself, so there is no path line.
- In a path, `->` is a direct call and `~>` an interface or virtual call into an implementation found in source. SharpDrill does not read DI registrations, so it cannot know which implementation runs. `(1 of N implementations)` after a `~>` step means source has several: check which one is registered before acting on the finding.
- A path that repeats most of an earlier finding's path refers to it by number: `same as 2`, or `as in 2 up to <method>, then -> ...` followed by the rest. A finding with several throws of one type in one method lists their lines together (`File.cs:10, 11`).
- `Handled` names the exception middleware or global MVC exception filter that still receives the exception; those findings come last. They are lower priority, but the client still gets whatever that handler returns, often a 500.
- The `--strict` section lists third-party calls (NuGet packages and referenced DLLs, not .NET itself) that no catch meets anywhere on the way from the action. Any catch type around the call or a caller counts as wrapping it. A catch that only rethrows does not. Logging (Serilog, NLog, log4net) and `Microsoft.Extensions.*` calls are left out, and the summary line says so; `--strict --verbose` includes them. `Microsoft.Extensions.Caching.*` is always listed.
- `Crashes` marks an exception that leaves an `async void` method. No `try` around the call and no global handler sees it: it ends the process. These come first in `--uncaught`; the fix is a catch inside that method or making it return `Task`.
- `--miscaught` entries say which `try` missed (`Missed by`, with its catch clauses on the next line) and what happens next (`Then`, naming the actions it may escape). `--caught` entries name the method with the catch (`Caught by`) and what it does (`Handler`).
- Library and framework code is a boundary: the exceptions it throws are not modelled, and neither are implicit exceptions such as a null dereference. No finding does not prove that nothing is thrown.

Exit codes: 0 complete, 1 usable but with gaps, Unknown results or limits (normal on real code), 2 invalid request or fatal, 130 cancelled. Findings never set the exit code, so in CI gate on the findings (stdout, or the warnings of `--format msbuild`) and treat 1 as success.

## Build warnings (the SharpDrill.MSBuild package)

A project that references the `SharpDrill.MSBuild` NuGet package gets the findings as build warnings:

| Code | Finding | Points at |
| --- | --- | --- |
| SD0001 | An exception may escape a controller action. | the throw |
| SD0002 | The same, but a global exception handler receives it. | the throw |
| SD0003 | A `try` whose catch clauses do not match the exception. | the `try` |
| SD0004 | A third-party call with no catch on the way (`--strict`). | the call |
| SD0005 | An exception leaves an `async void` method: no caller can catch it, so the process ends. | the throw |
| SD0000 | The analysis itself failed (build with `-v:n` to see why). | |

`sharpdrill exceptions --format msbuild` prints the same warnings without building.

To silence one finding, put `#pragma warning disable SD000N` (with a reason) or `[SuppressMessage("SharpDrill", "SD000N:reason")]` on the method, where the warning points. Suppress only when the user agrees the finding is intended.

## Fixing findings

- Uncaught: catch where the code can recover or translate the exception (for example into a 404 or a ProblemDetails response). A catch-all that only logs is not a fix. If the global handler is the intended design, say so instead of adding catches.
- Miscaught: catch the type that is actually thrown, or remove a catch that can never match.
- After a change, rerun the same command and confirm the finding is gone.
