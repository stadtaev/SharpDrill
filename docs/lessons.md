# Lessons learned

The first run on a real, mid-sized ASP.NET Core API (16 controller actions, DI everywhere, records and a `Result<T>` pattern) produced almost no information. `--uncaught` reported **0 explicit escapes** and between 4 and 56 unknown effects per action; `--caught` and `--miscaught` reported **0 results** with `witnesses: 1000; unknown: 1000; output limit: True`; every run ended in 544 raw gap lines. The tool had passed all its tests, and its fixture output looked right.

This document records why, because the causes were design mistakes, not bugs: wrong claims about what the analysis knows, defaults that matched our fixture instead of real code, and a budget that quietly changed what a zero meant. Each section ends with the rule we now follow.

## What the run looked like

| Symptom in the output | Cause | Fixed by |
| --- | --- | --- |
| `Unknown [MissingBody]` **with** a file:line (record parameters, property declarations) | Auto-property accessors and record constructors have no body in source, so they were classed "missing" | [`BodyStatus.Synthesized`](../src/SharpView.Core/Model/Facts.cs) |
| `Unknown [MissingBody]` **without** a location | Any call into framework or NuGet code | Library calls are counted, never Unknown |
| `Virtual dispatch` on `Ok(...)`, `StatusCode(...)` | Virtual members of `ControllerBase`: library code | Same as above |
| `Interface dispatch` on every service call | The analysis stopped at each DI interface, so nothing behind it was ever reached | Exception views follow source implementations by default |
| `caught: 0` / `miscaught: 0` with `output limit: True` | 1000 uncertainty witnesses filled the cap before a single real throw was examined | Separate budgets, de-duplication, an explicit message |
| `Implicit enumeration/disposal calls are outside supported extraction` | Every `foreach` and `using` was a gap | Their implicit calls are extracted |
| 544 lines of `Gap [...]` on stderr | One line per occurrence of a handful of causes | Grouped by cause with a count |

## 1. Unknown is only useful when it is rare and actionable

The principle "*Unknown is a result, not an error*" was right and we applied it too uniformly. We emitted `Unknown` for three very different situations and printed them the same way:

| Situation | Honest status | Example |
| --- | --- | --- |
| **Out of scope**: the analysis does not claim to know | A scope note, not a result | `string.IsNullOrEmpty`, `Guid.NewGuid()` |
| **Not looked at**: we chose not to follow it | A choice, visible and reversible | Stopping at an interface call |
| **Possibly wrong**: we followed a possibility, not a proof | A marked possibility | An implementation candidate of an interface |

Only a genuine "we cannot decide something we are trying to decide" belongs in *Unknown*: a catch filter, an origin inside a handler, an unobserved task, a cycle. On the fixture that is 5 witnesses. On the real API, before the fix, it was thousands, and a result that is always incomplete conveys nothing.

**Rule:** before adding an Unknown, say which of the three situations it is. If an agent reading the line could not act on it, it is a note, not an Unknown.

## 2. A body status is a claim about knowledge, not about syntax

`BodyStatus` was derived from syntax: a declaration with a body is `Source`, a source declaration without one is `NoBody`, everything else is `MetadataOnly`. That is wrong for members the compiler provides:

- the implicit constructor of `class Plain { }` and the primary constructor of `record Error(string Code, string Message)`;
- `{ get; set; }` and `{ get; init; }` accessors, and field-like event accessors;
- record members (`Equals`, `ToString`, `Deconstruct`, `Clone`).

They belong to *our* code and cannot contain an explicit `throw`. Calling them "missing" made every action that built a record or read a DTO property "unknown", which in this codebase was every action.

[`IsCompilerProvided`](../src/SharpView.Roslyn/Extraction/FactExtractor.Symbols.cs) now classifies them: member of a source assembly, not abstract, not extern, not on an interface or delegate, and either implicitly declared, an accessor of a property or event, or a constructor declared by the type syntax itself. Abstract and interface members stay `NoBody`: they really have no code.

**Rule:** a status describes what we know about the *code that runs*. "Has no syntax" and "has no code" are different things; check what the compiler provides before reporting something missing.

## 3. State the scope of the claim, then make the code and the docs agree

[coverage.md](coverage.md) already said *"Not modelled: implicit exceptions (from constructors, getters, framework calls)"*, while the code treated every library callee as `Unknown: origins inside library code`. The documentation and the implementation disagreed, and the implementation was the pessimistic one.

The claim we can actually make is: *which explicit `throw`s in your source can reach an action, and what catches them*. Exceptions thrown inside library code are outside it. So a library call is not an uncertainty; it is the boundary of the claim. We still make it visible, once per action (`Library calls not analysed: 5 methods (e.g. ...)`), so nobody mistakes "no escapes" for "cannot throw".

This applies to library virtual and interface calls too, when nothing in source overrides or implements the member. If a source type *does*, the call is genuinely uncertain and stays that way.

**Rule:** decide what the tool claims before deciding what it reports. If the documentation lists something as "not modelled", the code must not report it as "unknown".

## 4. Nothing behind DI interfaces was ever reached

The founding principle, "declared, candidate and runtime are different things", is sound, and the default we derived from it was useless for the most common shape of ASP.NET code: controller → `IService` → implementation. With candidates off, every service call ended the analysis, so a throw in a service could never appear in `--uncaught`, `--caught` or `--miscaught`. The output was *correct* and *empty*.

The principle does not require the strict default; it requires that possibilities are **marked**. Exception views now follow source implementation candidates by default and label every result that crossed one (`~>`, with `(1 of N implementations)` where source has several). A proven direct path to the same throw replaces the candidate one. `--declared-only` restores the strict behaviour. A call with no implementation in source stays Unknown and says so ("no source implementation found").

Following candidates exposed a precision problem we had not met before:

> `foreach` over a `List<T>` ends with an implicit `Dispose()`. Roslyn reports it as `IDisposable.Dispose`, an interface call. Our candidate lookup for that interface method is *every* `Dispose` implemented anywhere in the program. So every `foreach` in the application looked as though it might call any disposable class we had written.

The compiler binds a call written in source to the receiver type's own member. We now resolve implicit calls the same way ([`ImplicitCall`](../src/SharpView.Roslyn/Extraction/BodyWalker.cs)): a library receiver becomes a library call, and a source receiver has candidates only among its own overrides.

**Rule:** choose defaults for the typical program, not the principled corner. A default that returns nothing for the common case is a bug even when it is "conservative". Keep the honesty in the marking, not in the refusal.

## 5. The cap was out

All uncertainty witnesses and all real results shared one counter, `MaxResults` = 1000. In a program with many boundaries the first 1000 witnesses were all uncertainty, the search stopped, and the report derived from the witnesses (`caught`, `miscaught`) was built from an empty list. It printed `caught: 0`, a statement of fact that was false, with the real signal buried in a stats line (`output limit: True`).

What went wrong, and the fix in [`ExceptionQuery`](../src/SharpView.Core/Queries/ExceptionQuery.cs):

- **One budget for two kinds of thing.** Origin witnesses are the answer; uncertainty is context. Origins now have the budget and stop the search when exceeded; uncertainty has its own budget and never stops it.
- **No de-duplication.** The same unknown site, reached by many paths, was reported once per path. It is now reported once per action and site.
- **A limit changed the meaning of zero.** `0` with a limit reached means "unknown", not "none". The output now says so in words (`Stopped after N origin witnesses: the list above is incomplete. Raise --budget`), and a dropped-uncertainty flag keeps the result from being called complete.
- **Identical lines.** `OsaBaseController.cs:49:13` appeared three times. Identical lines are now collapsed to one with `(x3)`.

**Rules:** a cap must never silently change what an empty result means. Budget each category separately. Every limit message names what to raise.

## 6. Completeness is not the same as usefulness

Every run on the real project exits with code 1, because 420 field and property initializers are "ownerless" gaps that apply to the whole program. The fixture exits 1 too, and we documented that as *expected* ("the sample fixture returns 1 on purpose"). That sentence normalised the wrong thing: a tool that always reports "incomplete" teaches its users, and any agent calling it, to ignore the signal. This remains open (see below).

The cheap part is presentation. Gaps are now grouped by cause (`Gap [Unsupported] x420: ... (first at ...)`), so 544 lines become a handful that say what kind of thing is unsupported and where to start looking.

**Rule:** separate findings, caveats and errors. A caveat that applies to every run is not a property of any result; report it once, not as a failure.

## 7. Option sprawl is a symptom

`exceptions` had grown `--root`, `--max-depth`, `--max-paths`, `--budget`, `--locations` and, for a while, `--follow-candidates`. Most of those only existed to compensate for defaults that were wrong:

- `--follow-candidates` was needed because the default produced nothing useful;
- `--max-paths` existed to raise a cap that was being exhausted by noise;
- `--max-depth` tuned a limit that nobody could say a good value for.

After the changes the command has one limit, `--budget`, which scales the others, and one switch with a clear meaning, `--declared-only`.

**Rule:** an option should change what an answer *means*, never merely how much of it you get. If a number can be derived from another, derive it. If you cannot say what value a user should pick, it is not an option.

## 8. How we missed it, and how we found it

- **The fixture was built to exercise the model.** It contains a catch filter, a rethrow, an unobserved task and an interface with no implementation, deliberately. "18 unknown witnesses" looked like coverage of the model. It was mostly `StorageException..ctor()`, a compiler-provided constructor. Our tests asserted the noise (`witnesses: 26; unknown: 18`), so they defended the bug.
- **The fixture has no realistic app.** No records, no DI with a real implementation, no base controller calling `Ok(...)`, no `foreach`, no `using`. The first real project had all of them in every action.
- **What worked.** Before touching anything we built a small repro with exactly the patterns in the failing log and reproduced every symptom in minutes. Each fix was then checked against the repro and the fixture, and the fixture's outputs were compared byte for byte before and after unrelated refactors.
- **Two engines, one policy.** The detailed path search and the bottom-up summary make the same decisions (is this a library call? a dispatch boundary? a missing body?) in two places. Both needed every change here. A shared "what does this call site lead to" helper would remove the duplication and the risk of drift.

**Rules:** keep a realistic sample app next to the contrived fixture, and run every new analysis on it before shipping. Judge output by the *ratio* of signal to caveat, not by whether each caveat is individually justified. Ask of every line: what would an agent do with this?

## Technical notes (Roslyn)

- `IUsingOperation.DisposeInfo` is internal. Find `Dispose` from the resource type: `IDisposable.Dispose` for ordinary types, a pattern `Dispose()` for ref structs.
- A `foreach` over an array is wrapped in an implicit conversion to `IEnumerable`; unwrap it before deciding the collection is an array (arrays are enumerated without calls).
- `GetForEachStatementInfo` returns the interface methods; resolve them through `FindImplementationForInterfaceMember` on the receiver type.
- `IsImplicitlyDeclared` is true for record members and implicit constructors but not for written auto-property accessors, whose `AssociatedSymbol` is the property. Check both, plus constructors declared by the type syntax (primary constructors).
- Enums are serialized as numbers in the snapshot: add new `BodyStatus` values at the end so older snapshots still load.
- Library members are `MetadataOnly`; checking the *assembly* (is it one of the loaded projects?) is what separates "library" from "compiler-provided member of our code".

## Open items

- **Iterators.** `yield return` bodies run inside a generated `MoveNext`, when the caller enumerates; they are still a gap.
- **Library exceptions.** Library calls end the trace, so a `catch (JsonException)` around `JsonSerializer.Deserialize` has nothing to explain it. The `<exception cref>` tags in the referenced assemblies' XML docs could supply those origins.

Resolved since the first run: global caveats no longer set exit 1 (only gaps that affect a result do); initializers are attributed to the constructors that run them; `await foreach`/`await using` and awaited locals are modelled; handler outcomes come from control flow, filters are possible catches and a `finally` matters only when it can throw.
- **A realistic fixture.** A second sample solution with DI, records, a `Result<T>`, a base controller and disposables, so the real-code shape is under test.
- **One call-site resolution helper** shared by the detailed and summary engines.
