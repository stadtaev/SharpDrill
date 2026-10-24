# SharpView libraries

Building blocks of [SharpView](https://github.com/stadtaev/sharpview), source-backed analysis of ASP.NET Core Web APIs. Install the `SharpView.Cli` .NET tool (`dotnet tool install -g SharpView.Cli`) to use it from the command line, or reference the `SharpView` package to get its exception findings as build warnings; reference these packages to build your own analyses and rules.

| Package | Contents |
| --- | --- |
| `SharpView.Graph` | A small generic, immutable directed multigraph with breadth-first search, shortest paths, reachability, quotients and strongly connected components. Knows nothing about C#. |
| `SharpView.Core` | The fact model (types, callables, call sites, throws, catches), `ProgramIndex`, and the queries over it: reachability, call paths, exception propagation and handling, the MSBuild project map, and layer rules. No Roslyn dependency; works on a loaded or deserialized snapshot. |
| `SharpView.Roslyn` | Loads a `.csproj`, `.sln` or `.slnx` with MSBuild and Roslyn and extracts the fact snapshot. Never restores, builds or edits the analysed code. |
| `SharpView.Export.Mermaid` | Formats project maps and call diagrams as Mermaid flowcharts in Markdown. |

```csharp
MSBuildBootstrap.RegisterSdk();
var snapshot = await RoslynProgramLoader.LoadAsync(new AnalysisRequest("MyApi.slnx"));
var index = new ProgramIndex(snapshot);
var projects = ProjectMap.Build(snapshot);
```

See the repository README for the complete library walkthrough. Licensed under MIT.
