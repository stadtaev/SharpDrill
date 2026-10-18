# Graph correctness, exception summaries, and Mermaid export

User-authorized scope: address the graph review findings, provide a complete controller-action Mermaid export command,
document changes and limitations, and commit with a reference to that change document.

## Implementation decisions

- Keep the immutable directed multigraph and semantic CallGraph. Explicit call-site identity and original evidence
  remain authoritative; diagrams and grouped graphs are projections.
- Correct phased search and depth-bounded reachability. Preserve a preference for direct witnesses on equal-distance ties,
  but never let a longer preferred witness prevent discovering nodes reachable within the configured depth.
- Retain parent links internally; materialize full reach witnesses on request.
- Quotients retain all groups, node membership and supporting edges. Document materialized filtering costs and
  require stable node identities and immutable edge endpoints.
- Implement conservative bottom-up exception summaries with finite fact identities, monotone joins, SCC processing,
  call-site transfer (catch/rethrow/replacement/await/unknown), bounded work, and provenance. Keep the detailed witness
  query available: summaries do not promise all paths or proof of runtime unhandled exceptions.
- `diagram <input>` lists controller actions in deterministic order and accepts a number (or q to cancel). Explicit
  `--root`/`--root-id` selects an action without input. EOF exits cleanly. Default output is a new Markdown file in
  `Path.Combine(AppContext.BaseDirectory, "diagrams_exports")`; `--output` can select an explicit new file.
- Diagram defaults: 20 transitions, 200 method nodes, 1000 edges, bounded call-site work. Show project groups,
  declared dispatch boundaries, optional marked candidates, unresolved/missing-body boundaries, recursion/shared
  nodes, source evidence, and visible limits. Never enumerate every path to construct a diagram. Print the absolute output path after a successful write (explicit user requirement).
- Mermaid IDs are generated from stable ordered identities, independently of escaped labels. Export as Markdown
  containing a Mermaid flowchart and evidence/coverage information. No overwrites.

## Tasks and ownership

1. Graph/Search: `src/Sharpview.Graph`, `Core/Graphs`, `ProgramIndex`, `ReachabilityQuery`, graph/reach/architecture tests.
   Regressions: mixed-depth candidate seeds; descendants through shorter candidate routes; late BFS sources;
   isolated quotient nodes; parallel edge IDs; budget/depth completeness; lazy witnesses; cancellation.
2. Exceptions: `ExceptionQuery`, new summary/transfer files, exception summary tests. Verify ordinary and recursive
   propagation, different catches at separate sites, replacement/rethrow, known facts beside unknowns, task
   observation, work limits, and reconstructed provenance. Do not remove detailed query behaviour.
3. Diagrams/CLI: new Core projection and Markdown exporter, CLI parsing/selection/output, integration tests.
   Verify branches/shared nodes/cycles, escaped labels, project grouping, dispatch/unknown boundaries, small
   limits, root selection, interactive invalid input/EOF/q, default directory, custom output, no overwrite.
4. Integrate, run relevant tests then full repository suite, parse generated Mermaid with an actual Mermaid parser,
   review changes, update README/coverage/todo and `docs/changes/2026-10-18-graph-analysis-and-mermaid.md`.
5. Commit the reviewed changes with the change-document path in the commit body. No push requested.

## Validation

Use the existing xUnit/VSTest test project and .NET 10 SDK. Test regressions before their fixes. Full final validation:
`dotnet test sharpview.slnx --no-restore`, plus a fresh-process diagram export on ResearchSolution and Mermaid syntax
validation. Record exact results, limits, API decisions and deferred external validation in the change document.
