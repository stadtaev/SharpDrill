# SharpDrill.MSBuild

Reports [SharpDrill](https://github.com/stadtaev/SharpDrill)'s exception findings as build warnings after each build: in `dotnet build`, Visual Studio's Error List and VS Code's Problems panel. Build agents need nothing extra; `dotnet restore` brings it.

The usual way to run SharpDrill is the `SharpDrill` tool, in CI or on demand: `dotnet sharpdrill exceptions MyApi.slnx --format msbuild` prints the same warnings without adding seconds to every build. Use this package when you want them in every build without anyone running the tool.

```xml
<!-- the app or Web API project -->
<ItemGroup>
  <PackageReference Include="SharpDrill.MSBuild" Version="x.y.z" />
</ItemGroup>
```

| Code | Finding |
| --- | --- |
| `SD0001` | An exception may escape an entry point (controller action, `Main`, a library's public method): no catch on the way. |
| `SD0002` | The same, but a global exception handler (exception middleware or filter) receives it. |
| `SD0003` | A throw passes through a `try` whose catch clauses do not match its type. |
| `SD0004` | A third-party call with no catch on the way from the entry point (only with `--third-party`). |
| `SD0005` | An exception leaves an `async void` method: no caller can catch it, so the process ends. |
| `SD0000` | The analysis itself failed; build with `-v:n` to see why. |

Each warning points at the throw (SD0001/2/5), the `try` (SD0003) or the call (SD0004), and names the entry points and one example path. Interface and virtual calls are followed into their source implementations; such paths say "possible, not proven".

## Suppressing one finding

Use the C# suppression forms where the warning points (the throw, the `try` or the call):

```csharp
#pragma warning disable SD0003 // ArgumentException is the only one we can recover from here
try { Load(); } catch (ArgumentException) { }
#pragma warning restore SD0003

[SuppressMessage("SharpDrill", "SD0002:Mapped to 404 by middleware")]
public Order Find(int id) => orders.Get(id) ?? throw new NotFoundException(id);
```

A pragma covers the code up to its `restore` (or the end of the file); an attribute covers the method, property or type it is on. Assembly-level `[assembly: SuppressMessage]` is not read. The build log counts suppressed warnings. If the IDE flags these as unnecessary suppressions (IDE0079), add `dotnet_remove_unnecessary_suppression_exclusions = SD0001,SD0002,SD0003,SD0004,SD0005` to `.editorconfig`.

## Settings

```xml
<PropertyGroup>
  <!-- e.g. when exception middleware maps exceptions to responses on purpose -->
  <NoWarn>$(NoWarn);SD0002</NoWarn>
  <!-- fail the build -->
  <WarningsAsErrors>$(WarningsAsErrors);SD0001</WarningsAsErrors>
  <!-- run in CI only -->
  <SharpDrillEnabled Condition="'$(CI)' != 'true'">false</SharpDrillEnabled>
  <!-- default: this project and the projects it references -->
  <SharpDrillInput>$(MSBuildThisFileDirectory)../MyApi.slnx</SharpDrillInput>
  <!-- extra `sharpdrill exceptions` options; --third-party adds SD0004 -->
  <SharpDrillArguments>--third-party</SharpDrillArguments>
</PropertyGroup>
```

The analysis loads the whole project graph, so it adds seconds to every build. It does not run during the IDE's background design-time builds, and warnings appear after a build, not while typing. Requires a .NET 10 (or newer) SDK. For the command-line tool, install `SharpDrill` (`dotnet tool install -g SharpDrill`). Licensed under MIT.
