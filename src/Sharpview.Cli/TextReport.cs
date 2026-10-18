using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class TextReport(ProgramIndex index, TextWriter output, Arguments arguments)
{
    private readonly ProgramSnapshot snapshot = index.Snapshot;
    private readonly HashSet<string> ambiguous = index.Snapshot.Callables.GroupBy(x => x.DisplaySignature)
        .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
    internal string Name(CallableId id)
    {
        if (!index.TryGetCallable(id, out var method)) return id.Value;
        return method.DisplaySignature + (ambiguous.Contains(method.DisplaySignature) ? $" [{method.Compilation.Value}]" : "");
    }
    private string Project(CallableFact method) => index.ProjectName(method.Compilation);
    internal static string Location(SourceEvidence evidence) => $"{evidence.Path}:{evidence.StartLine}:{evidence.StartColumn}";
    private void MethodDetails(CallableId id, int transitions)
    {
        if (!index.TryGetCallable(id, out var method)) return;
        var type = index.DeclaringType(id)?.DisplayName ?? "<no containing type>";
        output.WriteLine($"  {Name(id)} | Project: {Project(method)} | Type: {type} | Transitions: {transitions}");
    }
    internal void Chain(CallChain chain)
    {
        var names = new List<string> { Name(chain.Root) };
        foreach (var step in chain.Steps)
            names.Add(step.Target is { } target ? Name(target) : $"[{step.Dispatch.ToString().ToLowerInvariant()} invocation: {step.TargetDisplay}]");
        var marker = chain.End switch
        {
            ChainEnd.Leaf => " [no outgoing calls]",
            ChainEnd.Metadata => " [metadata body unavailable]",
            ChainEnd.NoBody => " [body unavailable]",
            ChainEnd.UnknownTarget => " [unresolved target]",
            ChainEnd.Cycle => " [cycle]",
            ChainEnd.DepthLimit => " [max depth]",
            ChainEnd.DispatchBoundary => $" [{chain.Steps[^1].Dispatch.ToString().ToLowerInvariant()} target]",
            _ => ""
        };
        output.WriteLine(string.Join(" --> ", names) + marker);
        MethodDetails(chain.Root, 0);
        for (var i = 0; i < chain.Steps.Length; i++) if (chain.Steps[i].Target is { } target) MethodDetails(target, i + 1);
        if (arguments.Locations)
            foreach (var step in chain.Steps)
                foreach (var site in step.EvidenceSites) output.WriteLine($"  Evidence: {Location(index.CallSite(site).Evidence)} | {step.Dispatch} declared binding");
    }
    internal void Inspect(CancellationToken token)
    {
        foreach (var compilation in snapshot.Compilations)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Project: {compilation.ProjectPath} | Framework: {compilation.TargetFramework} | Configuration: {compilation.Configuration}");
        }
        output.WriteLine($"Types: {snapshot.Types.Length}; callables: {snapshot.Callables.Length}; calls: {snapshot.CallSites.Length}; dependencies: {snapshot.Dependencies.Length}; throws: {snapshot.ThrowSites.Length}; catches: {snapshot.Catches.Length}; actions: {AspNetConventions.ControllerActions(index).Length}; gaps: {snapshot.Gaps.Length}");
        foreach (var group in snapshot.CallSites.GroupBy(x => x.Dispatch).OrderBy(x => x.Key)) output.WriteLine($"Dispatch {group.Key}: {group.Count()}");
        Landscape(token);
    }
    internal void Landscape(CancellationToken token)
    {
        output.WriteLine("Exception landscape (explicit source origins; static types are separate from exact construction evidence):");
        foreach (var origin in snapshot.ThrowSites)
        {
            token.ThrowIfCancellationRequested();
            var method = index.Callable(origin.Owner);
            output.WriteLine($"Project: {Project(method)}\nOrigin: {Name(origin.Owner)}\n  Type: {index.DeclaringType(method.Id)?.DisplayName}\n  Throw: {origin.Kind} | Static exception type: {origin.StaticExceptionType?.DisplayName ?? "unknown / original for rethrow"} ({(origin.IsExactType ? "static type" : "static; runtime type may differ")})\n  Exact construction type: {origin.ExactExceptionType?.DisplayName ?? (origin.IsExactType ? origin.StaticExceptionType?.DisplayName : null) ?? "unknown"}\n  Evidence: {Location(origin.Evidence)}\n  Origin ID: {origin.Id.Value}");
        }
        foreach (var handler in snapshot.Catches)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Catch: {handler.CaughtType?.DisplayName ?? "all exceptions"} | Handler: {handler.Outcome}\n  Project: {Project(index.Callable(handler.Owner))} | Method: {Name(handler.Owner)}\n  Filter: {handler.Filter ?? "none"}\n  Evidence: {Location(handler.Evidence)}");
            foreach (var region in index.RegionsOf(handler.Id))
                output.WriteLine($"  Protected region: {Location(region.ProtectedBlock)} | span {region.ProtectedBlock.SpanStart}+{region.ProtectedBlock.SpanLength} | Finally: {region.HasFinally}");
            if (handler.SubsequentThrow is { } next && index.TryGetThrowSite(next, out var nextThrow)) output.WriteLine($"  Subsequent origin: {Location(nextThrow.Evidence)} ({next.Value})");
        }
        output.WriteLine("Boundary registrations (evidence only; runtime coverage and middleware order unknown):");
        foreach (var boundary in snapshot.BoundaryHandling)
            output.WriteLine($"  {boundary.Registration} | Project: {index.ProjectName(boundary.Compilation)} | {boundary.BoundMethod} | {Location(boundary.Evidence)}");
        if (snapshot.BoundaryHandling.IsEmpty) output.WriteLine("  No supported registration evidence found; boundary handling unknown.");
    }
    internal void Reach(RootReach reach)
    {
        output.WriteLine($"Root: {Name(reach.Root)}");
        foreach (var reached in reach.Reached)
        {
            var method = index.Callable(reached.Callable);
            output.WriteLine($"  [{reached.Distance}] {Name(reached.Callable)} | Project: {Project(method)}{(reached.ViaCandidate ? " (candidate)" : "")}");
            if (reached.Distance >= 2 || reached.WitnessUsesCandidate) output.WriteLine($"    Path: {Name(reach.Root)}{string.Concat(reached.Witness.Select(x => (x.Candidate ? " -[candidate]-> " : " --> ") + Name(x.Target)))}");
            if (arguments.FollowCandidates)
            {
                output.WriteLine($"    Direct distance: {reached.DirectDistance?.ToString() ?? "not established"}");
                if (reached.DirectDistance is not null && reached.WitnessUsesCandidate)
                {
                    output.WriteLine($"    Direct path: {string.Join(" --> ", reached.DirectWitness.Select(x => Name(x.Target)).Prepend(Name(reach.Root)))}");
                    if (arguments.Locations) foreach (var step in reached.DirectWitness)
                        output.WriteLine($"    Direct evidence: {Location(index.CallSite(step.Site).Evidence)}");
                }
            }
            if (arguments.Locations)
                foreach (var step in reached.Witness)
                    output.WriteLine($"    Evidence: {Location(index.CallSite(step.Site).Evidence)} | {(step.Candidate ? "implementation candidate" : "direct call")}");
        }
        foreach (var boundary in reach.Boundaries)
        {
            var where = boundary.Site is { } site ? Location(index.CallSite(site).Evidence) : index.Callable(boundary.Caller).Declarations.FirstOrDefault() is { } declaration ? Location(declaration) : "no source";
            output.WriteLine(boundary.Kind switch
            {
                ReachBoundaryKind.Dispatch => $"  Boundary: {boundary.Dispatch} dispatch --> {Name(boundary.DeclaredTarget!.Value)} | candidates followed: {boundary.CandidatesFollowed} | {where}",
                ReachBoundaryKind.Unresolved => $"  Boundary: unresolved target in {Name(boundary.Caller)} | {where}",
                ReachBoundaryKind.MissingBody => $"  Boundary: body unavailable for {Name(boundary.Caller)} | {where}",
                _ => $"  Boundary: depth limit at {Name(boundary.Caller)} | {where}"
            });
        }
    }
    // Where an exception is raised: method, declaring type, project and source location.
    private void Origin(string label, ThrowSiteFact origin)
    {
        var method = index.Callable(origin.Owner);
        output.WriteLine($"  {label}: {Name(origin.Owner)} | Type: {index.DeclaringType(method.Id)?.DisplayName ?? "<no containing type>"} | Project: {Project(method)} | {Location(origin.Evidence)}");
    }
    private static string Thrown(ThrowSiteFact origin) => origin.Kind == ThrowKind.Rethrow ? "rethrow of the caught exception"
        : (origin.ExactExceptionType ?? origin.StaticExceptionType)?.DisplayName is { } type
            ? type + (origin.IsExactType || origin.ExactExceptionType is not null ? "" : " (static type; runtime type may differ)")
            : "unknown type";
    private void ExamplePath(ExceptionWitness witness)
    {
        output.WriteLine($"  Example path: {string.Join(" --> ", witness.Path.Select(Name))}");
        if (arguments.Locations) foreach (var evidence in witness.Evidence) output.WriteLine($"    Evidence: {Location(evidence)}");
    }
    internal void Throw(ThrowSiteFact origin)
    {
        output.WriteLine($"Throw: {Thrown(origin)}");
        Origin("At", origin);
        output.WriteLine($"  Inside try in the same method: {(origin.ProtectedRegions.IsEmpty ? "no" : "yes")}");
    }
    internal void Caught(CaughtException caught)
    {
        var origin = index.ThrowSite(caught.Origin);
        var handler = index.Catch(caught.Catch);
        output.WriteLine($"Caught: {Thrown(origin)}");
        Origin("Thrown", origin);
        output.WriteLine($"  Caught: {Name(handler.Owner)} | catch ({handler.CaughtType?.DisplayName ?? "all exceptions"}){(handler.Filter is null ? "" : " when (...)")} | Project: {Project(index.Callable(handler.Owner))} | {Location(handler.Evidence)}");
        output.WriteLine($"  Handler: {Outcome(handler.Outcome)}{caught.Certainty switch
        {
            CatchCertainty.TypeUncertain => " | match uncertain: the runtime type may not be caught",
            CatchCertainty.Filtered => " | filter decides at runtime",
            _ => "",
        }}");
        ExamplePath(caught.Example);
    }
    internal void Miscaught(MiscaughtException miscaught)
    {
        var origin = index.ThrowSite(miscaught.Origin);
        var region = index.Region(miscaught.Missed.Region);
        var clauses = region.Catches.Select(x => index.Catch(x).CaughtType?.DisplayName ?? "all exceptions");
        output.WriteLine($"Miscaught: {miscaught.Missed.ExceptionType ?? Thrown(origin)}");
        Origin("Thrown", origin);
        output.WriteLine($"  Passed through: try in {Name(region.Owner)} | Project: {Project(index.Callable(region.Owner))} | {Location(region.ProtectedBlock)}");
        output.WriteLine($"  Catch clauses (none match): {string.Join(", ", clauses)}");
        output.WriteLine($"  Then: {string.Join("; ", miscaught.Outcomes.Select(Status))}");
        ExamplePath(miscaught.Example);
    }
    private static string Outcome(HandlerOutcome outcome) => outcome switch
    {
        HandlerOutcome.Intercepts => "handles it (recovery not proven)",
        HandlerOutcome.Rethrows => "rethrows it",
        HandlerOutcome.Replaces => "throws a replacement",
        _ => "unsupported handler flow; outcome unknown",
    };
    private static string Status(ExceptionStatus status) => status switch
    {
        ExceptionStatus.Intercepted => "caught further up",
        ExceptionStatus.RethrownOrReplaced => "rethrown or replaced further up",
        ExceptionStatus.MayEscape => "may escape the controller action",
        _ => "unknown",
    };
}
