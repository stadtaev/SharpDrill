# SharpView

Source-backed analysis of ASP.NET Core Web APIs, for people and for agents. Every result points at `file:line:column`, and anything the analysis cannot decide is reported as *Unknown* instead of being hidden.

```sh
dotnet tool install -g SharpView
sharpview projects MyApi.slnx
```

Requires a .NET 10 (or newer) SDK. Restore the solution first: SharpView never restores, builds or edits the analysed code. Loading evaluates MSBuild projects and may run project tooling and source generators, so it is not a sandbox.

## Commands

| Command | Answers |
| --- | --- |
| `projects` | The `<ProjectReference>` tree with each project's framework, `Nullable`, whether nullable (or all) warnings are errors, and compiler warning counts. `--mermaid` saves it as a diagram. |
| `exceptions` | Every explicit throw with its method, type and project. |
| `exceptions --uncaught` | Throws that may escape a controller action because no catch is on the way. |
| `exceptions --caught` | Where each exception is thrown and where it is caught, with the handler's outcome. |
| `exceptions --miscaught` | Throws that pass through a `try` whose catch clauses do not match their type. |
| `reach` | Everything an action can reach, once per method, with distance and shortest path. |
| `paths` | Every call path from an action (or any `--root` method). |
| `diagram` | A Mermaid call graph of one controller action. |
| `inspect` | Extraction summary; `--methods` lists root IDs; `--json` writes the full fact snapshot. |

Run `sharpview --help` for every option. Exit codes: 0 complete, 1 usable but with gaps, Unknown results or limits, 2 invalid request, 130 cancelled.

## Scope

Static analysis of C# source: paths are call relationships, not execution traces. Interface and virtual calls are recorded at their declared target; implementations are shown only as marked candidates. Implicit exceptions (null dereference, framework calls) are not modelled, and "may escape" never means "unhandled": middleware may still handle it.

Documentation, samples and source: <https://github.com/stadtaev/sharpview>. Licensed under MIT.
