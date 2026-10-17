using System.Collections.Immutable;

namespace Sharpview.Graph;

// Edges between two groups that share a key, in edge order.
public sealed record QuotientEdge<TGroup, TKey, TEdge>(TGroup From, TGroup To, TKey Key, ImmutableArray<TEdge> Edges);
// Unassigned holds edges with at least one endpoint outside every group, so callers can count rather than lose them.
public record Quotient<TGroup, TKey, TEdge>(ImmutableArray<QuotientEdge<TGroup, TKey, TEdge>> Edges, ImmutableArray<TEdge> Unassigned);

// Every assigned group is retained, including groups containing only isolated nodes. Members keep graph order.
public sealed record QuotientNode<TNode, TGroup>(TGroup Group, ImmutableArray<TNode> Members);
public sealed record Quotient<TNode, TGroup, TKey, TEdge>(
    ImmutableArray<QuotientEdge<TGroup, TKey, TEdge>> Edges, ImmutableArray<TEdge> Unassigned,
    ImmutableArray<QuotientNode<TNode, TGroup>> Nodes, ImmutableArray<TNode> UnassignedNodes)
    : Quotient<TGroup, TKey, TEdge>(Edges, Unassigned);

public static class GraphAlgorithms
{
    // Nodes reachable from the sources (sources included) in breadth-first discovery order.
    public static ImmutableArray<TNode> ReachableFrom<TNode, TEdge>(this DiGraph<TNode, TEdge> graph, IEnumerable<TNode> sources,
        Func<TEdge, bool>? follow = null, CancellationToken cancellationToken = default) where TNode : notnull where TEdge : IEdge<TNode>
    {
        var search = new BreadthFirstSearch<TNode, TEdge>(graph, cancellationToken: cancellationToken);
        foreach (var source in sources) search.AddSource(source);
        search.Run(follow);
        return [.. search.Discovered];
    }

    // Collapses nodes into groups (e.g. layers or projects) and buckets edges by (from group, to group, key).
    // Buckets appear in first-edge order; a null group means "not in any group" and sends the edge to Unassigned.
    public static Quotient<TNode, TGroup, TKey, TEdge> Quotient<TNode, TEdge, TGroup, TKey>(this DiGraph<TNode, TEdge> graph,
        Func<TNode, TGroup?> groupOf, Func<TEdge, TKey> keyOf, CancellationToken cancellationToken = default)
        where TNode : notnull where TEdge : IEdge<TNode> where TGroup : class where TKey : notnull
    {
        var groups = new Dictionary<TNode, TGroup?>(graph.Comparer);
        var memberships = new Dictionary<TGroup, ImmutableArray<TNode>.Builder>();
        var groupOrder = new List<TGroup>();
        var unassignedNodes = ImmutableArray.CreateBuilder<TNode>();
        foreach (var node in graph.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[node] = groupOf(node);
            if (group is null) { unassignedNodes.Add(node); continue; }
            if (!memberships.TryGetValue(group, out var members))
            { memberships[group] = members = ImmutableArray.CreateBuilder<TNode>(); groupOrder.Add(group); }
            members.Add(node);
        }
        var buckets = new Dictionary<(TGroup, TGroup, TKey), ImmutableArray<TEdge>.Builder>();
        var order = new List<(TGroup From, TGroup To, TKey Key)>();
        var unassigned = ImmutableArray.CreateBuilder<TEdge>();
        foreach (var edge in graph.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (groups[edge.Source] is not { } from || groups[edge.Target] is not { } to) { unassigned.Add(edge); continue; }
            var key = (from, to, keyOf(edge));
            if (!buckets.TryGetValue(key, out var bucket)) { buckets[key] = bucket = ImmutableArray.CreateBuilder<TEdge>(); order.Add(key); }
            bucket.Add(edge);
        }
        return new([.. order.Select(x => new QuotientEdge<TGroup, TKey, TEdge>(x.From, x.To, x.Key, buckets[x].ToImmutable()))], unassigned.ToImmutable(),
            [.. groupOrder.Select(group => new QuotientNode<TNode, TGroup>(group, memberships[group].ToImmutable()))], unassignedNodes.ToImmutable());
    }

    // Strongly connected components (Tarjan, iterative). Components come in reverse topological order of the
    // condensation; nodes inside a component keep graph order. A component of one node without a self-loop is acyclic.
    public static ImmutableArray<ImmutableArray<TNode>> StronglyConnectedComponents<TNode, TEdge>(this DiGraph<TNode, TEdge> graph,
        CancellationToken cancellationToken = default) where TNode : notnull where TEdge : IEdge<TNode>
    {
        var position = new Dictionary<TNode, int>(graph.Comparer);
        for (var i = 0; i < graph.Nodes.Length; i++) position[graph.Nodes[i]] = i;
        var index = new int[graph.NodeCount]; var low = new int[graph.NodeCount]; var onStack = new bool[graph.NodeCount];
        Array.Fill(index, -1);
        var stack = new Stack<int>(); var components = ImmutableArray.CreateBuilder<ImmutableArray<TNode>>();
        var counter = 0;
        for (var start = 0; start < graph.NodeCount; start++)
        {
            if (index[start] >= 0) continue;
            var work = new Stack<(int Node, int Edge)>(); work.Push((start, 0));
            while (work.TryPop(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (v, e) = frame;
                if (e == 0) { index[v] = low[v] = counter++; stack.Push(v); onStack[v] = true; }
                var edges = graph.OutEdges(graph.Nodes[v]);
                if (e < edges.Length)
                {
                    work.Push((v, e + 1));
                    var w = position[edges[e].Target];
                    if (index[w] < 0) work.Push((w, 0));
                    else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                if (low[v] == index[v])
                {
                    var members = new List<int>();
                    int w;
                    do { w = stack.Pop(); onStack[w] = false; members.Add(w); } while (w != v);
                    members.Sort();
                    components.Add([.. members.Select(x => graph.Nodes[x])]);
                }
                if (work.TryPeek(out var parent)) low[parent.Node] = Math.Min(low[parent.Node], low[v]);
            }
        }
        return components.ToImmutable();
    }

    // True when the component is a cycle: several nodes, or one node with an edge to itself.
    public static bool IsCyclic<TNode, TEdge>(this DiGraph<TNode, TEdge> graph, ImmutableArray<TNode> component)
        where TNode : notnull where TEdge : IEdge<TNode> =>
        component.Length > 1 || component.Length == 1 && graph.OutEdges(component[0]).Any(x => graph.Comparer.Equals(x.Target, component[0]));
}
