# SharpDrill libraries

Building blocks of [SharpDrill](https://github.com/stadtaev/SharpDrill), source-backed analysis of ASP.NET Core Web APIs. Install the `SharpDrill` .NET tool (`dotnet tool install -g SharpDrill`) to use it from the command line, or reference the `SharpDrill.MSBuild` package to get its exception findings as build warnings; reference these packages to build your own analyses and rules.

| Package | Contents |
| --- | --- |
| `SharpDrill.Graph` | A small generic, immutable directed multigraph with subgraphs, quotients and strongly connected components. Knows nothing about C#. |
| `SharpDrill.Core` | The fact model (types, callables, call sites, throws, catches), `ProgramIndex`, and the queries over it: exception propagation and handling, call diagrams, the MSBuild project map, and layer rules. No Roslyn dependency. |
| `SharpDrill.Roslyn` | Loads a `.csproj`, `.sln` or `.slnx` with MSBuild and Roslyn and extracts the fact snapshot. Never restores, builds or edits the analysed code. |
| `SharpDrill.Export.Mermaid` | Formats project maps and call diagrams as Mermaid flowcharts in Markdown. |

```csharp
MSBuildBootstrap.RegisterSdk();
var snapshot = await RoslynProgramLoader.LoadAsync(new AnalysisRequest("MyApi.slnx"));
var index = new ProgramIndex(snapshot);
var projects = ProjectMap.Build(snapshot);
```

See the repository README for the complete library walkthrough. Licensed under MIT.
