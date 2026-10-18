using System.Collections.Immutable;
using SharpView.Graph;

namespace SharpView.Core;

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
    // A quotient of the call graph and the dependency graph by layer. Edges whose caller/owner is referenced code
    // (not analysed source) are not program edges and are skipped.
    public static LayerGraph Build(ProgramIndex index, LayerMap map, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edges = new List<LayerEdge>();
        var external = 0;

        // The endpoint layer: a mapped layer, Unmapped for analysed source, or null for unmapped referenced code.
        string? CallableLayer(CallableId id) => map.LayerOf(id) ?? (map.IsSource(index.Callable(id).Compilation) ? LayerMap.Unmapped : null);
        string? TypeLayer(TypeId id) => map.LayerOf(id) ?? (map.IsSource(index.Type(id).Compilation) ? LayerMap.Unmapped : null);
        void Collect<TEdge>(Quotient<string, LayerEdgeKind, TEdge> quotient, Func<TEdge, (SourceEvidence? Evidence, string Source, string Target)> witness,
            Func<TEdge, bool> sourceInGroup)
        {
            foreach (var group in quotient.Edges)
            {
                var best = group.Edges.Select(witness).Aggregate((a, b) => Better(b, a) ? b : a);
                edges.Add(new(group.From, group.To, group.Key, group.Edges.Length, best.Evidence, best.Source, best.Target));
            }
            // Unassigned edges from source into unmapped referenced code are counted; edges owned by referenced code are not program edges.
            external += quotient.Unassigned.Count(sourceInGroup);
        }

        var callGraph = index.GetCallGraph(cancellationToken);
        var calls = callGraph.Graph.Where(x => !x.IsDeclaredTargetCandidate)
            .Quotient(CallableLayer, x => x.Kind switch { CallEdgeKind.Direct => LayerEdgeKind.DirectCall, CallEdgeKind.Declared => LayerEdgeKind.DeclaredCall, _ => LayerEdgeKind.CandidateCall }, cancellationToken);
        Collect(calls, x => (x.Site.Evidence, index.Callable(x.Source).DisplaySignature, index.Callable(x.Target).DisplaySignature), x => CallableLayer(x.Source) is not null);
        var unresolved = callGraph.UnresolvedSites.Count(x => CallableLayer(x.Caller) is not null);

        // Callable-owned facts take the callable's layer, which is its declaring type's layer.
        var dependencies = index.GetDependencyGraph(cancellationToken).Where(x => map.IsSource(x.OwnerCallable is { } owner ? index.Callable(owner).Compilation : index.Type(x.Source).Compilation))
            .Quotient(TypeLayer, _ => LayerEdgeKind.TypeDependency, cancellationToken);
        Collect(dependencies, x => (x.Fact.Evidence, x.OwnerCallable is { } owner ? index.Callable(owner).DisplaySignature : index.Type(x.Source).DisplayName,
            index.Type(x.Target).DisplayName), x => TypeLayer(x.Source) is not null);

        return new([.. edges.OrderBy(x => x.From, StringComparer.Ordinal).ThenBy(x => x.To, StringComparer.Ordinal).ThenBy(x => x.Kind)], external, unresolved);
    }

    // The witness is the fact with the smallest evidence location; facts without evidence lose to any with it.
    private static bool Better((SourceEvidence? Evidence, string Source, string Target) candidate, (SourceEvidence? Evidence, string Source, string Target) current) =>
        candidate.Evidence is not null && (current.Evidence is null || Compare(candidate.Evidence, candidate.Source, candidate.Target, current.Evidence, current.Source, current.Target) < 0);

    private static int Compare(SourceEvidence a, string aSource, string aTarget, SourceEvidence b, string bSource, string bTarget)
    {
        var result = string.CompareOrdinal(a.Path, b.Path);
        if (result == 0) result = a.SpanStart.CompareTo(b.SpanStart);
        if (result == 0) result = string.CompareOrdinal(aSource, bSource);
        return result != 0 ? result : string.CompareOrdinal(aTarget, bTarget);
    }
}
