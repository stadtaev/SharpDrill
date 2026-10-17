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
// Distance counts call transitions (not runtime stack depth); Witness is one shortest path from the root.
public sealed record ReachedCallable(CallableId Callable, int Distance, ImmutableArray<ReachStep> Witness, bool ViaCandidate);
// Caller is the callable where traversal stopped; Site is null for MissingBody and DepthLimit boundaries.
public sealed record ReachBoundary(CallableId Caller, CallSiteId? Site, ReachBoundaryKind Kind, DispatchKind Dispatch,
    CallableId? DeclaredTarget, int CandidatesFollowed);
public sealed record RootReach(CallableId Root, ImmutableArray<ReachedCallable> Reached, ImmutableArray<ReachBoundary> Boundaries, bool DepthLimitReached);
public sealed record ReachResult(ImmutableArray<RootReach> Roots, bool WorkLimitReached, int ExpandedSites);

// Breadth-first reachability per root: each callable is reported once with one shortest witness. With candidates
// followed, direct reachability is computed first; ViaCandidate marks callables reachable only through a candidate step. Unlike path
// enumeration this stays linear in the call graph, so it suits "what can this action reach" rules on large solutions.
public static class ReachabilityQuery
{
    public static ReachResult Analyze(ProgramIndex index, ImmutableArray<CallableId> roots, ReachOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        foreach (var root in roots) if (!index.ContainsCallable(root)) throw new ArgumentException($"Unknown root ID: {root.Value}");
        var results = ImmutableArray.CreateBuilder<RootReach>();
        var calls = index.CallGraph;
        var work = 0; var workLimited = false;
        foreach (var root in roots.Distinct().OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
        {
            BreadthFirstSearch<CallableId, CallEdge>? search = null;
            var charged = new HashSet<CallableId>();
            // Bodies are expanded within the depth limit. Unresolved sites are not graph edges, so they are charged
            // to the shared budget when their caller is expanded.
            bool Expand(CallableId node, int distance) =>
                index.Callable(node).Body == BodyStatus.Source && distance < options.MaxDepth && search!.Charge(calls.Unresolved(node).Length) && charged.Add(node);
            // A non-direct site costs one examined site; its candidate edges belong to that site and are free.
            search = new(calls.Graph, Expand, x => x.Kind == CallEdgeKind.Candidate ? 0 : 1, options.MaxExpansions - work, cancellationToken);
            search.AddSource(root);
            // Candidate steps found during the direct-only phase wait until every definitely reachable callable is known,
            // so a callable reachable through direct calls is never labelled as candidate-only.
            var deferred = new List<CallEdge>();
            search.Run(x => x.Kind == CallEdgeKind.Direct || x.Kind == CallEdgeKind.Candidate && options.FollowCandidates && Defer(x));
            bool Defer(CallEdge edge) { deferred.Add(edge); return false; }
            if (options.FollowCandidates && !search.BudgetExhausted)
            {
                foreach (var edge in deferred) search.Relax(edge);
                search.Run(x => x.Kind != CallEdgeKind.Declared);
            }
            work += search.Spent;
            workLimited = search.BudgetExhausted;

            var reached = search.Discovered.Where(x => x != root).Select(x =>
            {
                var witness = search.PathTo(x).Select(e => new ReachStep(e.Site.Id, e.Target, e.Kind == CallEdgeKind.Candidate)).ToImmutableArray();
                return new ReachedCallable(x, search.Distance(x), witness, witness.Any(e => e.Candidate));
            });
            var (boundaries, depthLimited) = Boundaries(index, calls, search, charged, options);
            results.Add(new(root, [.. reached.OrderBy(x => x.Distance).ThenBy(x => index.Callable(x.Callable).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Callable.Value, StringComparer.Ordinal)],
                boundaries, depthLimited));
            if (workLimited) break;
        }
        return new(results.ToImmutable(), workLimited, work);
    }

    // Where traversal stopped, in the order callables were taken from the queue and, per callable, in call-site order.
    private static (ImmutableArray<ReachBoundary>, bool) Boundaries(ProgramIndex index, CallGraph calls,
        BreadthFirstSearch<CallableId, CallEdge> search, HashSet<CallableId> charged, ReachOptions options)
    {
        var boundaries = ImmutableArray.CreateBuilder<ReachBoundary>();
        var depthLimited = false;
        foreach (var node in search.Dequeued)
        {
            if (index.Callable(node).Body != BodyStatus.Source)
            {
                boundaries.Add(new(node, null, ReachBoundaryKind.MissingBody, DispatchKind.Direct, null, 0));
                continue;
            }
            var outgoing = index.Outgoing(node);
            if (search.Distance(node) >= options.MaxDepth)
            {
                if (!outgoing.IsEmpty) { depthLimited = true; boundaries.Add(new(node, null, ReachBoundaryKind.DepthLimit, DispatchKind.Direct, null, 0)); }
                continue;
            }
            // Only sites that were examined: a budget stop can leave a callable unexpanded or partially expanded.
            var edges = calls.Graph.OutEdges(node)[..search.ExaminedEdges(node)];
            var examined = edges.Select(x => x.Site.Id).ToHashSet();
            foreach (var site in outgoing)
            {
                if (calls.Unresolved(node).Contains(site)) { if (charged.Contains(node)) boundaries.Add(new(node, site.Id, ReachBoundaryKind.Unresolved, site.Dispatch, null, 0)); }
                else if (site.Dispatch != DispatchKind.Direct && examined.Contains(site.Id))
                    boundaries.Add(new(node, site.Id, ReachBoundaryKind.Dispatch, site.Dispatch, site.BoundTarget,
                        options.FollowCandidates ? edges.Count(x => x.Kind == CallEdgeKind.Candidate && x.Site.Id == site.Id) : 0));
            }
        }
        return (boundaries.ToImmutable(), depthLimited);
    }
}
