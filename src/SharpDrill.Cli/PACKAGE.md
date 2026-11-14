# SharpDrill

Source-backed analysis of .NET code (Web APIs, console apps, libraries), for people and coding agents. Every result points at `file:line`; anything the analysis cannot decide is reported instead of hidden.

```sh
dotnet tool install -g SharpDrill
cd MyApp
sharpdrill exceptions                       # exceptions that may escape an entry point, with one example path each
sharpdrill exceptions miscaught             # try blocks that catch the wrong type
sharpdrill skill                            # teach GitHub Copilot and Claude Code to use it
```

Entry points are controller actions, `Main` or top-level statements, and the public methods of a library that nothing in the solution references.

| Command | Answers |
| --- | --- |
| `exceptions [uncaught\|caught\|miscaught\|thrown]` | What may escape (default), where each exception is caught, which `try` blocks miss, every throw. `--from E` for one entry point, `--third-party` for unguarded NuGet/DLL calls. |
| `diagram` | A Mermaid call graph of one entry point. |
| `projects` | The `<ProjectReference>` tree with framework, nullable settings and warning counts. |
| `skill` | Installs the agent skill (`.github/skills`, `.claude/skills`, or `--user`). |

`--format msbuild` prints findings as build warnings (SD0001–SD0005) for CI and IDEs; the optional `SharpDrill.MSBuild` package runs it after every build. Requires a .NET 10 or newer SDK; missing packages are restored automatically and the code is never built or edited. Exit codes: 0 complete, 1 usable with gaps, 2 invalid request, 130 cancelled.

Static analysis: paths are call relationships, not execution traces; interface calls are followed into their source implementations as marked possibilities; library code and implicit exceptions are not modelled.

Documentation: <https://github.com/stadtaev/SharpDrill/blob/main/docs/cli.md>. Licensed under MIT.
