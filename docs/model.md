# Model reference

Detailed semantics of facts, identities and queries. For the big picture start with [How Sharpview works](overview.md).

Core records contain immutable arrays, declaration IDs, typed uses and source evidence. Compilation identity contains project path, configuration and selected framework. Method identity uses assembly identity and canonical documentation identity, normalising generic definitions, reduced extensions and partial methods. Local/lambda identities include source position; arbitrary-edit stability is not promised. Duplicate display signatures retain distinct IDs.

Source evidence stores the full file path, SHA-256 of the decoded UTF-8 source text, zero-based span start/length and one-based line/column. Generated source has an explicit flag. A checksum is evidence for the analysed document, not a promise that a file has not changed later.

Call sites retain owners, declared bindings, dispatch, type arguments, implicit-site evidence, ordered enclosing protected-region IDs and direct-await observation. No inferred runtime implementation is stored. Display traversal may collapse repeated relationships while retaining all supporting site IDs; unresolved sites remain distinct. Constructors and accessors are executable callables. Local/lambda bodies have their own ownership; declaration does not invent an invocation.

Throw sites retain explicit/rethrow kind, static exception type, a separate `ExactExceptionType` for known construction, catch ownership and enclosing protected regions. For `throw (Exception)new StorageException()`, the static type remains `Exception` and the construction type is `StorageException`. Catch records retain caught type, filter text, body evidence, conservative outcome and subsequent throw ID. A region stores protected block evidence, ordered handler IDs and finally uncertainty. These are small lexical facts, not a second AST.

Exception analysis is a structural may-analysis. It walks each action's supported direct calls with bounded path-local cycle detection, then unwinds each explicit origin through its own regions and its caller sites. A catch applies only to a region containing the relevant throw or call. Matching uses type identity and ancestry; a nonexact base-typed thrown value can have uncertain narrow-handler matching. Handler-owned explicit origins are retained separately as Unknown because handler reachability depends on other exceptions.

Empty handlers and bare return/empty statement handlers intercept within the model; interception does not imply recovery. A single unconditional throw/rethrow continues with replacement/original exception respectively. After a changed exception, `Status` remains `RethrownOrReplaced` and `SubsequentStatus` records later interception, possible action escape, or uncertainty. Witness evidence retains the original throw, handlers, replacement/rethrow and call sites.

Queries return IDs and structured endings; CLI formatting adds arrows and descriptions. The exception result budget bounds all witnesses, including Unknown boundaries. Work counts examined call sites; call-path relationship collapse still charges all supporting evidence. The depth limit counts transitions. Budget limits return explicit flags even when a partially explored path cannot be emitted.

JSON exports a schema-version-3 envelope (all IDs as plain strings), producer version and the full snapshot. Absolute paths and local identities make snapshot IDs local research evidence; use `Key` for portable identity.

## Consumer model

**Facts for rules.** Types record namespace, kind (class, record, struct, interface, enum, delegate), modifiers (abstract, sealed, static, generic), base class and containing type. Callables record modifiers (static, abstract, virtual, override, sealed, generic, extension, operator, explicit implementation, finalizer) and the directly overridden member. Attributes keep their rendered constructor and named arguments. Field and property types are `Field`/`Property` dependencies of their owning type.

**Implementation candidates.** `ImplementationFact` pairs a declared interface or overridden member with a source implementation, one override step per fact. Candidates are not inferred runtime targets: receiver type, DI registration and further overrides decide what runs. Queries follow them only when a consumer opts in, and mark every step that does.

**Identity.** `CallableId`/`TypeId` stay local (they contain project paths and assembly versions). `Key` is portable: `<project>|<documentation ID>`, or for lambdas and local functions the nearest documented member plus name/ordinal and project-relative position. Lambdas and local functions have position-based IDs so same-shaped bodies in one type never share a fact. Caveats: when two projects share a file name (or two versions of an assembly are referenced) later keys get `#2`, `#3` suffixes in ID order, which can shift when projects are added; lambdas and local functions in source-generated files carry the generator output path, which may include configuration/framework folders when `EmitCompilerGeneratedFiles` is on. `SourceRoot` with `SourceEvidence.RelativeTo` yields repository-relative paths. All IDs, including `ThrowSiteId`, `CatchId` and `ExceptionRegionId`, are typed.

**Index.** `ProgramIndex` is built once per snapshot and gives ordered lookups: callables, types, projects, outgoing/incoming call sites, throws, catches, regions, gaps, members, ancestors and implementation candidates in both directions. Queries take it directly; snapshot overloads remain.

**Conventions.** Controller actions are classified in Core (`AspNetConventions`) from facts, so the snapshot does not grow one array per framework concept. Boundary-handler registrations stay extracted because they need invocation binding.

**Layers.** `LayerModel` holds explicit mappings (`project`, `namespace`, `type` scopes; longest prefix wins, project wins a tie) and allowed dependencies. `LayerMap` resolves each element once. `LayerGraph` aggregates `DirectCall`, `DeclaredCall`, `CandidateCall` and `TypeDependency` edges with counts and the witness at the smallest evidence location; unmapped source is `(unmapped)`, unmapped referenced assemblies are counted, not dropped. Namespace mappings match namespaces only (earlier releases also matched type-name prefixes; use `type:` for that).

**Rules.** A rule is a plain function returning `Finding(RuleId, Verdict, Subject, Message, Evidence)`. `LayerRules.AllowedDependencies` reports disallowed cross-layer edges as `Violation` and edges touching unmapped source as `Unknown`; candidate edges are not evaluated.

**Reachability vs paths.** `ReachabilityQuery` is breadth-first: each reachable callable once per root, with transition distance and one deterministic shortest witness, plus explicit dispatch, unresolved, missing-body and depth boundaries. `CallChainQuery` still enumerates every path for drill-down and is exponential in the worst case. Exception witnesses carry `ExceptionReason` codes (one per distinct reason) and, for dispatch boundaries, the dispatch kind.
