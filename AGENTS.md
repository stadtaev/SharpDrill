# AGENTS.md

This file provides guidance to AI coding agents working with code in this repository.

SharpView (`sharpview` dotnet tool) is a Roslyn-based static analyser for ASP.NET Core Web APIs: project map, exception
propagation from controller actions, reachability, call paths and Mermaid diagrams. Start with `docs/overview.md`
(pipeline, principles); `docs/model.md` has detailed fact/query semantics, `docs/coverage.md` what is and isn't modelled,
and `docs/lessons.md` the design rules learned from the first real-world run.

FOR NOW IT'S STARTING MAIN GOAL IS TO PROIDE ACCURATE FACTS WITH MINIMAL NECESSARY VOLUME OF DETAILS AND NONSENSE. FAVOR QUALITY AND INTUITIVE API OVER QUANTITY  

## Commands

Requires the .NET 10 SDK from `global.json`.

```sh
dotnet restore sharpview.slnx
dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx   # tests analyse this fixture; build it first
dotnet test sharpview.slnx                                           # ~2 minutes
dotnet test tests/SharpView.Tests --filter "FullyQualifiedName~ExtractionTests.Some_test_name"
dotnet pack sharpview.slnx -c Release                                # CI also packs

# Run the CLI from source on the fixture
dotnet run --project src/SharpView.Cli -- exceptions tests/Fixtures/ResearchSolution/ResearchSolution.slnx --miscaught
```

Exit code 1 on the fixture is expected (top-level statements in `Program.cs` are a gap). 0 = complete, 1 = usable but
with gaps/Unknown/limits, 2 = invalid request or fatal, 130 = cancelled.

## Architecture

Pipeline: **Load → Extract facts → Index → Queries → (Rules) → CLI report**.

- `SharpView.Roslyn` — `RoslynProgramLoader` opens the solution via MSBuild (never restores/builds the target).
  `Extraction/FactExtractor*.cs` registers source symbols and bodies; `BodyWalker` walks each body's operation tree and
  records call sites, throws, catches, regions and `AnalysisGap`s. All Roslyn knowledge lives here.
- `SharpView.Core` — plain immutable records (`Model/Facts.cs`, the `ProgramSnapshot`), `ProgramIndex` (lookups, lazy
  call/dependency graphs) and queries (`ExceptionQuery` per-path witnesses for `--caught`/`--miscaught`,
  `ExceptionSummaryQuery` bottom-up summaries for `--uncaught`, `ExceptionTransfer` shared catch/finally transfer,
  `ReachabilityQuery`, `CallChainQuery`). **Core has no Roslyn or Mermaid dependency**: it depends only on `SharpView.Graph`
  and can run on a snapshot loaded from JSON.
- `SharpView.Graph` — generic immutable directed multigraph (BFS, SCC, quotient); knows nothing about C#.
- `SharpView.Export.Mermaid` — Mermaid/Markdown formatting; the only project with the Mermaid NuGet dependency.
- `SharpView.Cli` — argument parsing (`Arguments.cs`, strict per-command option sets), one `ICliCommand` per command
  under `Commands/`, shared naming in `Display.cs`. Formatting only; no analysis logic. `ReportCommand` prints grouped
  gaps to stderr after each report; results go to stdout, diagnostics to stderr. Packed as the `SharpView.Cli` tool.
- `SharpView.Build` — no code: the `SharpView` MSBuild package. `build/SharpView.targets` runs the packed CLI's
  `exceptions --format msbuild` after each build, so findings become SV000N warnings in dotnet build, VS and VS Code.

## Principles that shape changes

- **Declared vs candidate vs runtime.** A call site records its declared target. Implementations/overrides are separate
  `ImplementationFact` candidates; following them is opt-in (or default in exception views) and every such step is
  marked as a possibility. Never claim a runtime target (DI registrations are not read).
- **Unknown must be rare and actionable** (see `docs/lessons.md`). Library/framework calls are the boundary of the claim,
  counted but not Unknown. Compiler-provided members (auto-accessors, record members, implicit constructors) are
  `BodyStatus.Synthesized`, not missing. A new gap or Unknown must be something the analysis genuinely cannot decide;
  if it fires on trivial code it is noise.
- **Evidence and determinism.** Results carry `SourceEvidence`; output is ordinal-sorted and byte-identical across runs.
- When code behaviour and `docs/coverage.md` disagree, fix one of them; regenerate `docs/samples/` (commands in
  `docs/samples/README.md`) when CLI output changes.

## Tests

All in `tests/SharpView.Tests` (xUnit; `InternalsVisibleTo` exposes Roslyn internals). Ignore the stray
`tests/Sharpview.Tests` directory (build output only).

- `ExtractionTests` compile inline C# in an `AdhocWorkspace` and run `FactExtractor` directly — the place for extraction
  and gap behaviour.
- `CoreTests`, `ReachabilityTests`, `ArchitectureTests`, etc. build `ProgramSnapshot`s by hand.
- `CliTests`, `CliContractTests`, `DiagramCliTests`, `ResearchTests` run against the ResearchSolution fixture and assert
  exact output strings and counts (witnesses, gaps, global caveats); changes to extraction or formatting usually require
  updating these.

## Code style

Dense, expression-heavy C# with file-scoped namespaces, primary constructors, records and collection expressions; long
lines are normal. Comments are short and explain *why*. Match this density rather than expanding code.
