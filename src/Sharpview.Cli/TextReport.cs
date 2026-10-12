using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class TextReport(ProgramSnapshot snapshot, TextWriter output, Arguments options)
{
    private readonly Dictionary<CallableId, CallableFact> methods = snapshot.Callables.ToDictionary(x => x.Id);
    private readonly Dictionary<CallSiteId, CallSiteFact> sites = snapshot.CallSites.ToDictionary(x => x.Id);
    private readonly Dictionary<ThrowSiteId, ThrowSiteFact> throws = snapshot.ThrowSites.ToDictionary(x => x.Id);
    private readonly HashSet<string> ambiguous = snapshot.Callables.GroupBy(x => x.DisplaySignature)
        .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
    internal string Name(CallableId id)
    {
        if (!methods.TryGetValue(id, out var method)) return id.Value;
        return method.DisplaySignature + (ambiguous.Contains(method.DisplaySignature) ? $" [{method.Compilation.Value}]" : "");
    }
    private string Project(CallableFact method) => snapshot.Compilations.FirstOrDefault(x => x.Id == method.Compilation) is { } compilation
        ? Path.GetFileNameWithoutExtension(compilation.ProjectPath) : "metadata";
    internal static string Location(SourceEvidence evidence) => $"{evidence.Path}:{evidence.StartLine}:{evidence.StartColumn}";
    private void MethodDetails(CallableId id, int transitions)
    {
        if (!methods.TryGetValue(id, out var method)) return;
        var type = snapshot.Types.FirstOrDefault(x => x.Id == method.DeclaringType)?.DisplayName ?? "<no containing type>";
        output.WriteLine($"  {Name(id)} | Project: {Project(method)} | Type: {type} | Layer: {LayerQuery.Resolve(snapshot, method, options.Layers) ?? "unconfigured"} | Transitions: {transitions}");
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
                foreach (var site in step.EvidenceSites) output.WriteLine($"  Evidence: {Location(sites[site].Evidence)} | {step.Dispatch} declared binding");
    }
    internal void Inspect(CancellationToken token)
    {
        foreach (var compilation in snapshot.Compilations)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Project: {compilation.ProjectPath} | Framework: {compilation.TargetFramework} | Configuration: {compilation.Configuration}");
        }
        output.WriteLine($"Types: {snapshot.Types.Length}; callables: {snapshot.Callables.Length}; calls: {snapshot.CallSites.Length}; dependencies: {snapshot.Dependencies.Length}; throws: {snapshot.ThrowSites.Length}; catches: {snapshot.Catches.Length}; actions: {snapshot.ControllerActions.Length}; gaps: {snapshot.Gaps.Length}");
        foreach (var group in snapshot.CallSites.GroupBy(x => x.Dispatch).OrderBy(x => x.Key)) output.WriteLine($"Dispatch {group.Key}: {group.Count()}");
        Landscape(token);
    }
    internal void Landscape(CancellationToken token)
    {
        output.WriteLine("Exception landscape (explicit source origins; static types are separate from exact construction evidence):");
        foreach (var origin in snapshot.ThrowSites)
        {
            token.ThrowIfCancellationRequested();
            var method = methods[origin.Owner];
            output.WriteLine($"Project: {Project(method)}\nOrigin: {Name(origin.Owner)}\n  Type: {snapshot.Types.FirstOrDefault(x => x.Id == method.DeclaringType)?.DisplayName}\n  Throw: {origin.Kind} | Static exception type: {origin.StaticExceptionType?.DisplayName ?? "unknown / original for rethrow"} ({(origin.IsExactType ? "static type" : "static; runtime type may differ")})\n  Exact construction type: {origin.ExactExceptionType?.DisplayName ?? (origin.IsExactType ? origin.StaticExceptionType?.DisplayName : null) ?? "unknown"}\n  Evidence: {Location(origin.Evidence)}\n  Origin ID: {origin.Id.Value}");
        }
        foreach (var handler in snapshot.Catches)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Catch: {handler.CaughtType?.DisplayName ?? "all exceptions"} | Handler: {handler.Outcome}\n  Project: {Project(methods[handler.Owner])} | Method: {Name(handler.Owner)}\n  Filter: {handler.Filter ?? "none"}\n  Evidence: {Location(handler.Evidence)}");
            foreach (var region in snapshot.ExceptionRegions.Where(x => x.Catches.Contains(handler.Id)))
                output.WriteLine($"  Protected region: {Location(region.ProtectedBlock)} | span {region.ProtectedBlock.SpanStart}+{region.ProtectedBlock.SpanLength} | Finally: {region.HasFinally}");
            if (handler.SubsequentThrow is { } next && throws.TryGetValue(next, out var nextThrow)) output.WriteLine($"  Subsequent origin: {Location(nextThrow.Evidence)} ({next.Value})");
        }
        output.WriteLine("Boundary registrations (evidence only; runtime coverage and middleware order unknown):");
        foreach (var boundary in snapshot.BoundaryHandling)
            output.WriteLine($"  {boundary.Registration} | Project: {Path.GetFileNameWithoutExtension(snapshot.Compilations.Single(x => x.Id == boundary.Compilation).ProjectPath)} | {boundary.BoundMethod} | {Location(boundary.Evidence)}");
        if (snapshot.BoundaryHandling.IsEmpty) output.WriteLine("  No supported registration evidence found; boundary handling unknown.");
        output.WriteLine("Controller exception witnesses:");
    }
    internal void Witness(ExceptionWitness witness)
    {
        var origin = witness.Origin is { } id && throws.TryGetValue(id, out var fact) ? fact : null;
        output.WriteLine($"Project: {Project(methods[origin?.Owner ?? witness.Action])}");
        output.WriteLine($"Origin: {(origin is null ? "Unknown" : Name(origin.Owner))}");
        output.WriteLine($"Exception: {witness.ExceptionType ?? "Unknown"} ({(witness.IsExactType ? "exact construction" : "static; runtime type may differ")})");
        output.WriteLine($"Static exception type: {witness.StaticExceptionType ?? witness.ExceptionType ?? "Unknown"}");
        output.WriteLine($"Exact construction type: {witness.ExactExceptionType ?? (witness.IsExactType ? witness.ExceptionType : null) ?? "Unknown"}");
        output.WriteLine($"Path: {string.Join(" --> ", witness.Path.Select(Name))}");
        output.WriteLine($"Status: {Status(witness.Status)}\nReason: {witness.Reason}");
        if (witness.SubsequentOrigin is { } next && throws.TryGetValue(next, out var subsequent))
            output.WriteLine($"Subsequent origin: {Name(subsequent.Owner)} at {Location(subsequent.Evidence)} | {next.Value}");
        if (witness.SubsequentStatus is { } nextStatus) output.WriteLine($"Subsequent status: {Status(nextStatus)}");
        foreach (var evidence in witness.Evidence) output.WriteLine($"Evidence: {Location(evidence)}");
        var action = methods[witness.Action];
        var boundaries = snapshot.BoundaryHandling.Where(x => x.Compilation == action.Compilation).ToArray();
        output.WriteLine(boundaries.Length == 0 ? "Boundary handling: Unknown (no supported registration evidence in action project)"
            : "Boundary handling: registration evidence; action interception and middleware coverage unknown");
        foreach (var boundary in boundaries) output.WriteLine($"  {boundary.Registration}: {Location(boundary.Evidence)}");
        output.WriteLine();
    }
    private static string Status(ExceptionStatus status) => status switch
    {
        ExceptionStatus.Intercepted => "Intercepted by a matching handler",
        ExceptionStatus.RethrownOrReplaced => "Rethrown or replaced",
        ExceptionStatus.MayEscape => "May escape the controller action under the supported analysis model",
        _ => "Unknown"
    };
}
