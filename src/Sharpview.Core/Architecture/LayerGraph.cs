using System.Collections.Immutable;

namespace Sharpview.Core;

// DirectCall: direct dispatch to a bound target. DeclaredCall: interface/virtual/delegate/dynamic site, counted at its
// declared binding. CandidateCall: from such a site to an implementation candidate (never an inferred runtime target).
// TypeDependency: signature, inheritance, construction, body-use, field and property dependencies.
public enum LayerEdgeKind { DirectCall, DeclaredCall, CandidateCall, TypeDependency }

// Count is the number of supporting call sites or dependency facts. The witness is the supporting fact with the
// smallest evidence location (path, then span), so it is stable across runs.
public sealed record LayerEdge(string From, string To, LayerEdgeKind Kind, int Count,
    SourceEvidence? Evidence, string WitnessSource, string WitnessTarget);

// ExternalReferences: calls/dependencies into referenced assemblies that no mapping covers (kept as a count, not dropped).
// UnresolvedCalls: call sites without a known bound target.
public sealed record LayerGraph(ImmutableArray<LayerEdge> Edges, int ExternalReferences, int UnresolvedCalls)
{
    public static LayerGraph Build(ProgramIndex index, LayerMap map, CancellationToken cancellationToken = default)
    {
        var edges = new Dictionary<(string From, string To, LayerEdgeKind Kind), (int Count, SourceEvidence? Evidence, string Source, string Target)>();
        var external = 0; var unresolved = 0;

        // Returns the endpoint layer, or null when the endpoint is an unmapped referenced element.
        string? Layer(CompilationId compilation, string? layer) => layer ?? (map.IsSource(compilation) ? LayerMap.Unmapped : null);
        void Add(string from, string? to, LayerEdgeKind kind, SourceEvidence? evidence, string source, string target)
        {
            if (to is null) { external++; return; }
            var key = (from, to, kind);
            if (!edges.TryGetValue(key, out var edge)) { edges[key] = (1, evidence, source, target); return; }
            var better = evidence is not null && (edge.Evidence is null || Compare(evidence, source, target, edge.Evidence, edge.Source, edge.Target) < 0);
            edges[key] = better ? (edge.Count + 1, evidence, source, target) : (edge.Count + 1, edge.Evidence, edge.Source, edge.Target);
        }

        foreach (var site in index.Snapshot.CallSites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var caller = index.Callable(site.Caller);
            var from = Layer(caller.Compilation, map.LayerOf(caller.Id));
            if (from is null) continue;
            if (site.BoundTarget is not { } bound || !index.TryGetCallable(bound, out var target)) { unresolved++; continue; }
            var direct = site.Dispatch == DispatchKind.Direct;
            Add(from, Layer(target.Compilation, map.LayerOf(bound)), direct ? LayerEdgeKind.DirectCall : LayerEdgeKind.DeclaredCall,
                site.Evidence, caller.DisplaySignature, target.DisplaySignature);
            if (direct) continue;
            // Overrides of overrides are candidates too; each candidate counts once per site.
            var seen = new HashSet<CallableId> { bound };
            var pending = new Queue<CallableId>(); pending.Enqueue(bound);
            while (pending.TryDequeue(out var declared))
                foreach (var candidate in index.Implementations(declared))
                {
                    if (!seen.Add(candidate.Implementation)) continue;
                    pending.Enqueue(candidate.Implementation);
                    var implementation = index.Callable(candidate.Implementation);
                    Add(from, Layer(implementation.Compilation, map.LayerOf(implementation.Id)), LayerEdgeKind.CandidateCall,
                        site.Evidence, caller.DisplaySignature, implementation.DisplaySignature);
                }
        }

        foreach (var dependency in index.Snapshot.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only dependencies owned by analysed source; facts about referenced members are not program edges.
            string? from; string source;
            if (dependency.OwningCallable is { } owner && index.TryGetCallable(owner, out var callable))
            {
                if (!map.IsSource(callable.Compilation)) continue;
                from = map.LayerOf(owner) ?? LayerMap.Unmapped; source = callable.DisplaySignature;
            }
            else if (dependency.OwningType is { } ownerType && index.TryGetType(ownerType, out var type))
            {
                if (!map.IsSource(type.Compilation)) continue;
                from = map.LayerOf(ownerType) ?? LayerMap.Unmapped; source = type.DisplayName;
            }
            else continue;
            // Type parameters have no declaration and are not dependencies on another element.
            if (dependency.ReferencedType.Declaration is not { } referenced || !index.TryGetType(referenced, out var target)) continue;
            Add(from, Layer(target.Compilation, map.LayerOf(referenced)), LayerEdgeKind.TypeDependency, dependency.Evidence, source, target.DisplayName);
        }

        return new([.. edges.Select(x => new LayerEdge(x.Key.From, x.Key.To, x.Key.Kind, x.Value.Count, x.Value.Evidence, x.Value.Source, x.Value.Target))
            .OrderBy(x => x.From, StringComparer.Ordinal).ThenBy(x => x.To, StringComparer.Ordinal).ThenBy(x => x.Kind)], external, unresolved);
    }

    private static int Compare(SourceEvidence a, string aSource, string aTarget, SourceEvidence b, string bSource, string bTarget)
    {
        var result = string.CompareOrdinal(a.Path, b.Path);
        if (result == 0) result = a.SpanStart.CompareTo(b.SpanStart);
        if (result == 0) result = string.CompareOrdinal(aSource, bSource);
        return result != 0 ? result : string.CompareOrdinal(aTarget, bTarget);
    }
}
