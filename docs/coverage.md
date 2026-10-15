# Coverage and limits

What Sharpview models today, and what it deliberately does not. "Boundary" and "Unknown" results are how the limits show up in reports; nothing outside the model is silently dropped.

## Inputs and loading

- Restored SDK-style C# projects and solutions (`.csproj`, `.sln`, `.slnx`), hosted on .NET 10, loaded through the SDK's MSBuild workspace (no Visual Studio).
- One configuration/target-framework variant per project. An explicit `--framework` applies to the whole input and must be declared by every selected project.
- Missing restore assets in any selected project abort loading. Workspace or compiler failures that still produce a compilation become visible gaps.
- Project evaluation happens before the assets check, so project tooling and source generators may execute. Sharpview never builds, restores or edits the analysed source.
- Generated documents are included by default; `--exclude-generated` records each exclusion as evidence.

## Controller actions

Recognised by convention, classified in Core from extracted facts:

- **Included:** declared, public, instance, ordinary, non-generic methods on non-abstract, non-generic source types that derive from ASP.NET Core `ControllerBase` or carry `[Controller]`.
- **Excluded:** methods with inherited `[NonAction]`, types with inherited `[NonController]`.
- **Out of scope:**
  - inherited action enumeration;
  - discovering controllers by name alone;
  - application-model conventions;
  - endpoint reachability;
  - minimal APIs. Recognised minimal-API registrations produce gaps, and the CLI always prints these limits.

## Calls

| Supported (followed) | Boundary (recorded, not followed) | Gap (not modelled, listed) |
| --- | --- | --- |
| Normal invocations, explicit construction, source constructors | Interface and virtual calls, including virtual members with bodies (an explicit root may still inspect its own body) | Top-level statements |
| Property/event accessors, user-defined operators and conversions | Delegates, `dynamic`, function pointers, invalid operations | Field/property initialisers |
| Reduced extension methods; local functions and lambdas as separately owned bodies | Metadata-only (library) bodies | Implicit base-constructor calls, `foreach`/`using`/`lock`/iterator implicit calls |

Implementation candidates come from source types' interface maps and overrides; dependency-injection registrations are not read.

## Exceptions

- **Origins:** explicit `throw`, throw expressions and rethrows, with semantic types. `throw new E()` has an exact construction type; `throw e` only its static type.
- **Handlers:** a catch applies only to a region that protects the throw or the call. Rethrows keep the original exception through a simple handler; replacements continue with the new exception.
- **Unknown, with a reason:**
  - catch filters, which may accept, reject or throw;
  - complex handler flow and `finally` effects;
  - iterator, enumeration, disposal and `lock` behaviour;
  - custom awaitables, async void, and aliased or unobserved tasks;
  - origins inside library code;
  - handler-owned throws, whose reachability depends on other exceptions.
- **Not modelled:** implicit exceptions (from constructors, getters, framework calls) and branch feasibility. All branches and catch-body calls are structural facts.
- **Wording:** a catch is never reported as successful recovery. "May escape" is scoped to the action under this model. Registration evidence (`UseExceptionHandler`, `AddExceptionHandler`, `UseDeveloperExceptionPage`) does not prove middleware order, environment conditions, invocation, response availability or complete coverage.

## Layers and dependencies

- Layers require explicit `project:`, `namespace:` or `type:` prefix mappings.
- **Dependency sources:**
  - call sites;
  - signatures and inheritance, where base types contribute their type arguments;
  - construction and body use;
  - field and property types.
- **Not dependency sources:** top-level statements, attributes, events and generic constraints.

## Traversal

- Distances are static call transitions from a root, not stack depth.
- Cycles and depth/work/output limits are always visible; a global limit means the result is not exhaustive.
- Cancellation propagates through loading, extraction and traversal.

## How this is verified

Tests combine independently built Core snapshots, in-memory Roslyn compilations, a three-project Web API fixture and fresh CLI processes (see [validation](validation.md)). Sharpview's own solution is used only as a loader smoke input; no external Web API analysis is claimed yet.
