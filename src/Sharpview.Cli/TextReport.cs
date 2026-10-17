using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class TextReport(ProgramIndex index, TextWriter output, Arguments options)
{
    private readonly ProgramSnapshot snapshot = index.Snapshot;
    internal LayerMap Layers { get; } = new(index, new LayerModel(options.Layers, options.Allowed));
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
        output.WriteLine($"  {Name(id)} | Project: {Project(method)} | Type: {type} | Layer: {Layers.LayerOf(id) ?? "unconfigured"} | Transitions: {transitions}");
    }
    internal void Chain(CallChain chain, bool details)
    {
        var names = new List<string> { Name(chain.Root) };
        foreach (var step in chain.Steps)
            names.Add(step.IsExcludedDispatch ? $"[{step.Dispatch.ToString().ToLowerInvariant()} call: {step.TargetDisplay}]"
                : step.Target is { } target ? Name(target) : $"[{step.Dispatch.ToString().ToLowerInvariant()} invocation: {step.TargetDisplay}]");
        var marker = chain.End switch
        {
            ChainEnd.Leaf => " [no outgoing calls]",
            ChainEnd.Metadata => " [metadata body unavailable]",
            ChainEnd.NoBody => " [body unavailable]",
            ChainEnd.UnknownTarget => " [unresolved target]",
            ChainEnd.Cycle => " [cycle]",
            ChainEnd.DepthLimit => " [max depth]",
            ChainEnd.DispatchBoundary when chain.Steps[^1].IsExcludedDispatch => "",
            ChainEnd.DispatchBoundary => $" [{chain.Steps[^1].Dispatch.ToString().ToLowerInvariant()} target]",
            _ => ""
        };
        output.WriteLine(string.Join(" --> ", names) + marker);
        if (details)
        {
            MethodDetails(chain.Root, 0);
            for (var i = 0; i < chain.Steps.Length; i++) if (chain.Steps[i].Target is { } target) MethodDetails(target, i + 1);
        }
        if (options.Locations)
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
        output.WriteLine("Controller exception witnesses:");
    }
    internal void Witness(ExceptionWitness witness)
    {
        var origin = witness.Origin is { } id && index.TryGetThrowSite(id, out var fact) ? fact : null;
        output.WriteLine($"Project: {Project(index.Callable(origin?.Owner ?? witness.Action))}");
        output.WriteLine($"Origin: {(origin is null ? "Unknown" : Name(origin.Owner))}");
        output.WriteLine($"Exception: {witness.ExceptionType ?? "Unknown"} ({(witness.IsExactType ? "exact construction" : "static; runtime type may differ")})");
        output.WriteLine($"Static exception type: {witness.StaticExceptionType ?? witness.ExceptionType ?? "Unknown"}");
        output.WriteLine($"Exact construction type: {witness.ExactExceptionType ?? (witness.IsExactType ? witness.ExceptionType : null) ?? "Unknown"}");
        output.WriteLine($"Path: {string.Join(" --> ", witness.Path.Select(Name))}");
        output.WriteLine($"Status: {Status(witness.Status)}\nReason: {witness.Reason}");
        if (witness.SubsequentOrigin is { } next && index.TryGetThrowSite(next, out var subsequent))
            output.WriteLine($"Subsequent origin: {Name(subsequent.Owner)} at {Location(subsequent.Evidence)} | {next.Value}");
        if (witness.SubsequentStatus is { } nextStatus) output.WriteLine($"Subsequent status: {Status(nextStatus)}");
        foreach (var evidence in witness.Evidence) output.WriteLine($"Evidence: {Location(evidence)}");
        var action = index.Callable(witness.Action);
        var boundaries = snapshot.BoundaryHandling.Where(x => x.Compilation == action.Compilation).ToArray();
        output.WriteLine(boundaries.Length == 0 ? "Boundary handling: Unknown (no supported registration evidence in action project)"
            : "Boundary handling: registration evidence; action interception and middleware coverage unknown");
        foreach (var boundary in boundaries) output.WriteLine($"  {boundary.Registration}: {Location(boundary.Evidence)}");
        output.WriteLine();
    }
    internal void Reach(RootReach reach)
    {
        output.WriteLine($"Root: {Name(reach.Root)}");
        foreach (var reached in reach.Reached)
        {
            var method = index.Callable(reached.Callable);
            output.WriteLine($"  [{reached.Distance}] {Name(reached.Callable)} | Project: {Project(method)} | Layer: {Layers.LayerOf(reached.Callable) ?? "unconfigured"}{(reached.ViaCandidate ? " (candidate)" : "")}");
            if (reached.Distance >= 2 || reached.WitnessUsesCandidate) output.WriteLine($"    Path: {Name(reach.Root)}{string.Concat(reached.Witness.Select(x => (x.Candidate ? " -[candidate]-> " : " --> ") + Name(x.Target)))}");
            if (options.FollowCandidates)
            {
                output.WriteLine($"    Direct distance: {reached.DirectDistance?.ToString() ?? "not established"}");
                if (reached.DirectDistance is not null && reached.WitnessUsesCandidate)
                {
                    output.WriteLine($"    Direct path: {string.Join(" --> ", reached.DirectWitness.Select(x => Name(x.Target)).Prepend(Name(reach.Root)))}");
                    if (options.Locations) foreach (var step in reached.DirectWitness)
                        output.WriteLine($"    Direct evidence: {Location(index.CallSite(step.Site).Evidence)}");
                }
            }
            if (options.Locations)
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
    internal void LayerOverview(LayerGraph graph, IEnumerable<Finding> findings)
    {
        foreach (var edge in graph.Edges)
            output.WriteLine($"Layer edge: {edge.From} --> {edge.To} | {edge.Kind} | {edge.Count} | Evidence: {(edge.Evidence is null ? "none" : Location(edge.Evidence))} | Witness: {edge.WitnessSource} --> {edge.WitnessTarget}");
        foreach (var finding in findings)
            output.WriteLine($"Finding [{finding.RuleId}] {finding.Verdict}: {finding.Subject} | {finding.Message}" + (finding.Evidence.IsEmpty ? "" : $" | Evidence: {Location(finding.Evidence[0])}"));
    }
    private static string Status(ExceptionStatus status) => status switch
    {
        ExceptionStatus.Intercepted => "Intercepted by a matching handler",
        ExceptionStatus.RethrownOrReplaced => "Rethrown or replaced",
        ExceptionStatus.MayEscape => "May escape the controller action under the supported analysis model",
        _ => "Unknown"
    };
}
