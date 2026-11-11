# SharpDrill

Source-backed answers about a .NET codebase, for people and coding agents. SharpDrill reads C# with Roslyn and reports:

- **Exceptions:** which ones may leave an entry point with no catch on the way, where each is caught, and which slip past a `try` that does not match them.
- **Call diagrams:** a Mermaid graph of what one entry point calls, grouped by project.
- **Project map:** the `<ProjectReference>` tree with each project's framework, nullable settings and warning count.

Entry points are controller actions, `Main` or top-level statements, and the public methods of a library that nothing in the solution references, so it works on Web APIs, console apps and libraries. Every result points at `file:line`; anything the analysis cannot decide is reported, never hidden.

## Try it

```sh
dotnet tool install -g SharpDrill     # needs a .NET 10 or newer SDK
cd MyApp
sharpdrill exceptions                 # what may escape, with one example path each
sharpdrill exceptions miscaught       # try blocks that catch the wrong type
sharpdrill exceptions --from OrdersController.Get
sharpdrill skill                      # teach Copilot and Claude Code to use it
```

```text
3. Research.Infrastructure.StorageException
   Thrown:     Repository.Read  Research.Infrastructure/Repository.cs:6
   Escapes:    OrdersController.Filter, OrdersController.Get, OrdersController.Incompatible, OrdersController.Unrelated
   Handled:    by the global exception handler UseExceptionHandler  Research.Api/Program.cs:5
   Path:       OrdersController.Filter -> Service.Filter -> Repository.Read
```

From C#:

```csharp
var program = await RoslynProgramLoader.OpenAsync("MyApp.slnx");
foreach (var escape in program.Exceptions().Uncaught)
    Console.WriteLine($"{escape.ExceptionType} may escape {escape.Example.EntryPoint.DisplaySignature}");
```

## Documentation

| Read | For |
| --- | --- |
| [CLI](docs/cli.md) | Commands, options, reading the output, build warnings (SD0001–SD0005), CI and agents. |
| [Library](docs/library.md) | The C# API: open a program, entry points, the exception report, diagrams and graphs. |
| [Coverage](docs/coverage.md) | What is modelled and what is not; how to read "may escape" and "not followed". |
| [Samples](docs/samples/README.md) | Full outputs on the sample Web API in `tests/Fixtures`. |

## From source

```sh
dotnet restore SharpDrill.slnx
dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx   # the sample the tests analyse
dotnet test SharpDrill.slnx
dotnet run --project src/SharpDrill.Cli -- exceptions tests/Fixtures/ResearchSolution/ResearchSolution.slnx
```

## Packages and releases

| Package | Purpose |
| --- | --- |
| `SharpDrill` | The `sharpdrill` .NET tool. |
| `SharpDrill.MSBuild` | Optional: exception findings as warnings in every build ([settings](src/SharpDrill.Build/PACKAGE.md)). |
| `SharpDrill.Core` | Fact model, `ProgramIndex`, entry points, exception report, call diagrams, project map. No Roslyn dependency. |
| `SharpDrill.Roslyn` | Loads solutions and projects into a fact snapshot. |
| `SharpDrill.Graph` | Generic immutable directed multigraph (SCC, quotient). |
| `SharpDrill.Export.Mermaid` | Mermaid/Markdown for project maps and call diagrams. |

All packages share one version; the libraries are pre-1.0 and their API may change between minor versions. Every push to
`main` that changes code runs [release.yml](.github/workflows/release.yml): test, publish all packages to nuget.org
(trusted publishing, no stored key), tag `v<version>` and create a GitHub release. The version follows
[Conventional Commits](https://www.conventionalcommits.org/) since the last tag: `feat:` bumps minor, anything else patch,
`!` or `BREAKING CHANGE:` minor on 0.x and major from 1.0. `[skip-release]` leaves a commit out; changes limited to
`docs/` or `README.md` do not publish. Run the workflow manually with a version to publish an exact one.

Licensed under MIT.
