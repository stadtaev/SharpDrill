using System.Collections.Immutable;
using Sharpview.Graph;

namespace Sharpview.Core;

// Direct: a direct-dispatch call. Declared: an interface/virtual/delegate/dynamic call at its declared binding.
// Candidate: from a non-direct call site to one implementation candidate of its declared target (never a runtime claim).
public enum CallEdgeKind { Direct, Declared, Candidate }

// One edge per call site (Direct or Declared), plus one Candidate edge per candidate of a non-direct site.
public sealed record CallEdge(CallableId Source, CallableId Target, CallSiteFact Site, CallEdgeKind Kind) : IEdge<CallableId>
{
    // A candidate edge to the declared target itself (a virtual member with its own body) adds no new dependency.
    public bool IsDeclaredTargetCandidate => Kind == CallEdgeKind.Candidate && Site.BoundTarget == Target;
}

// Whole-program call graph over every callable. Out-edges follow ProgramIndex.Outgoing order (target display, then
// site ID); a non-direct site's Declared edge is followed by its Candidate edges in ID order. Sites without a known
// target are not edges: they are listed per caller so traversals can report them as boundaries.
public sealed class CallGraph
{
    private readonly Dictionary<CallableId, ImmutableArray<CallSiteFact>> unresolved;

    public DiGraph<CallableId, CallEdge> Graph { get; }
    public ImmutableArray<CallSiteFact> UnresolvedSites { get; }

    private CallGraph(DiGraph<CallableId, CallEdge> graph, ImmutableArray<CallSiteFact> unresolvedSites)
    {
        Graph = graph; UnresolvedSites = unresolvedSites;
        unresolved = unresolvedSites.GroupBy(x => x.Caller).ToDictionary(x => x.Key, x => x.ToImmutableArray());
    }

    // Unresolved sites of a caller, in Outgoing order.
    public ImmutableArray<CallSiteFact> Unresolved(CallableId caller) => unresolved.GetValueOrDefault(caller, []);

    public static CallGraph Build(ProgramIndex index, CancellationToken cancellationToken = default)
    {
        var builder = DiGraph<CallableId, CallEdge>.CreateBuilder();
        var unresolved = ImmutableArray.CreateBuilder<CallSiteFact>();
        foreach (var callable in index.Snapshot.Callables) builder.AddNode(callable.Id);
        foreach (var callable in index.Snapshot.Callables)
            foreach (var site in index.Outgoing(callable.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (site.BoundTarget is not { } target || !index.ContainsCallable(target)) { unresolved.Add(site); continue; }
                if (site.Dispatch == DispatchKind.Direct) { builder.AddEdge(new(callable.Id, target, site, CallEdgeKind.Direct)); continue; }
                builder.AddEdge(new(callable.Id, target, site, CallEdgeKind.Declared));
                foreach (var candidate in Candidates(index, target)) builder.AddEdge(new(callable.Id, candidate, site, CallEdgeKind.Candidate));
            }
        return new(builder.Build(), unresolved.ToImmutable());
    }

    // Transitive implementation candidates of a declared target in ID order. A declared target with its own source
    // body (a virtual method) is itself a candidate.
    private static IEnumerable<CallableId> Candidates(ProgramIndex index, CallableId declared)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        if (index.Callable(declared).Body == BodyStatus.Source) result.Add(declared.Value);
        var seen = new HashSet<CallableId> { declared };
        var pending = new Queue<CallableId>(); pending.Enqueue(declared);
        while (pending.TryDequeue(out var current))
            foreach (var implementation in index.Implementations(current))
                if (seen.Add(implementation.Implementation)) { result.Add(implementation.Implementation.Value); pending.Enqueue(implementation.Implementation); }
        return result.Select(x => new CallableId(x));
    }
}
