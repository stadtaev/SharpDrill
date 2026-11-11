# AGENTS.md

This file provides guidance to AI coding agents working with code in this repository.

SharpDrill (`sharpdrill` dotnet tool) is a Roslyn-based static analyser for .NET code (Web APIs, console apps,
libraries): exception propagation from entry points, Mermaid call diagrams and the project map. Docs: `docs/cli.md`
(commands and output), `docs/library.md` (the C# API), `docs/coverage.md` (what is and isn't modelled).

FOR NOW IT'S STARTING MAIN GOAL IS TO PROIDE ACCURATE FACTS WITH MINIMAL NECESSARY VOLUME OF DETAILS AND NONSENSE. FAVOR QUALITY AND INTUITIVE API OVER QUANTITY  

## Commands

Requires the .NET 10 SDK from `global.json`.

```sh
dotnet restore SharpDrill.slnx
dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx   # tests analyse this fixture; build it first
dotnet test SharpDrill.slnx                                           # ~3 minutes
dotnet test tests/SharpDrill.Tests --filter "FullyQualifiedName~ExtractionTests.Some_test_name"
dotnet pack SharpDrill.slnx -c Release                                # CI also packs

# Run the CLI from source on the fixture
dotnet run --project src/SharpDrill.Cli -- exceptions miscaught tests/Fixtures/ResearchSolution/ResearchSolution.slnx
```

Exit 1 on the fixture's exception views is expected (an interface without implementation and a never-awaited task are
deliberate). 0 = complete, 1 = usable but with gaps/Unknown/limits, 2 = invalid request or fatal, 130 = cancelled.

## Architecture

Pipeline: **Load → Extract facts → Index → Queries → CLI report**.

- `SharpDrill.Roslyn` — `RoslynProgramLoader` opens the solution via MSBuild (never restores/builds the target);
  `OpenAsync(path)` loads and indexes in one call. `Extraction/FactExtractor*.cs` registers source symbols and bodies
  (top-level statements are the body of `Program.<Main>$`); `BodyWalker` walks each body's operation tree and records
  call sites, throws, catches, regions and `AnalysisGap`s. All Roslyn knowledge lives here.
- `SharpDrill.Core` — plain immutable records (`Model/Facts.cs`, the `ProgramSnapshot`), `ProgramIndex` (lookups, lazy
  call/dependency graphs) and the queries. **Core has no Roslyn or Mermaid dependency.** The consumer API is extension
  methods on `ProgramIndex`: `EntryPoints()`/`EntryPoint(name)` (`Conventions/EntryPoints.cs`) and `Exceptions(options)`
  (`Exceptions/ExceptionReport.cs`), whose findings carry resolved facts. The exception engine behind it
  (`ExceptionSummaryQuery`, `ExceptionHandlingQuery`, `ExceptionTransfer`) is internal.
- `SharpDrill.Graph` — generic immutable directed multigraph (SCC, quotient); knows nothing about C#.
- `SharpDrill.Export.Mermaid` — Mermaid/Markdown formatting; the only project with the Mermaid NuGet dependency.
- `SharpDrill.Cli` — `Commands.cs` gets the snapshot: `SnapshotCache` (reused while `ProgramSnapshot.Inputs` are
  unchanged), else the loader, with `Restore` when it finds packages missing. `Arguments.cs` parses strictly per command:
  views are words (`exceptions caught`), options are flags. One `ICliCommand` per command under `Commands/`, shared
  naming in `Display.cs`. Formatting only; no analysis logic. `skill` (like `--version`) loads nothing: it writes the
  embedded `Skill/SKILL.md`, the agent-facing guide. Update it when commands, options or output change.
- `SharpDrill.Build` — no code: the `SharpDrill.MSBuild` package. `build/SharpDrill.MSBuild.targets` runs the packed CLI's
  `exceptions --format msbuild` after each build, so findings become SD000N warnings.

### Where exception behaviour is decided

One question per place; change the place that owns the question.

| Question | Place |
| --- | --- |
| Where does the program start? | `ProgramEntryPoints` (`Conventions/EntryPoints.cs`) |
| What does this call site lead to (source, library, dispatch candidates, unresolved)? | `CallGraph.Resolve` (`Graphs/CallGraph.cs`) |
| What happens to an exception at one throw or call site (catch match, filter, rethrow, finally, await)? | `ExceptionTransfer` |
| Which exceptions may leave each method and call site, through which steps? | `ExceptionSummaryQuery`: Reach → Seed → Propagate → Summarize into `ExceptionSummaryResult` |
| Which catches a throw meets or misses, and where it ends? | `ExceptionHandlingQuery`, from the recorded transfer steps |
| What is one finding (grouping, global handlers, example choice, test-double hiding)? | `ExceptionReport` |
| How is it worded and laid out? | `Cli/Commands/ExceptionsCommand*.cs`: views, `.Layout` (blocks, paths), `.MSBuild` (warnings) |

## Principles that shape changes

- **Declared vs candidate vs runtime.** A call site records its declared target. Implementations/overrides are separate
  `ImplementationFact` candidates; following them is the default and every such step is marked as a possibility. Never
  claim a runtime target (DI registrations are not read).
- **Unknown must be rare and actionable.** Before adding one, decide whether it is out of scope (a note, not Unknown),
  a choice not to look (visible and reversible), or a real "cannot decide". Library/framework calls are the boundary of
  the claim, not Unknown. Compiler-provided members are `BodyStatus.Synthesized`, not missing. If an agent reading the
  line could not act on it, it is noise.
- **State the claim, then make code and docs agree.** If `docs/coverage.md` says "not modelled", the code must not
  report it as Unknown; when they disagree, fix one of them.
- **Defaults fit the typical program.** A default that returns nothing for the common case (controller → `IService` →
  implementation) is a bug even when it is "conservative"; keep honesty in the marking, not in the refusal.
- **A limit never changes what an empty result means.** Budgets are reported in words with what to raise.
- **Options change what an answer means, not how much of it you get.** If a number can be derived, derive it.
- **Judge output by signal-to-caveat ratio.** Ask of every line: what would an agent do with this? Keep a realistic
  sample next to the contrived fixture and run new analysis on real code before shipping.
- **Evidence and determinism.** Results carry `SourceEvidence`; output is ordinal-sorted and byte-identical across runs.
  Regenerate `docs/samples/` (commands in `docs/samples/README.md`) when CLI output changes.

## Tests

All in `tests/SharpDrill.Tests` (xUnit; `InternalsVisibleTo` exposes Core and Roslyn internals). Ignore the stray
`tests/Sharpdrill.Tests` directory (build output only).

- `ExtractionTests` compile inline C# in an `AdhocWorkspace` and run `FactExtractor` directly (`Extract(..., output:
  OutputKind.ConsoleApplication)` for top-level statements) — the place for extraction and gap behaviour.
- `ExceptionReportTests` cover the consumer API (entry points, report) on inline programs.
- `CoreTests`, `ArchitectureTests`, `GraphTests`, etc. build `ProgramSnapshot`s by hand.
- `CliTests`, `CliContractTests`, `DiagramCliTests`, `ResearchTests` run against the ResearchSolution fixture and assert
  exact output strings and counts; changes to extraction or formatting usually require updating these.

## Code style

Dense, expression-heavy C# with file-scoped namespaces, primary constructors, records and collection expressions; long
lines are normal. Comments are short and explain *why*. Match this density rather than expanding code.
