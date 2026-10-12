using System.Collections.Immutable;

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
        var work = 0; var workLimited = false;
        foreach (var root in roots.Distinct().OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
        {
            var reached = new Dictionary<CallableId, ReachedCallable> { [root] = new(root, 0, [], false) };
            var boundaries = ImmutableArray.CreateBuilder<ReachBoundary>();
            var depthLimited = false;
            var queue = new Queue<CallableId>(); queue.Enqueue(root);
            void Visit(CallableId target, ReachedCallable from, ReachStep step, Queue<CallableId> pending)
            {
                if (reached.ContainsKey(target)) return;
                reached[target] = new(target, from.Distance + 1, from.Witness.Add(step), from.ViaCandidate || step.Candidate);
                pending.Enqueue(target);
            }
            // Candidate steps found during the direct-only phase wait until every definitely reachable callable is known,
            // so a callable reachable through direct calls is never labelled as candidate-only.
            var deferred = new List<(ReachedCallable From, ReachStep Step)>();
            var candidatePhase = false;
            while (!workLimited && (queue.Count > 0 || (!candidatePhase && deferred.Count > 0)))
            {
                if (queue.Count == 0)
                {
                    candidatePhase = true;
                    foreach (var (from, step) in deferred) Visit(step.Target, from, step, queue);
                    continue;
                }
                var current = queue.Dequeue();
                cancellationToken.ThrowIfCancellationRequested();
                var here = reached[current];
                var callable = index.Callable(current);
                if (callable.Body != BodyStatus.Source)
                {
                    boundaries.Add(new(current, null, ReachBoundaryKind.MissingBody, DispatchKind.Direct, null, 0));
                    continue;
                }
                var outgoing = index.Outgoing(current);
                if (here.Distance >= options.MaxDepth)
                {
                    if (!outgoing.IsEmpty) { depthLimited = true; boundaries.Add(new(current, null, ReachBoundaryKind.DepthLimit, DispatchKind.Direct, null, 0)); }
                    continue;
                }
                foreach (var site in outgoing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (work >= options.MaxExpansions) { workLimited = true; break; }
                    work++;
                    if (site.BoundTarget is not { } target || !index.ContainsCallable(target))
                    {
                        boundaries.Add(new(current, site.Id, ReachBoundaryKind.Unresolved, site.Dispatch, null, 0));
                        continue;
                    }
                    if (site.Dispatch == DispatchKind.Direct) { Visit(target, here, new(site.Id, target, false), queue); continue; }
                    var candidates = options.FollowCandidates ? Candidates(index, target) : [];
                    boundaries.Add(new(current, site.Id, ReachBoundaryKind.Dispatch, site.Dispatch, target, candidates.Length));
                    foreach (var candidate in candidates)
                        if (candidatePhase) Visit(candidate, here, new(site.Id, candidate, true), queue);
                        else deferred.Add((here, new(site.Id, candidate, true)));
                }
            }
            results.Add(new(root, [.. reached.Values.Where(x => x.Callable != root)
                .OrderBy(x => x.Distance).ThenBy(x => index.Callable(x.Callable).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Callable.Value, StringComparer.Ordinal)],
                boundaries.ToImmutable(), depthLimited));
            if (workLimited) break;
        }
        return new(results.ToImmutable(), workLimited, work);
    }

    // Transitive implementation candidates of a declared target, in ID order. A declared target with its own source
    // body (a virtual method) is itself a candidate.
    private static ImmutableArray<CallableId> Candidates(ProgramIndex index, CallableId declared)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        if (index.Callable(declared).Body == BodyStatus.Source) result.Add(declared.Value);
        var seen = new HashSet<CallableId> { declared };
        var pending = new Queue<CallableId>(); pending.Enqueue(declared);
        while (pending.TryDequeue(out var current))
            foreach (var implementation in index.Implementations(current))
                if (seen.Add(implementation.Implementation)) { result.Add(implementation.Implementation.Value); pending.Enqueue(implementation.Implementation); }
        return [.. result.Select(x => new CallableId(x))];
    }
}
