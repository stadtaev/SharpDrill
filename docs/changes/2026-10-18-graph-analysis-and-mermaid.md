# Graph correctness, exception summaries and Mermaid exports

Date: 2026-10-18. This document describes the implementation and its compatibility, validation and analysis limits.
The implementation commit references this path in its body.

## User-visible behavior

`diagram <restored project/solution>` lists controller actions in a deterministic numbered order. Enter a number to export
one action's reachable call relationships, or `q` to cancel. Invalid input is retried; EOF cancels without saving. Exact
`--root` and `--root-id` options select a recognized controller action without prompting. Missing/ambiguous roots fail clearly.

The default output directory is `Path.Combine(AppContext.BaseDirectory, "diagrams_exports")`, beside the running DLL/published
binary, irrespective of the terminal's current directory. Filenames contain a sanitized action slug, UTC timestamp and GUID.
`--output <new path>` selects an explicit file; its parent directory must exist. `FileMode.CreateNew` prevents overwrite races.
The command prints **`Saved diagram: /full/absolute/path.md` only after the write succeeds and the stream closes**. A failed
write removes the newly created partial file. EOF/quit/no recognized actions returns 0 with no artifact; an incomplete exported
graph returns 1 while still reporting the saved path.

The Markdown includes a Mermaid top-to-bottom flowchart, groups keyed by compilation identity and labeled by project name,
numbered edge evidence, dispatch/body/unresolved boundaries, relevant extraction gaps and visible limits. Source-controlled
architectural labels are unnecessary. Multiple projects with the same display name remain separate groups. Shared callees
appear once; branches, recursion and distinct call sites to the same method remain edges. Solid arrows mean direct calls;
dotted labeled arrows distinguish declared dispatch, possible implementation candidates and unresolved calls. Candidate
traversal requires `--follow-candidates`; opting in never claims a resolved runtime receiver or removes dispatch uncertainty.

Defaults: 20 call transitions, 200 displayed nodes (including unresolved boundary nodes), 1,000 displayed edges and 100,000
examined call sites. Flags: `--max-depth`, `--max-nodes`, `--max-edges`, `--max-expansions`. Stop markers name the limiting resource.
A bounded graph projection avoids enumerating every path. These limits cover the projection after loading/indexing and
whole-program call-graph construction; they are not process-wide memory/time limits.

`exceptions --summary` adds bottom-up possible-escape summaries. It shows distinct origins, exception type, changes through
handlers, one example path, source evidence, separate Unknown reasons and middleware registration evidence. CLI budgets use
`--max-expansions` (100,000 summary work units) and `--max-facts` (100,000 retained method effects/uncertainties). Library defaults
are 1,000,000 work units and 100,000 facts. Path-depth, chain-count and dispatch options do not apply to summary mode and are
rejected explicitly. Existing detailed `exceptions` remains available to report intercepted origins and individual paths.

## Graph defects and corrections

### Phased search and late sources

The original search only inserted previously unseen nodes. Mixed-depth candidate seeds could record `Y` at distance 4 before
finding a distance-2 route, preventing expansion to `Z` within a depth-4 limit. A shorter candidate route to a method already
reached by direct calls had the same defect.

`BreadthFirstSearch.Relax` now accepts strict distance improvements and queues affected nodes again. Late `AddSource` calls
can improve a discovered node to distance 0. Queue entries carry distance versions, so stale entries are skipped. Equal-length
paths retain the first witness. Relaxation validates both endpoints and checks cancellation.

Review exposed a second defect under budget exhaustion: changing an ancestor's parent immediately rewrote a descendant's
lazy path while its recorded distance remained old. Parent entries are now immutable path versions. A descendant retains its
admitted path until it is relaxed; distance and witness length agree even if the budget stops before reopening finishes.
`FoldPaths` computes path attributes once per retained parent version without allocating full path arrays. Complete searches
provide shortest paths; work-limited searches provide coherent admitted paths whose optimality is not promised. Phased
improvements may revisit nodes/edges, so their worst-case cost is not the single-pass BFS bound.

### Reachability semantics and storage

Direct-only traversal establishes bounded direct reachability first. Candidate relaxation then improves shortest allowed
routes and reopens descendants. `ViaCandidate` means a bounded direct-only route was not established; `WitnessUsesCandidate`
describes the chosen witness itself. A callable may have a longer direct route and a shorter candidate witness. Candidate
steps are labeled individually in text output, including one-transition shortcuts. This clarifies the former behavior, which
preferred a longer direct witness and could consequently omit reachable descendants.

Each reached callable holds a lazy witness backed by shared immutable parent entries. It no longer eagerly copies every
root-to-node path (quadratic retained arrays on a long chain). Witness materialization costs the length of the requested path.
A consumer that requests every witness on a long chain still requests quadratic total output; the library cannot avoid the
size of that requested output.

Site work includes unresolved sites individually in deterministic outgoing order. A bulk charge for unresolved sites can no
longer suppress an earlier known call. Parallel candidate edges do not charge the same call site repeatedly. Boundaries use
pregrouped site edges and final discovery state, avoiding duplicate emissions during reopen and repeated full edge rescans.
Candidate counts track edges actually processed, including zero if a direct-phase budget stop prevented candidate traversal.
`RootReach.WorkLimitReached` identifies the truncated root in addition to the global flag. Cycle-only depth boundaries are
suppressed when relevant direct callees are already reached; unresolved and dispatch uncertainty stays visible.

### Quotients, identities and graph preparation

Quotients now retain all assigned groups and original members, including isolated nodes, plus unassigned nodes. Supporting
parallel edges and unassigned edges remain available. The new four-type-parameter quotient result derives from the existing
three-parameter result, preserving its `Edges`/`Unassigned` access. Quotient graphs are aggregation/presentation structures:
group collapse may introduce apparent paths, so exception analysis operates at method/call-site granularity.

`CallEdge.Id` is a stable, unambiguous length-prefixed combination of call-site ID, kind and target ID. Separate calls and
candidate relationships therefore have separate identities for future overlays/export schemas. Dispatch implementation
closures are memoized per declared target during a graph build rather than recomputed at every call site.

`ProgramIndex.GetCallGraph(token)` and `GetDependencyGraph(token)` use cancellation-aware gates and publish only complete,
uncancelled results. Tokenless properties delegate to these APIs. Concurrent callers share the published graph; a cancelled
build does not poison the cache. Closure expansion and iterative dependency type-use flattening check cancellation. Layer
aggregation and diagram/reach callers pass their tokens through to graph construction.

`DiGraph` structure is immutable, but generic payloads are shared, not cloned/frozen. Node equality/hash codes and edge
endpoints must remain stable. `Where` and `Subgraph` materialize graphs and adjacency; they are not zero-copy filtered views.
Whole-program candidate adjacency remains materialized even for queries that do not follow candidates. A future demand-driven
representation requires workload measurements and should retain call-site identity and deterministic order.

## Exception processing

Detailed and summary analysis share `ExceptionTransfer`, including tri-state static/exact type matching, protected-region
order, catch filters, finally uncertainty, handler outcomes, rethrows/replacements, async restrictions and awaited-task
observation. The transfer is not a set subtraction of caught types. Detailed mode retains structurally shared evidence/handler lists and materializes arrays at the final witness; summary
mode disables accumulated path arrays and uses provenance links instead.

Summary analysis computes reachable direct-call components and processes SCCs in callee-before-caller order. Each cyclic
component reaches a finite monotone fixed point before callers are processed. Facts preserve original throw identity and
subsequent replacement/rethrow identity/type state. Known escape effects and uncertainty are separate sets. Recursion alone
does not produce an Unknown: a closed recursive component with no explicit origins has no explicit escape effects within
this model. Same-type origins stay distinct, and catches are transferred separately at each call site.

Each retained effect has at most one first-discovered provenance link to a previously admitted fact. No path enumeration or
full path array is retained per summary. `GetWitness(root, origin)` returns one deterministic example; the overload accepting
the full escaping fact distinguishes different transformations of the same origin. Provenance links are acyclic even when
the method graph is recursive. They explain one supporting route and do not represent every possible path.

`ExceptionSummaryResult` exposes immutable method/root summaries and crossing summaries keyed by call-site ID, along with
work/fact counters and limit flags. Missing bodies, unresolved/uncertain dispatch, unbound types and extraction gaps remain
Unknown with machine-readable reasons and evidence. Ownerless extraction gaps are conservatively attached to selected roots.
On budget exhaustion, method/crossing results are marked incomplete rather than presenting absent facts as a complete empty
set. The fact cap counts unique method effects and uncertainties; crossing effects are bounded by charged transfer work. Work accounts for site/origin processing and crossing transfers; indexing, SCC preparation and result materialization
have their own graph-sized cost. Fixed-point cost also depends on distinct effects and transfers; it is not claimed to be
linear solely in nodes and edges.

## Safety of the Markdown projection

Generated graph/node/group IDs are independent of source labels. Mermaid labels use entity codes for syntax/HTML/fence
characters and flatten control characters. Markdown evidence escapes HTML, pipe and formatting delimiters, preventing source
names or paths from injecting another Mermaid block. No JavaScript or Mermaid runtime is added to the shipped .NET projects.
The official Mermaid parser is used externally for validation of generated artifacts. A real fixture export is included at
[`docs/samples/diagram.md`](../samples/diagram.md).

## Validation evidence

The repository uses .NET 10, xUnit and VSTest. Tests were added for the observed regressions and feature behaviors. Focused
checks precede the final solution run. Exact final commands, counts and parser version are recorded below after verification.

| Requirement | Regression / contract evidence |
| --- | --- |
| Correct phased seeds and late sources | `Phased_search_reopens_nodes_when_a_shorter_seed_arrives`; `Late_source_improves_previously_expanded_descendants_and_validates_relax_endpoints` |
| Budget-consistent distance/witness and truthful candidate counts | `Budget_stop_preserves_distance_and_witness_consistency_after_a_late_source`; `Candidate_budget_stop_keeps_witnesses_consistent_and_reports_only_followed_candidates` |
| Candidate shortcuts through direct-reachable nodes | `Shorter_candidate_route_reopens_a_direct_reachable_method_at_the_depth_limit` |
| Lazy witness storage | `Long_chain_keeps_parent_links_without_eager_quadratic_witness_arrays` (5,000 transitions, allocation ceiling before requesting witnesses) |
| Isolated quotient groups and parallel edges | `Quotient_keeps_isolated_groups_memberships_and_parallel_supporting_edges` |
| Cache publication/cancellation and stable semantic IDs | `Graph_caches_honor_cancellation_and_publish_one_complete_instance`; `Call_edge_ids_distinguish_site_kind_and_target_and_are_stable` |
| Call-site-sensitive exception propagation | `Exception_summary_transfers_each_diamond_call_site_independently` |
| Recursive summaries and provenance | `Exception_summary_closed_recursion_has_no_escape_or_cycle_unknown`; `Exception_summary_preserves_origins_across_recursive_calls_and_reconstructs_acyclic_witness` |
| Summary budgets, cancellation and gap evidence | `Exception_summary_work_and_fact_budgets_mark_all_roots_incomplete`; `Exception_summary_budget_marks_crossing_effects_incomplete`; `Exception_summary_honors_cancellation_and_rejects_invalid_budgets`; `Exception_summary_ownerless_gap_marks_root_unknown` |
| Compact summary provenance | `Exception_summary_long_chain_keeps_provenance_compact_until_witness_requested` |
| Known effects alongside unknown | `Exception_summary_keeps_known_escape_beside_unknown_boundary` |
| Replacement/rethrow and task observation | `Exception_summary_preserves_origin_and_applies_handler_transformation_per_site`; `Exception_summary_requires_await_for_task_exception_to_reach_caller_catch` |
| Graph projection preserves branches/shared/cycles/sites | `Projection_preserves_shared_nodes_cycles_and_parallel_call_sites` |
| Dispatch and missing-body boundaries | `Dispatch_unresolved_and_missing_bodies_remain_visible` |
| Diagram budgets, escaping, cancellation | `Limits_are_bounded_and_visible`; `Escapes_source_labels_and_rejects_invalid_roots_options_and_cancellation` |
| Interactive output and absolute default path | `Picker_retries_invalid_input_saves_beside_binary_and_prints_absolute_path` |
| EOF/quit produces no file | `Eof_or_quit_cancels_without_creating_output` |
| Scripted signature/ID and candidate export | `Explicit_root_exports_project_groups_evidence_and_limits_without_prompt_and_refuses_overwrite`; `Explicit_action_id_selects_without_prompt_and_preserves_dispatch_when_candidates_are_absent` |
| Invalid options and overwrite refusal | `Invalid_diagram_options_fail_before_loading`; `Diagram_requires_controller_action_and_size_options_are_diagram_only`; `Explicit_root_exports_project_groups_evidence_and_limits_without_prompt_and_refuses_overwrite` |
| Summary CLI output and budgets | `Summary_mode_reports_known_escapes_alongside_unknowns_and_limits`; `Invalid_summary_options_fail_before_loading` |

Final verification completed:

- `dotnet test sharpview.slnx --no-restore --logger 'trx;LogFileName=full-suite.trx' --results-directory /tmp/sharpview-test-results`
  passed **192/192**, with **0 failures and 0 skipped** (1 minute 58 seconds). The TRX counters were inspected independently:
  total/executed/passed 192, failed/error/timeout/aborted/not-executed 0. This command built all projects successfully.
- Earlier focused graph/diagram checks passed 45 tests, then 33 graph/reach/CLI tests with TRX output. Focused Core exception
  checks passed 48 tests. A subsequent focused integration run exposed a test assumption that the fixture's `IRepository`
  had an implementation; the fixture has none. The test now verifies that no candidate edge is invented, while synthetic
  Core tests cover actual candidate edges. The final 192-test run includes the corrected test and all added cases.
- Official Mermaid parsers **11.17.2** and **12.2.0** accepted the generated direct fixture, declared-interface, recursive,
  special-character label and real-candidate synthetic exports. Node/parser packages were installed only under
  `/tmp/sharpview-mermaid-validation`; no JavaScript dependency was added to the repository.
- Fresh-process CLI tests verify the default binary-relative directory, numeric picker, EOF/quit, exact action signature
  and ID selection, full absolute saved path, output limits, summary CLI and refusal to overwrite.
- `git diff --check` passed. Independent graph, diagram and final architecture reviews found no remaining actionable
  correctness/specification issues after the two budget-reporting fixes described above.

Internal decisions recorded during implementation: use the visible shared checkout on a new feature branch with one final
commit; parent owns diagrams/CLI while a worker owns disjoint graph/exception files; report shortest candidate-bearing
witnesses separately from direct-only classification; retain conservative uncertainty across unsupported transfers. The
first two affect coordination/isolation and were checked for unrelated changes before staging. The latter two may affect
consumers expecting direct-preferred witnesses or fewer Unknowns, and their public contracts are described above.

## Remaining model boundaries

- Possible escape under an explicit-source/direct-call model is not proof of a runtime unhandled exception. Middleware
  registrations are evidence only; runtime ordering/coverage is not inferred.
- Unknown effects propagate conservatively through callers, including catches that cannot prove interception of unsupported
  control flow or extraction gaps. Partial uncertainty does not erase known effects.
- Implicit exceptions, path feasibility, receiver/DI resolution, async void/custom awaitables, aliased/unobserved tasks,
  filters/finally and unsupported handler flow remain bounded by the documented extractor/transfer coverage.
- Minimal APIs, inherited action enumeration and top-level/initializer callable ownership remain extraction limitations.
  A call diagram shows database behavior only when corresponding calls/bodies are extracted; it does not invent a database tier.
- Full-program drawings, DOT, reusable generic attribute bags and NetworkX node-link export are future increments, not implied
  by the Mermaid feature. No new graph library or runtime database is necessary for the current in-memory analysis.
- External Web API validation is still unavailable without a working package feed/restore. The earlier archive's configured
  feed returned HTTP 404. Repository fixture and Sharpview inputs are used here; external validation is not claimed.

## Follow-up: use a maintained Mermaid generator

At the user's request, handwritten flowchart serialization is replaced by **Mermaid.NET.Flowcharts 6.0.0**, centrally pinned
in `Directory.Packages.props` and referenced by `Sharpview.Core`. The package models nodes, project subgraphs, link kinds and
root styles, and owns Mermaid formatting and standard text escaping. Graph projection, stable IDs, edge evidence, boundaries,
limits, selection, filenames and Markdown reporting remain Sharpview responsibilities.

The package was selected after checking its source, tests, license, CI and releases on 2026-10-18:

- [Source and usage](https://github.com/JansthcirlU/Mermaid.NET.Flowcharts): MIT, typed flowchart API, subgraphs and styles,
  distinct parallel links, no runtime package dependencies. Its CI builds/tests .NET 8, 9 and 10.
- [Release 6.0.0](https://www.nuget.org/packages/Mermaid.NET.Flowcharts/6.0.0) was published in August 2026. It is actively
  maintained as a small community project, not an official Mermaid SDK or a vendor-supported component.
- `FoggyBalrog.MermaidDotNet` has promising parser integration tests but explicitly states it is not production-ready.
  The other `MermaidDotNet` implementation was also inspected; the selected package offers the focused typed subgraph/text
  API needed by this exporter.

A private `MarkdownSafeText` adapter delegates ordinary escaping to the package and additionally flattens controls/Unicode
line separators, encodes backticks so labels cannot close the Markdown fence, and preserves blank labels that the library's
nonempty text factory rejects. Markdown evidence/heading escaping stays separate. This removes the handwritten Mermaid
node/subgraph/arrow/style templates and the custom general Mermaid label escaper. No rendering engine, Node or browser
runtime is introduced to the application.

Generated syntax is semantically equivalent but not byte-identical: the package uses `--->` for ordinary arrows, a space
before subgraph labels, HTML named entities for some escaped characters, and its own whitespace formatting. These are
validated against the official Mermaid parsers. The checked-in diagram sample is regenerated from the package output.

Validation for this follow-up:

- `dotnet restore sharpview.slnx` succeeded; the resolved package targets .NET 10 and has no transitive runtime dependencies.
- Updated text/escaping expectations were first run against the old formatter and failed as expected; the package-backed
  implementation then passed all 8 focused diagram tests before blank-label coverage was added.
- `dotnet test sharpview.slnx --no-restore --logger 'trx;LogFileName=mermaid-package.trx' --results-directory /tmp/sharpview-test-results`
  passed **195/195**, with **0 failures and 0 skipped**, in 1 minute 11 seconds. TRX counters were inspected. This includes
  existing fresh-process diagram/CLI integration tests and the new
  `Library_generation_preserves_edge_identity_root_style_and_markdown_safe_labels` and
  `Library_generation_retains_blank_display_labels` cases.
- Official Mermaid **11.17.2** and **12.2.0** accepted package-generated fixture, special-character and actual-candidate
  diagrams. Additional parser database checks verified node/edge counts (4/3, 1/0 and 3/2 respectively), including the
  candidate label. Parser validation remains an external verification step, not a repository .NET integration test.
- `git diff --check` passed. The sample Markdown was regenerated from an actual fresh-process CLI export; the command still
  prints its absolute saved path. User-facing selection, limits, evidence and overwrite behavior remain covered by the suite.

## Follow-up: extract the export adapter without DI

The Mermaid/Markdown formatter moves to a dedicated **`Sharpview.Export.Mermaid`** project. The NuGet reference moves with it;
`Sharpview.Core` now depends only on `Sharpview.Graph`. Core retains the format-independent `CallDiagram` records,
`DiagramOptions` and `CallDiagramQuery` analysis/projection. CLI and tests explicitly reference the new exporter project, which
is included in `sharpview.slnx`.

The static entry point is now `Sharpview.Export.Mermaid.MermaidExporter.Markdown(CallDiagram)`. Source consumers must reference
the new project and import `Sharpview.Export.Mermaid`; the README example is updated. The exporter implementation is moved
unchanged apart from its namespace and Core import, so formatting, escaping, evidence, limits, file selection and absolute-path
reporting retain their prior behavior. The call remains static: no DI container, exporter interface, registration extension,
hosting package or new service abstraction is introduced.

Dependency direction:

```text
Sharpview.Cli -> Sharpview.Core -> Sharpview.Graph
Sharpview.Cli -> Sharpview.Export.Mermaid -> Sharpview.Core
Sharpview.Export.Mermaid -> Mermaid.NET.Flowcharts
```

Validation for the extraction:

- `dotnet build sharpview.slnx` succeeded with **0 warnings and 0 errors**, restoring/building the new project as part of the solution.
- `dotnet test sharpview.slnx --no-build --no-restore --logger 'trx;LogFileName=export-extraction.trx' --results-directory /tmp/sharpview-test-results`
  passed the same **195/195** tests, with **0 failures and 0 skipped**, in 1 minute 13 seconds. TRX counters were inspected.
  Existing exporter tests and fresh-process CLI integration tests now reference/use the new assembly.
- A source comparison verified that exporter code differs from its previous implementation only by namespace and the Core
  import. No new behavior tests were necessary for this mechanical extraction.
- Core's restored assets contain only the `Sharpview.Graph` project dependency, confirming that the Mermaid package no longer
  appears in Core's dependency closure. All CLI/test source references and README usage imports were updated.
- `git diff --check` passed. No DI or service-registration dependency was added.
