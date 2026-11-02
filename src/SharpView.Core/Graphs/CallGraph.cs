using System.Collections.Immutable;
using SharpView.Graph;

namespace SharpView.Core;

// Direct: a direct-dispatch call. Declared: an interface/virtual/delegate/dynamic call at its declared binding.
// Candidate: from a non-direct call site to one implementation candidate of its declared target (never a runtime claim).
public enum CallEdgeKind { Direct, Declared, Candidate }

// One edge per call site (Direct or Declared), plus one Candidate edge per candidate of a non-direct site.
// Length-prefixed components avoid ambiguity even when source IDs contain separators.
public readonly record struct CallEdgeId(string Value);

public sealed record CallEdge(CallableId Source, CallableId Target, CallSiteFact Site, CallEdgeKind Kind) : IEdge<CallableId>
{
    public CallEdgeId Id => new($"{Site.Id.Value.Length}:{Site.Id.Value}:{Kind}:{Target.Value.Length}:{Target.Value}");

    // A candidate edge to the declared target itself (a virtual member with its own body) adds no new dependency.
    public bool IsDeclaredTargetCandidate => Kind == CallEdgeKind.Candidate && Site.BoundTarget == Target;
}

// What a call site leads to, decided once so every traversal draws the same boundaries.
// Source: a direct call to a callable of the program (its body status is the callee's own business).
// Library: a call into a referenced assembly, directly or through an interface/virtual member that no source type
// implements; its exceptions are not modelled, so it is counted, never followed and never Unknown.
// Dispatch: an interface/virtual/delegate/dynamic call; Callees are its source implementation candidates (possibly
// none, which is a boundary). Following them is the query's choice.
// Unresolved: no bound target in the program.
public enum CallTargetKind { Source, Library, Dispatch, Unresolved }

public readonly record struct CallTarget(CallTargetKind Kind, ImmutableArray<CallableId> Callees);

// Whole-program call graph over every callable. Out-edges follow ProgramIndex.Outgoing order (target display, then
// site ID); a non-direct site's Declared edge is followed by its Candidate edges in ID order. Sites without a known
// target are not edges: they are listed in UnresolvedSites so traversals can report them as boundaries.
public sealed class CallGraph
{
    private readonly Dictionary<CallSiteId, CallTarget> targets;

    public DiGraph<CallableId, CallEdge> Graph { get; }
    public ImmutableArray<CallSiteFact> UnresolvedSites { get; }

    private CallGraph(DiGraph<CallableId, CallEdge> graph, ImmutableArray<CallSiteFact> unresolvedSites, Dictionary<CallSiteId, CallTarget> targets)
    {
        Graph = graph; UnresolvedSites = unresolvedSites; this.targets = targets;
    }

    // What the site leads to. A Dispatch target's Callees are its source implementation candidates in ID order:
    // possibilities, never inferred runtime targets. Every site of the snapshot resolves; an unknown ID is a caller bug.
    public CallTarget Resolve(CallSiteId site) => targets.TryGetValue(site, out var target) ? target : throw new KeyNotFoundException($"Unknown call site: {site.Value}");

    public static CallGraph Build(ProgramIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = DiGraph<CallableId, CallEdge>.CreateBuilder();
        var candidates = new Dictionary<CallableId, ImmutableArray<CallableId>>();
        var unresolved = ImmutableArray.CreateBuilder<CallSiteFact>();
        var targets = new Dictionary<CallSiteId, CallTarget>();
        bool IsLibrary(CallableId id) => index.Callable(id).Body == BodyStatus.MetadataOnly;
        foreach (var callable in index.Snapshot.Callables) { cancellationToken.ThrowIfCancellationRequested(); builder.AddNode(callable.Id); }
        foreach (var callable in index.Snapshot.Callables)
            foreach (var site in index.Outgoing(callable.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (site.BoundTarget is not { } target || !index.ContainsCallable(target))
                {
                    unresolved.Add(site);
                    // A dispatch with no declared member in the program still has nowhere to go: a boundary, not "unresolved".
                    targets[site.Id] = new(site.Dispatch == DispatchKind.Direct ? CallTargetKind.Unresolved : CallTargetKind.Dispatch, []);
                    continue;
                }
                if (site.Dispatch == DispatchKind.Direct)
                {
                    builder.AddEdge(new(callable.Id, target, site, CallEdgeKind.Direct));
                    targets[site.Id] = new(IsLibrary(target) ? CallTargetKind.Library : CallTargetKind.Source, [target]);
                    continue;
                }
                builder.AddEdge(new(callable.Id, target, site, CallEdgeKind.Declared));
                if (!candidates.TryGetValue(target, out var closure))
                    candidates[target] = closure = Candidates(index, target, cancellationToken);
                foreach (var candidate in closure)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    builder.AddEdge(new(callable.Id, candidate, site, CallEdgeKind.Candidate));
                }
                targets[site.Id] = IsLibrary(target) && closure.IsEmpty ? new(CallTargetKind.Library, [target]) : new(CallTargetKind.Dispatch, closure);
            }
        return new(builder.Build(), unresolved.ToImmutable(), targets);
    }

    // Transitive implementation candidates of a declared target in ID order. A declared target with its own source
    // body (a virtual method) is itself a candidate.
    private static ImmutableArray<CallableId> Candidates(ProgramIndex index, CallableId declared, CancellationToken cancellationToken)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        if (index.Callable(declared).Body == BodyStatus.Source) result.Add(declared.Value);
        var seen = new HashSet<CallableId> { declared };
        var pending = new Queue<CallableId>(); pending.Enqueue(declared);
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var implementation in index.Implementations(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(implementation.Implementation)) { result.Add(implementation.Implementation.Value); pending.Enqueue(implementation.Implementation); }
            }
        }
        return [.. result.Select(x => new CallableId(x))];
    }
}
