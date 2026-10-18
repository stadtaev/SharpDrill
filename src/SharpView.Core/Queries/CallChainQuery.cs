using System.Collections.Immutable;

namespace SharpView.Core;

public enum ChainEnd { Leaf, Metadata, NoBody, DispatchBoundary, UnknownTarget, Cycle, DepthLimit }
public sealed record ChainOptions(int MaxDepth = 20, int MaxChains = 1000, int MaxExpansions = 100000)
{
    public void Validate()
    {
        if (MaxDepth < 0 || MaxChains < 1 || MaxExpansions < 1) throw new ArgumentException("Depth must be nonnegative; work and output limits must be positive.");
    }
}
// One step per distinct (target, dispatch) relationship; unresolved sites stay separate. EvidenceSites are in ID order.
public sealed record ChainStep(CallableId Caller, CallableId? Target, string TargetDisplay,
    DispatchKind Dispatch, ImmutableArray<CallSiteId> EvidenceSites);
public sealed record CallChain(CallableId Root, ImmutableArray<ChainStep> Steps, ChainEnd End);
public sealed record ChainSearchResult(ImmutableArray<CallChain> Chains, bool OutputLimitReached, bool WorkLimitReached, int ExpandedSites);

public static class CallChainQuery
{
    // Frames carry independent path state. The explicit stack avoids recursion at user-supplied depths.
    public static ChainSearchResult Enumerate(ProgramSnapshot snapshot, ImmutableArray<CallableId> roots,
        ChainOptions options, CancellationToken cancellationToken = default) => Enumerate(new ProgramIndex(snapshot), roots, options, cancellationToken);
    public static ChainSearchResult Enumerate(ProgramIndex program, ImmutableArray<CallableId> roots,
        ChainOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var calls = program.GetCallGraph(cancellationToken);
        var steps = new Dictionary<CallableId, ImmutableArray<ChainStep>>();
        ImmutableArray<ChainStep> Steps(CallableId caller) =>
            steps.TryGetValue(caller, out var cached) ? cached : steps[caller] = Group(calls, caller);
        foreach (var root in roots) if (!program.ContainsCallable(root)) throw new ArgumentException($"Unknown root ID: {root.Value}");
        var output = ImmutableArray.CreateBuilder<CallChain>();
        var work = 0; var outputLimited = false; var workLimited = false;
        var stack = new Stack<(CallableId Root, CallableId Current, ImmutableArray<ChainStep> Path, ImmutableHashSet<CallableId> Seen, ChainEnd? End)>();
        foreach (var root in roots.Distinct().OrderByDescending(x => program.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenByDescending(x => x.Value, StringComparer.Ordinal))
            stack.Push((root, root, [], ImmutableHashSet.Create(root), null));
        while (stack.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = frame.End;
            // Terminal frames (e.g. an unresolved target) may name a callable outside the graph; never expand them.
            var edges = end is null ? Steps(frame.Current) : [];
            if (end is null && program.Callable(frame.Current).Body != BodyStatus.Source)
                end = program.Callable(frame.Current).Body == BodyStatus.MetadataOnly ? ChainEnd.Metadata : ChainEnd.NoBody;
            if (end is null && edges.IsEmpty) end = ChainEnd.Leaf;
            if (end is null && frame.Path.Length >= options.MaxDepth) end = ChainEnd.DepthLimit;
            if (end is not null)
            {
                if (output.Count >= options.MaxChains) { outputLimited = true; break; }
                output.Add(new(frame.Root, frame.Path, end.Value)); continue;
            }
            // Charge actual evidence sites, including duplicates, not just collapsed relationships.
            var pending = new List<(ChainStep Step, ChainEnd? End)>();
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edge.EvidenceSites.Length > options.MaxExpansions - work) { workLimited = true; break; }
                work += edge.EvidenceSites.Length;
                ChainEnd? terminal = edge.Target is null || !program.ContainsCallable(edge.Target.Value) ? ChainEnd.UnknownTarget
                    : edge.Dispatch != DispatchKind.Direct ? ChainEnd.DispatchBoundary
                    : frame.Seen.Contains(edge.Target.Value) ? ChainEnd.Cycle : null;
                pending.Add((edge, terminal));
            }
            // Retain already examined paths when the work budget stops this expansion.
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var (edge, terminal) = pending[i];
                var target = edge.Target ?? frame.Current;
                stack.Push((frame.Root, target, frame.Path.Add(edge), frame.Seen.Add(target), terminal));
            }
            if (workLimited)
            {
                // Do not descend further. Emit evaluated terminal branches only.
                while (stack.TryPop(out var stopped))
                {
                    if (stopped.End is null) continue;
                    if (output.Count >= options.MaxChains) { outputLimited = true; break; }
                    output.Add(new(stopped.Root, stopped.Path, stopped.End.Value));
                }
                break;
            }
        }
        return new(output.ToImmutable(), outputLimited, workLimited, work);
    }

    // Call-site edges (candidates excluded: paths keep declared bindings) and unresolved sites of one caller. Grouping
    // collapses repeated relationships; the explicit order makes the result independent of input order.
    private static ImmutableArray<ChainStep> Group(CallGraph calls, CallableId caller) =>
        [.. calls.Graph.OutEdges(caller).Where(x => x.Kind != CallEdgeKind.Candidate).Select(x => x.Site).Concat(calls.Unresolved(caller))
            .GroupBy(x => (x.BoundTarget, x.Dispatch, Unresolved: x.BoundTarget is null ? x.Id.Value : ""))
            .Select(e => new ChainStep(caller, e.First().BoundTarget, e.First().TargetDisplay, e.First().Dispatch,
                [.. e.OrderBy(x => x.Id.Value, StringComparer.Ordinal).Select(x => x.Id)]))
            .OrderBy(x => x.TargetDisplay, StringComparer.Ordinal).ThenBy(x => x.EvidenceSites[0].Value, StringComparer.Ordinal)];
}
public static class DependencyQueries
{
    public static IEnumerable<DependencyFact> SignatureUses(ProgramSnapshot snapshot, CallableId callable) =>
        snapshot.Dependencies.Where(x => x.OwningCallable == callable && x.Kind == DependencyKind.Signature);
}
