# Todo

## Analytical graph for agents and visualisation

**Idea.** Enrich the whole-program graph with analytical information, such as the exceptions a call can throw and the dependencies of each element. The enriched graph is then the shared source for agentic analysis and for visualisations.

### Design rule: context-free facts on the graph, context-dependent results beside it

| Information | Depends on | Where it belongs |
| --- | --- | --- |
| Project, layer, type, signature, location, kind | nothing | node attribute |
| Exceptions thrown locally in a method | nothing | node attribute |
| Exceptions a method may let escape (summary) | the method and its callees, not the caller | node attribute (computed) |
| Dispatch kind, call-site location, awaited, protected `try` regions | the call site | edge attribute |
| Exceptions crossing a specific call | callee summary minus what that site's catches intercept | edge attribute |
| Dependencies, fan-in/out, cycle membership (SCC) | nothing | node attribute |
| Distance from an action, "escapes action X", witness path | a root and a path | not on the graph: overlays keyed by root, or query results |

**Why the split matters.** A `catch` protects a call site, not a method, so one method can be intercepted on one path and escape on another. Storing "exception status" on a node would be wrong. Storing the escape summary on the node and the per-site filtering on the edge is exact.

### Plan

1. **Attribute layers, not graph mutation.** Keep `DiGraph` generic and immutable. Add typed attribute layers, the equivalent of NetworkX node/edge attribute dicts, for example `NodeAttributes<CallableId, ExceptionSummary>` and `EdgeAttributes<CallEdge, CrossingExceptions>`. Each analysis adds a layer independently. Every value carries evidence and uncertainty (`Unknown` plus a reason code).
2. **Exception summaries bottom-up.**
   - **Order:** `StronglyConnectedComponents` already returns components in reverse topological order, so callees are processed before callers.
   - **Cycles:** a small fixpoint inside each cyclic component; Unknown is sticky.
   - **Performance:** linear in graph size, replacing the exponential path enumeration in `ExceptionQuery`.
   - **Witnesses:** store one "came from" edge per (method, exception), like BFS parent pointers, so a witness path can be rebuilt on demand.
3. **Export with levels.**
   - **Agents:** node-link JSON with all attribute layers, loadable directly with `networkx.node_link_graph`.
   - **Visualisation:** quotient views (project, layer, type) with aggregated attributes, e.g. "exceptions escaping Infrastructure into Application", plus per-action slices via `ReachableFrom`/`Subgraph`. Candidate formats: DOT, Mermaid.

The suggested first increment is step 2 (exception summaries). It is the most valuable attribute, it replaces the exponential path enumeration, and it proves the attribute-layer design.

### Concerns

- **Summaries answer "can", witnesses answer "how".** Agents usually need both, so keep the "came from" pointers. They cost little memory.
- **Uncertainty compounds upward.** One Unknown deep in the graph marks every caller's summary as partially Unknown. That is honest, but reports must say *why* (reason codes and a source pointer), or agents will over-trust or over-discount results.
- **Size is fine for analysis, not for raw rendering.** A 100k-edge graph is small in memory, but call-level drawings become unreadable beyond a few hundred nodes. Always quotient or slice before visualising.
- **Do not create a second source of truth.** Attributes are derived from the snapshot facts. Exports are views, never re-imported as facts.
- **Do not store root-dependent results on nodes.** Distances, per-action statuses and witness paths stay overlays or query results.

## Other open items

- Move `CallChainQuery` (`chains`, `controllers`) and `ExceptionQuery` onto `CallGraph`. They still use their own depth-first searches with per-path state.
- `reach`:
  - add a per-root truncation flag when the work budget stops mid-root (today only the global flag shows it);
  - skip the depth-limit boundary when every callee past the limit is already reached.
- Event types are not dependency sources yet (documented in coverage).
- Validate on an external Web API; so far only the repository fixture and Sharpview itself.
