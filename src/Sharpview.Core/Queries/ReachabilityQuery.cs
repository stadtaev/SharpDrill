using System.Collections.Immutable;
using Sharpview.Graph;

namespace Sharpview.Core;

public sealed record ReachOptions(bool FollowCandidates = false, int MaxDepth = 20, int MaxExpansions = 100000)
{
    public void Validate()
    {
        if (MaxDepth < 0 || MaxExpansions < 1) throw new ArgumentException("Depth must be nonnegative; the work limit must be positive.");
    }
}
public enum ReachBoundaryKind { Dispatch, Unresolved, MissingBody, DepthLimit }
// Candidate steps follow an implementation candidate, never an inferred runtime target.
public sealed record ReachStep(CallSiteId Site, CallableId Target, bool Candidate);
// Distance/Witness describe a shortest allowed path when the root is complete; a work stop retains a valid
// admitted path whose distance and witness agree. ViaCandidate independently means no bounded direct-only
// path was found; a shorter witness may use candidates even for a directly reachable callable.
public sealed record ReachedCallable
{
    private Lazy<ImmutableArray<ReachStep>> witness;
    public CallableId Callable { get; init; }
    public int Distance { get; init; }
    public ImmutableArray<ReachStep> Witness { get => witness.Value; init => witness = new(() => value); }
    public bool ViaCandidate { get; init; }
    public bool WitnessUsesCandidate { get; init; }

    public ReachedCallable(CallableId Callable, int Distance, ImmutableArray<ReachStep> Witness, bool ViaCandidate)
        : this(Callable, Distance, () => Witness, ViaCandidate, Witness.Any(x => x.Candidate)) { }

    internal ReachedCallable(CallableId callable, int distance, Func<ImmutableArray<ReachStep>> witness,
        bool viaCandidate, bool witnessUsesCandidate)
    {
        Callable = callable; Distance = distance; this.witness = new(witness);
        ViaCandidate = viaCandidate; WitnessUsesCandidate = witnessUsesCandidate;
    }

    public void Deconstruct(out CallableId callable, out int distance, out ImmutableArray<ReachStep> witness, out bool viaCandidate)
    { callable = Callable; distance = Distance; witness = Witness; viaCandidate = ViaCandidate; }
}
// Caller is the callable where traversal stopped; Site is null for MissingBody and DepthLimit boundaries.
public sealed record ReachBoundary(CallableId Caller, CallSiteId? Site, ReachBoundaryKind Kind, DispatchKind Dispatch,
    CallableId? DeclaredTarget, int CandidatesFollowed);
public sealed record RootReach(CallableId Root, ImmutableArray<ReachedCallable> Reached, ImmutableArray<ReachBoundary> Boundaries, bool DepthLimitReached)
{
    public bool WorkLimitReached { get; init; }
}
public sealed record ReachResult(ImmutableArray<RootReach> Roots, bool WorkLimitReached, int ExpandedSites);

// Direct paths seed the tree first so equal-length witnesses prefer direct calls. Candidate relaxation may improve
// those distances and reopen their descendants. Site work is charged once, including unresolved sites in order.
public static class ReachabilityQuery
{
    public static ReachResult Analyze(ProgramIndex index, ImmutableArray<CallableId> roots, ReachOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        foreach (var root in roots) if (!index.ContainsCallable(root)) throw new ArgumentException($"Unknown root ID: {root.Value}");
        var results = ImmutableArray.CreateBuilder<RootReach>();
        var calls = index.GetCallGraph(cancellationToken);
        var siteEdges = calls.Graph.Edges.GroupBy(x => x.Site.Id).ToDictionary(x => x.Key, x => x.ToImmutableArray());
        var work = 0; var workLimited = false;
        foreach (var root in roots.Distinct().OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            BreadthFirstSearch<CallableId, CallEdge>? search = null;
            var examined = new HashSet<CallSiteId>();
            var deferred = new List<CallEdge>();
            var followedCandidates = new HashSet<CallEdgeId>();
            var candidatePhase = false;
            bool Expand(CallableId node, int distance)
            {
                if (index.Callable(node).Body != BodyStatus.Source || distance >= options.MaxDepth) return false;
                foreach (var site in index.Outgoing(node))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!examined.Contains(site.Id))
                    {
                        if (!search!.Charge(1)) break;
                        examined.Add(site.Id);
                    }
                    if (!siteEdges.TryGetValue(site.Id, out var edges)) continue;
                    foreach (var edge in edges)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (edge.Kind == CallEdgeKind.Direct) search!.Relax(edge);
                        else if (edge.Kind == CallEdgeKind.Candidate && options.FollowCandidates)
                        {
                            if (candidatePhase) { followedCandidates.Add(edge.Id); search!.Relax(edge); }
                            else deferred.Add(edge);
                        }
                    }
                }
                // Traversal is over sites (including unresolved ones), rather than charging their parallel edges.
                return false;
            }
            search = new(calls.Graph, Expand, budget: options.MaxExpansions - work, cancellationToken: cancellationToken);
            search.AddSource(root);
            search.Run();
            var direct = search.Discovered.ToHashSet();
            if (options.FollowCandidates && !search.BudgetExhausted)
            {
                candidatePhase = true;
                foreach (var edge in deferred) { followedCandidates.Add(edge.Id); search.Relax(edge); }
                search.Run();
            }
            work += search.Spent;
            workLimited = search.BudgetExhausted;

            // Compute witness provenance from the parent tree in distance order, without materializing any paths.
            var candidatePaths = search.FoldPaths(false, (prior, edge) => prior || edge.Kind == CallEdgeKind.Candidate);
            var reached = ImmutableArray.CreateBuilder<ReachedCallable>();
            foreach (var node in search.Discovered.Where(x => x != root).OrderBy(search.Distance)
                .ThenBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var usesCandidate = candidatePaths[node];
                reached.Add(new(node, search.Distance(node),
                    () => search.PathTo(node).Select(e => new ReachStep(e.Site.Id, e.Target, e.Kind == CallEdgeKind.Candidate)).ToImmutableArray(),
                    !direct.Contains(node), usesCandidate));
            }
            var (boundaries, depthLimited) = Boundaries(index, siteEdges, search, examined, followedCandidates, options, cancellationToken);
            results.Add(new(root, reached.ToImmutable(), boundaries, depthLimited) { WorkLimitReached = workLimited });
            if (workLimited) break;
        }
        return new(results.ToImmutable(), workLimited, work);
    }

    private static (ImmutableArray<ReachBoundary>, bool) Boundaries(ProgramIndex index,
        Dictionary<CallSiteId, ImmutableArray<CallEdge>> siteEdges, BreadthFirstSearch<CallableId, CallEdge> search,
        HashSet<CallSiteId> examined, HashSet<CallEdgeId> followedCandidates, ReachOptions options, CancellationToken cancellationToken)
    {
        var boundaries = ImmutableArray.CreateBuilder<ReachBoundary>();
        var depthLimited = false;
        foreach (var node in search.Discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index.Callable(node).Body != BodyStatus.Source)
            {
                boundaries.Add(new(node, null, ReachBoundaryKind.MissingBody, DispatchKind.Direct, null, 0));
                continue;
            }
            var atDepth = search.Distance(node) >= options.MaxDepth;
            var outgoing = index.Outgoing(node);
            // A depth boundary means something remains unknown or a relevant callee was not reached elsewhere.
            if (atDepth && outgoing.Any(site => !siteEdges.TryGetValue(site.Id, out var edges) ||
                site.Dispatch != DispatchKind.Direct || edges.Any(e => !search.IsDiscovered(e.Target))))
            {
                depthLimited = true;
                boundaries.Add(new(node, null, ReachBoundaryKind.DepthLimit, DispatchKind.Direct, null, 0));
            }
            foreach (var site in outgoing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!atDepth && !examined.Contains(site.Id)) continue;
                if (!siteEdges.TryGetValue(site.Id, out var edges))
                    boundaries.Add(new(node, site.Id, ReachBoundaryKind.Unresolved, site.Dispatch, null, 0));
                else if (site.Dispatch != DispatchKind.Direct)
                    boundaries.Add(new(node, site.Id, ReachBoundaryKind.Dispatch, site.Dispatch, site.BoundTarget,
                        edges.Count(x => x.Kind == CallEdgeKind.Candidate && followedCandidates.Contains(x.Id))));
            }
        }
        return (boundaries.ToImmutable(), depthLimited);
    }
}
