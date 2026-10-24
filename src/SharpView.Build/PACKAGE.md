# SharpView (MSBuild package)

Reports [SharpView](https://github.com/stadtaev/sharpview)'s exception findings as build warnings after each build: in `dotnet build`, Visual Studio's Error List and VS Code's Problems panel. Build agents need nothing extra; `dotnet restore` brings it.

```xml
<!-- the Web API project -->
<ItemGroup>
  <PackageReference Include="SharpView" Version="x.y.z" />
</ItemGroup>
```

| Code | Finding |
| --- | --- |
| `SV0001` | An exception may escape a controller action: no catch on the way. |
| `SV0002` | The same, but a global exception handler (exception middleware or filter) receives it. |
| `SV0003` | A throw passes through a `try` whose catch clauses do not match its type. |
| `SV0004` | A third-party call with no catch on the way from the action (only with `--strict`). |
| `SV0000` | The analysis itself failed; build with `-v:n` to see why. |

Each warning points at the throw (SV0001/2), the `try` (SV0003) or the call (SV0004), and names the actions and one example path. Interface and virtual calls are followed into their source implementations; such paths say "possible, not proven".

## Suppressing one finding

Use the C# suppression forms where the warning points (the throw, the `try` or the call):

```csharp
#pragma warning disable SV0003 // ArgumentException is the only one we can recover from here
try { Load(); } catch (ArgumentException) { }
#pragma warning restore SV0003

[SuppressMessage("SharpView", "SV0002:Mapped to 404 by middleware")]
public Order Find(int id) => orders.Get(id) ?? throw new NotFoundException(id);
```

A pragma covers the code up to its `restore` (or the end of the file); an attribute covers the method, property or type it is on. Assembly-level `[assembly: SuppressMessage]` is not read. The build log counts suppressed warnings. If the IDE flags these as unnecessary suppressions (IDE0079), add `dotnet_remove_unnecessary_suppression_exclusions = SV0001,SV0002,SV0003,SV0004` to `.editorconfig`.

## Settings

```xml
<PropertyGroup>
  <!-- e.g. when exception middleware maps exceptions to responses on purpose -->
  <NoWarn>$(NoWarn);SV0002</NoWarn>
  <!-- fail the build -->
  <WarningsAsErrors>$(WarningsAsErrors);SV0001</WarningsAsErrors>
  <!-- run in CI only -->
  <SharpViewEnabled Condition="'$(CI)' != 'true'">false</SharpViewEnabled>
  <!-- default: this project and the projects it references -->
  <SharpViewInput>$(MSBuildThisFileDirectory)../MyApi.slnx</SharpViewInput>
  <!-- extra `sharpview exceptions` options; strict adds SV0004 -->
  <SharpViewArguments>--strict</SharpViewArguments>
</PropertyGroup>
```

The analysis loads the whole project graph, so it adds seconds to every build. It does not run during the IDE's background design-time builds, and warnings appear after a build, not while typing. Requires a .NET 10 (or newer) SDK. For the command-line tool, install `SharpView.Cli` (`dotnet tool install -g SharpView.Cli`). Licensed under MIT.
