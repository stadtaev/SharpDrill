# SharpDrill

Source-backed analysis of ASP.NET Core Web APIs, for people and for agents. Every result points at `file:line:column`, and anything the analysis cannot decide is reported as *Unknown* instead of being hidden.

```sh
dotnet tool install -g SharpDrill
cd MyApi && sharpdrill projects   # the .sln/.slnx (or single .csproj) in the current directory; or pass a path
```

Requires a .NET 10 (or newer) SDK. Missing packages are restored automatically (`--no-restore` turns that off); SharpDrill never builds or edits the analysed code. Repeat runs reuse the last analysis while nothing it read has changed (`--no-cache` analyses again). Loading evaluates MSBuild projects and may run project tooling and source generators, so it is not a sandbox.

## Commands

| Command | Answers |
| --- | --- |
| `projects` | The `<ProjectReference>` tree with each project's framework, `Nullable`, whether nullable (or all) warnings are errors, and compiler warning counts. `--mermaid` saves it as a diagram. |
| `exceptions` | Every explicit throw with its method, type and project. |
| `exceptions --uncaught` | Throws that may escape a controller action because no catch is on the way. |
| `exceptions --caught` | Where each exception is thrown and where it is caught, with the handler's outcome. |
| `exceptions --miscaught` | Throws that pass through a `try` whose catch clauses do not match their type. |
| `diagram` | A Mermaid call graph of one controller action. |
| `skill` | Installs the agent skill for GitHub Copilot and Claude Code in this repository (`.github/skills` for Copilot, `.claude/skills` for Claude Code), or `--user` for every repository. Rerun after updating the tool. |

`sharpdrill exceptions --format msbuild` prints the uncaught and miscaught findings as build-log warnings (`SD0001` may escape an action, `SD0002` reaches a global exception handler, `SD0003` miscaught, `SD0004` third-party call with `--strict`, `SD0005` leaves an async void method), for a CI job or a VS Code task with the `$msCompile` problem matcher. `#pragma warning disable SD000N` or `[SuppressMessage("SharpDrill", "SD000N")]` where a warning points accepts that finding. For teams, install it as a local tool (`dotnet new tool-manifest`, `dotnet tool install SharpDrill`) so CI gets the same version with `dotnet tool restore`; the repository README has a GitLab CI job. To get the warnings in every build instead, Visual Studio's Error List included, reference the optional `SharpDrill.MSBuild` package from the Web API project.

Run `sharpdrill --help` for every option. Exit codes: 0 complete, 1 usable but with gaps, Unknown results or limits, 2 invalid request, 130 cancelled.

## Scope

Static analysis of C# source: paths are call relationships, not execution traces. Interface and virtual calls are recorded at their declared target; `exceptions` and `diagram` follow their source implementations as marked possibilities (`--declared-only` turns that off). Implicit exceptions (null dereference, framework calls) are not modelled, and "may escape" never means "unhandled": middleware may still handle it.

Documentation, samples and source: <https://github.com/stadtaev/SharpDrill>. Licensed under MIT.
