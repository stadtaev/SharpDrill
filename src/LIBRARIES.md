# SharpDrill libraries

Building blocks of [SharpDrill](https://github.com/stadtaev/SharpDrill), source-backed analysis of .NET code (Web APIs, console apps, libraries). Install the `SharpDrill` .NET tool (`dotnet tool install -g SharpDrill`) to use it from the command line, or reference the `SharpDrill.MSBuild` package to get its exception findings as build warnings; reference these packages to build your own analyses and rules.

| Package | Contents |
| --- | --- |
| `SharpDrill.Graph` | A small generic, immutable directed multigraph with subgraphs, quotients and strongly connected components. Knows nothing about C#. |
| `SharpDrill.Core` | `Codebase` and its queries: entry points, the exception report, call diagrams and the MSBuild project map, over a plain fact model. No Roslyn dependency. |
| `SharpDrill.Roslyn` | `Codebase.OpenAsync(path)`: loads a `.csproj`, `.sln` or `.slnx` with MSBuild and Roslyn. Never restores, builds or edits the analysed code. |
| `SharpDrill.Export.Mermaid` | Formats project maps and call diagrams as Mermaid flowcharts in Markdown. |

```csharp
var codebase = await Codebase.OpenAsync("MyApp.slnx");   // using SharpDrill.Core; using SharpDrill.Roslyn;
var uncaught = codebase.Exceptions().Uncaught;
var diagram = codebase.Diagram("OrdersController.Get");
var projects = codebase.Projects();
```

See [docs/library.md](https://github.com/stadtaev/SharpDrill/blob/main/docs/library.md) for the API. Licensed under MIT.
