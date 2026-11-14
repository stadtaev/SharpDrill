---
name: sharpdrill
description: Analyse a .NET codebase (Web API, console app or library) with the sharpdrill CLI. Finds exceptions that may escape entry points, catches that miss, third-party calls with no catch, call diagrams of an entry point and the project reference map. Use for questions about exception handling, error paths, what a method calls or how a .NET solution is structured, and for SD0001-SD0005 build warnings.
---
<!-- Written by `sharpdrill skill` {{version}}. Rerun it after `dotnet tool update -g SharpDrill` to refresh this file. -->

# SharpDrill

`sharpdrill` reads C# with Roslyn and reports facts with `file:line` evidence. Paths are call relationships in source, not runtime traces.

## Running it

- Install: `dotnet tool install -g SharpDrill` (.NET 10 or newer SDK). Run it in the folder with the `.sln`/`.slnx` (or the only `.csproj`), or pass the path: `sharpdrill exceptions path/to/App.slnx`.
- Packages are restored automatically. A build is needed only when stderr reports compile errors.
- The first run loads the solution (seconds to minutes); later runs reuse it while nothing changed, so several commands in a row are cheap.
- Results go to stdout; the one-line summary, `Not followed:` and limits go to stderr. Exit 1 is normal on real code (some calls could not be followed); 2 means a wrong request, and the message says what to pass.

## Commands

Everything traced starts at an **entry point**: a controller action, `Main` or top-level statements (`Program.<Main>$`), or a public method of a library project nothing references.

| Question | Command |
| --- | --- |
| Which exceptions can leave the app or an endpoint with no catch on the way? | `sharpdrill exceptions` |
| ... from one entry point only? | `sharpdrill exceptions --from OrdersController.Get` |
| Which `try` blocks catch the wrong exception type? | `sharpdrill exceptions miscaught` |
| Where is each exception caught, and what does the handler do? | `sharpdrill exceptions caught` |
| Where is anything thrown at all? | `sharpdrill exceptions thrown` |
| Is every call into a NuGet package or DLL wrapped in a catch? | `sharpdrill exceptions --third-party` |
| What does one entry point call? | `sharpdrill diagram --from <name>` (Mermaid, saved to a Markdown file) |
| How do the projects reference each other, and how is each built? | `sharpdrill projects` |

`--from` takes a signature or any unique part of one (`OrdersController.Get`, `Program.<Main>$`); when several match, the error lists them to pick from. `--locations` adds `file:line` to every path step. `--declared-only` stops at interface calls instead of following their implementations. `--budget N` raises the work limit when stderr says the run stopped early.

For a question none of these answers directly (who calls X, is this exception ever caught, what reaches this method), combine them: `thrown` to find throws, `caught`/`miscaught` for one throw's fate, `diagram --from` to see what an entry point reaches. Then read the code at the reported lines.

## Reading the output

- Findings are numbered after an `Overview:` that counts them by type.
- `Thrown` (and `Rethrown`) is where the exception starts; `(in the entry point)` means the throw is in the entry point itself.
- `Path` runs from the entry point down to the throw. `->` is a direct call; `~>` an interface or virtual call into an implementation found in source. SharpDrill does not read DI registrations, so `~>` is a possibility; `(1 of N implementations)` means source has several, so check which one is registered before acting. `same as 2` or `as in 2 up to X, then ...` reuse an earlier path.
- `Escapes` lists the entry points it may leave when there is more than the one the path starts at.
- `Handled` names exception middleware or a global MVC filter that still receives it; those come last. Lower priority, but the client still gets whatever that handler returns, often a 500.
- `Crashes`: an exception leaves an `async void` method. Nothing catches it and the process ends. Fix with a catch inside that method or by returning `Task`.
- `miscaught` entries show `Missed by` (the `try` and its clauses) and `Then` (what happens next). `caught` entries show `Caught by` and `Handler`.
- `--third-party` adds third-party calls with no catch anywhere on the way. Any catch type counts; one that only rethrows does not. Logging and `Microsoft.Extensions` are left out (Caching stays).
- `Not followed:` lists where the analysis stopped (an interface with no implementation in source, a task never awaited, a `finally` that may throw). Report these as open questions; do not guess what happens there.
- Library code and implicit exceptions (null dereference) are not modelled. No finding does not prove nothing is thrown.

## Build warnings (`--format msbuild`, or the SharpDrill.MSBuild package after each build)

| Code | Finding | Points at |
| --- | --- | --- |
| SD0001 | An exception may escape an entry point. | the throw |
| SD0002 | The same, but a global exception handler receives it. | the throw |
| SD0003 | A `try` whose catch clauses do not match the exception. | the `try` |
| SD0004 | A third-party call with no catch on the way (`--third-party`). | the call |
| SD0005 | An exception leaves an `async void` method and ends the process. | the throw |
| SD0000 | The analysis itself failed (build with `-v:n` to see why). | |

Findings never set the exit code; in CI gate on the warnings. To silence one finding, put `#pragma warning disable SD000N` (with a reason) or `[SuppressMessage("SharpDrill", "SD000N:reason")]` where the warning points, and only when the user agrees the finding is intended.

## Fixing findings

- Uncaught: catch where the code can recover or translate the exception (for example into a 404 or ProblemDetails). A catch-all that only logs is not a fix. If the global handler is the intended design, say so instead of adding catches.
- Miscaught: catch the type that is actually thrown, or remove a catch that can never match.
- After a change, rerun the same command and confirm the finding is gone.
