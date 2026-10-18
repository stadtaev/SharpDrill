# Todo

## Analytical graph for agents and visualisation

The immutable method graph is the common source for queries and projections. The initial graph corrections, exception
summary engine and action Mermaid export are implemented; see [the detailed change document](changes/2026-10-18-graph-analysis-and-mermaid.md).

### Placement and semantics

| Information | Depends on | Location |
| --- | --- | --- |
| Project/type/signature/declaration | snapshot and stable identity | callable facts/index |
| Dispatch, call-site evidence, awaited flag, protected regions | call site | call-edge site fact |
| Local explicit origins | source body | throw facts |
| Possible escaping effects and uncertainty | snapshot, analysis policy and callees | immutable method summaries beside the graph |
| Effects crossing a particular call | callee effects and that site's transfer | crossing summaries keyed by call-site ID |
| SCC membership, fan-in/out, architectural mappings | snapshot and configuration | derived views/overlays |
| Root distance, candidate provenance and witness | root and traversal policy | query result |

A catch protects a call site. Transfer is not simple type-set subtraction: matching can be uncertain; filters/finally can
change flow; rethrow/replacement changes provenance; task observation changes propagation. The supported transfer code is
shared by detailed and summary analysis. Computed summaries are scoped to a snapshot and options, not mutable global truth.

### Implemented

- Directed multigraph with stable semantic call-edge IDs, adjacency in both directions, SCCs and quotient membership.
- Correct distance improvement across phased seeds and late sources. Work truncation preserves coherent admitted paths.
- Lazy direct-only and candidate-inclusive reach witnesses/distances, per-root truncation and explicit uncertainties.
- Finite origin-preserving exception effects, SCC scheduling, monotone fixed points, per-site transfer and bounded work/facts.
  Recursion alone is not unknown. Known effects coexist with uncertainty; one example witness is reconstructed on request.
- `diagram` numbered action selection or `--root`/`--root-id`, bounded graph projection, project groups and Mermaid Markdown.
  The command prints the absolute saved path. Raw full-program rendering is deliberately not the default.

### Next increments

- Typed reusable attribute-layer container if a second independent analysis demonstrates a useful common interface.
  Current exception summaries already provide typed immutable dictionaries; avoid an untyped attribute bag.
- Agent node-link JSON with schema/version, stable node/edge IDs, memberships and provenance, followed by NetworkX import
  verification. Treat exports as projections; never re-import them as extraction facts.
- Additional DOT/project/type/layer diagrams. Keep supporting edges and membership so aggregation is reversible for
  explanations. Group collapse can introduce apparent paths; analyze exceptions on the method graph.
- Evaluate conservative uncertainty categories so some unknown throws can safely be intercepted at catch-all sites.
  Unsupported control flow or extraction gaps cannot generally be removed this way.
- Performance measurement on representative large solutions. Costs depend on effects, call sites and fixed-point work;
  do not claim summary propagation is linear solely in graph size. Graph preparation currently materializes all candidate
  edges even when a particular query does not follow candidates.
- Consider demand-driven candidate adjacency and cancellation-aware generic graph materialization for very large inputs.

## Other open items

- Move detailed `CallChainQuery` / `ExceptionQuery` traversal onto shared graph operations if it reduces duplication while
  retaining call-site/path-sensitive reporting. Their bounded enumeration is still useful for detailed explanations.
- Event types are not dependency sources yet (documented in coverage).
- Validate against an external Web API with a working restore configuration. The earlier API archive's configured package
  feed returned HTTP 404; repository fixtures and SharpView remain the available validation inputs.
- Extend extraction ownership for top-level/initializer callables, minimal APIs and inherited action enumeration.
