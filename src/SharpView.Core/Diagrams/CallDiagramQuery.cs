using System.Collections.Immutable;

namespace SharpView.Core;

public sealed record DiagramOptions(bool FollowCandidates = false, int MaxDepth = 20, int MaxNodes = 200,
    int MaxEdges = 1000, int MaxExpansions = 100000)
{
    public void Validate()
    {
        if (MaxDepth < 0 || MaxNodes < 1 || MaxEdges < 1 || MaxExpansions < 1)
            throw new ArgumentException("Depth must be nonnegative; node, edge and work limits must be positive.");
    }
}
public enum DiagramLimit { Depth, Nodes, Edges, Work }
public enum DiagramBoundaryKind { Dispatch, Unresolved, MissingBody }
public sealed record DiagramNode(string Id, CallableId? Callable, string Label, string ProjectId, string Project,
    BodyStatus? Body, bool IsRoot);
public sealed record DiagramEdge(string Source, string Target, CallSiteId Site, CallEdgeKind? Kind, SourceEvidence Evidence);
public sealed record DiagramBoundary(string Node, CallSiteId? Site, DiagramBoundaryKind Kind);
public sealed record CallDiagram(string Root, ImmutableArray<DiagramNode> Nodes, ImmutableArray<DiagramEdge> Edges,
    ImmutableArray<DiagramBoundary> Boundaries, ImmutableArray<DiagramLimit> Limits, int ExaminedSites,
    bool FollowCandidates, ImmutableArray<AnalysisGap> Gaps)
{
    public bool IsComplete => Boundaries.IsEmpty && Limits.IsEmpty && Gaps.IsEmpty;
}

// A bounded subgraph, not an enumeration of execution paths. Shared callees and recursive edges are retained.
// The work limit bounds examined sites in this projection; snapshot loading and whole-graph construction precede it.
public static class CallDiagramQuery
{
    public static CallDiagram Build(ProgramIndex index, CallableId root, DiagramOptions options, CancellationToken token = default)
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        if (!index.ContainsCallable(root)) throw new ArgumentException($"Unknown root ID: {root.Value}");
        var calls = index.GetCallGraph(token);
        var nodes = new List<DiagramNode>();
        var edges = ImmutableArray.CreateBuilder<DiagramEdge>();
        var boundaries = ImmutableArray.CreateBuilder<DiagramBoundary>();
        var limits = new HashSet<DiagramLimit>();
        var ids = new Dictionary<CallableId, string>();
        var queued = new HashSet<CallableId>();
        var pending = new Queue<(CallableId Method, int Depth)>();
        var examined = 0;
        string AddMethod(CallableId method)
        {
            if (ids.TryGetValue(method, out var existing)) return existing;
            var fact = index.Callable(method);
            var id = "n" + nodes.Count;
            ids.Add(method, id);
            nodes.Add(new(id, method, fact.DisplaySignature, fact.Compilation.Value, index.ProjectName(fact.Compilation), fact.Body, method == root));
            return id;
        }
        void Queue(CallableId method, int depth)
        {
            if (queued.Add(method)) pending.Enqueue((method, depth));
        }
        var rootId = AddMethod(root); Queue(root, 0);
        var stop = false;
        while (!stop && pending.TryDequeue(out var item))
        {
            token.ThrowIfCancellationRequested();
            var (method, depth) = item;
            var source = ids[method];
            if (!index.Callable(method).HasBody)
            {
                boundaries.Add(new(source, null, DiagramBoundaryKind.MissingBody));
                continue;
            }
            var outgoing = index.Outgoing(method);
            if (depth >= options.MaxDepth)
            {
                if (!outgoing.IsEmpty) limits.Add(DiagramLimit.Depth);
                continue;
            }
            // Edges are contiguous per site, in the same order as Outgoing. No per-site rescans or path copies.
            var methodEdges = calls.Graph.OutEdges(method);
            var edgePosition = 0;
            foreach (var site in outgoing)
            {
                token.ThrowIfCancellationRequested();
                if (examined == options.MaxExpansions) { limits.Add(DiagramLimit.Work); stop = true; break; }
                examined++;
                if (site.BoundTarget is not { } target || !index.ContainsCallable(target))
                {
                    if (!CanAddEdge(null)) break;
                    var id = "n" + nodes.Count;
                    var owner = index.Callable(method);
                    nodes.Add(new(id, null, "Unresolved: " + site.TargetDisplay, owner.Compilation.Value, index.ProjectName(owner.Compilation), null, false));
                    edges.Add(new(source, id, site.Id, null, site.Evidence));
                    boundaries.Add(new(source, site.Id, DiagramBoundaryKind.Unresolved));
                    continue;
                }
                while (edgePosition < methodEdges.Length && methodEdges[edgePosition].Site.Id == site.Id)
                {
                    token.ThrowIfCancellationRequested();
                    var edge = methodEdges[edgePosition++];
                    if (edge.Kind == CallEdgeKind.Candidate && !options.FollowCandidates) continue;
                    if (!CanAddEdge(edge.Target)) break;
                    var destination = AddMethod(edge.Target);
                    edges.Add(new(source, destination, site.Id, edge.Kind, site.Evidence));
                    if (edge.Kind == CallEdgeKind.Declared) boundaries.Add(new(source, site.Id, DiagramBoundaryKind.Dispatch));
                    else Queue(edge.Target, depth + 1);
                }
                if (stop) break;
            }
        }
        var gaps = index.Snapshot.Gaps.Where(x => x.Owner is null || ids.ContainsKey(x.Owner.Value)).ToImmutableArray();
        return new(rootId, [.. nodes], edges.ToImmutable(), boundaries.ToImmutable(), [.. limits.Order()], examined, options.FollowCandidates, gaps);

        bool CanAddEdge(CallableId? target)
        {
            if (edges.Count == options.MaxEdges) { limits.Add(DiagramLimit.Edges); stop = true; return false; }
            if ((target is null || !ids.ContainsKey(target.Value)) && nodes.Count == options.MaxNodes)
            { limits.Add(DiagramLimit.Nodes); stop = true; return false; }
            return true;
        }
    }
}
