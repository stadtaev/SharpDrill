# CLI

## Getting started

```sh
dotnet tool install -g SharpDrill          # needs a .NET 10 or newer SDK; update with dotnet tool update -g SharpDrill
cd MyApp                                   # the folder with the .sln/.slnx, or the only .csproj
sharpdrill projects                        # how the projects fit together
sharpdrill exceptions                      # what may escape an entry point, one example path each
sharpdrill exceptions miscaught            # try blocks that catch the wrong type
sharpdrill exceptions --from OrdersController.Get   # one entry point
sharpdrill skill                           # let GitHub Copilot and Claude Code use it too
```

For a team, pin it as a local tool so CI gets the same version: `dotnet new tool-manifest`, `dotnet tool install
SharpDrill`, commit `dotnet-tools.json`, then `dotnet tool restore` and `dotnet sharpdrill ...` elsewhere. From source:
`dotnet run --project src/SharpDrill.Cli -- exceptions <input>`.

## Usage

```sh
sharpdrill <command> [view] [input] [options]
```

`input` is a `.sln`, `.slnx`, `.csproj` or folder; without it, the solution (or else the only project) in the current
folder. Missing packages are restored automatically; SharpDrill never builds or edits the code. The last analysis is
cached per input and reused while nothing it read has changed, so several commands in a row are cheap.

## exceptions

Traces exceptions from every **entry point**: controller actions, `Main` or top-level statements (`Program.<Main>$`), and
the public methods of a library project that no loaded project references.

| View | Answers |
| --- | --- |
| `uncaught` (default) | Which exceptions may leave an entry point with no catch on the way? One example path each. |
| `caught` | Where is each exception caught, and what does the handler do (handles, rethrows, replaces)? |
| `miscaught` | Which `try` blocks does an exception pass through because no clause matches its type, and what happens next? |
| `thrown` | Where is anything thrown? Every explicit `throw` in source, independent of entry points. |

| Option | Effect |
| --- | --- |
| `--from E` | One entry point: its ID, signature or a unique part of it (`OrdersController.Get`). Errors list the candidates. |
| `--third-party` | `uncaught` also lists calls into NuGet packages and DLLs that no catch meets on the way (not .NET itself, logging or `Microsoft.Extensions`; `Microsoft.Extensions.Caching` stays). |
| `--declared-only` | Stop at interface/virtual calls instead of following them into their source implementations. |
| `--locations` | `file:line` for every step of each path. |
| `--format msbuild` | One build warning per finding (below). Without a view: uncaught and miscaught. |
| `--budget N` | Work limit (default 100000); the output says when to raise it. |

### Reading the output

```text
1. Research.Infrastructure.StorageException
   Thrown:     Repository.Read  Research.Infrastructure/Repository.cs:6
   Escapes:    OrdersController.Filter, OrdersController.Get, OrdersController.Incompatible, OrdersController.Unrelated
   Handled:    by the global exception handler UseExceptionHandler  Research.Api/Program.cs:5
   Path:       OrdersController.Filter -> Service.Filter -> Repository.Read
```

- Findings are numbered after an `Overview:` that counts them by type. Stdout holds results; stderr has the one-line summary, `Not followed:` and limits.
- `Thrown` (and `Rethrown`) is where it starts; `(in the entry point)` means the throw is in the entry point itself. Throws of one type in one method share a block (`File.cs:10, 11`).
- `Escapes` lists the entry points it may leave when there is more than the one the path starts at.
- `Path` runs from the entry point down to the throw. `->` is a direct call; `~>` follows an interface or virtual call into an implementation found in source, a possibility since DI registrations are not read. `(1 of N implementations)` marks where source has several. `same as 2` and `as in 2 up to X, then ...` refer to an earlier path.
- `Handled` names exception middleware or a global MVC exception filter that still receives it (controller actions only); those findings come last.
- `Crashes` marks an exception leaving an `async void` method: no caller can catch it, so the process ends. Listed first.
- `miscaught` adds `Missed by` (the `try` and its clauses) and `Then` (caught further up, may escape, ...); `caught` adds `Caught by` and `Handler`.
- `Not followed:` lists where the analysis stopped: an interface with no implementation in source, a task never awaited, a `finally` that may throw. These are open questions, not findings.
- No finding does not prove nothing is thrown: library code and implicit exceptions (null dereference) are not modelled. See [coverage](coverage.md).

### Build warnings

`--format msbuild` prints `file(line,col): warning SD000N: message`, which build logs, VS Code (`$msCompile`) and Visual Studio parse.

| Code | Finding | Points at |
| --- | --- | --- |
| SD0001 | An exception may escape an entry point. | the throw |
| SD0002 | The same, but a global exception handler receives it. | the throw |
| SD0003 | A `try` whose catch clauses do not match the exception. | the `try` |
| SD0004 | A third-party call with no catch on the way (`--third-party`). | the call |
| SD0005 | An exception leaves an `async void` method and ends the process. | the throw |
| SD0000 | The analysis itself failed (`SharpDrill.MSBuild` only). | |

Accept a finding where it points with `#pragma warning disable SD000N` or `[SuppressMessage("SharpDrill", "SD000N:reason")]`;
the summary counts suppressed warnings. The `SharpDrill.MSBuild` package runs this after every build
([settings](../src/SharpDrill.Build/PACKAGE.md)).

A CI job that fails on SD0001 (exit 1 only means some calls could not be followed):

```yaml
script:
  - dotnet tool restore     # SharpDrill pinned in dotnet-tools.json
  - code=0; dotnet sharpdrill exceptions MyApp.slnx --format msbuild > sharpdrill.txt || code=$?
  - cat sharpdrill.txt; test "$code" -le 1
  - if grep -q "warning SD0001" sharpdrill.txt; then exit 1; fi
```

## diagram

A Mermaid call graph of one entry point, saved as Markdown (to `--output`, or `./diagrams_exports`; existing files are
never overwritten). Without `--from` it lists the entry points and asks for a number. Calls are grouped by project;
interface calls lead into their implementations, drawn as candidates (`--declared-only` stops at the declaration).
`--max-depth` (20), `--max-nodes` (200) and `--max-edges` (1000) bound it; a cut-short diagram says what to raise.

## projects

The `<ProjectReference>` tree as MSBuild evaluates it, each project with its framework, `Nullable`, whether nullable or
all warnings are errors, and its compiler warning count. `--mermaid` also saves it as a diagram with a settings table.

## skill

Writes the agent skill (`sharpdrill/SKILL.md`) to `.github/skills` (GitHub Copilot) and `.claude/skills` (Claude Code) at
the repository root, or with `--user` to `~/.copilot/skills` and `~/.claude/skills`. Commit it for the team; rerun after
`dotnet tool update`. It never overwrites a `SKILL.md` it did not write.

## Loading options and exit codes

`--configuration NAME` (default Debug), `--framework TFM` (multi-target inputs), `--exclude-generated`, `--include-tests`
(test projects are skipped by default), `--restore` (always restore first), `--no-restore`, `--no-cache`. The cache lives
in the local application data folder (`SHARPDRILL_CACHE_DIR` overrides it); loading evaluates MSBuild and may run
source generators, so it is not a sandbox.

| Exit | Meaning |
| --- | --- |
| 0 | Complete. |
| 1 | Usable, with gaps, Unknown results or limits (normal on real code). Findings never set the exit code. |
| 2 | Invalid request or fatal error; the message says what to do. |
| 130 | Cancelled. |
