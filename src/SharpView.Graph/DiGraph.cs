using System.Collections.Immutable;

namespace SharpView.Graph;

// An edge connects two nodes; edges carry their own data (evidence, kind, ...) so parallel edges stay distinct.
public interface IEdge<out TNode>
{
    TNode Source { get; }
    TNode Target { get; }
}

// Immutable directed multigraph with adjacency lists in both directions. Nodes, edges and every adjacency list keep
// insertion order, so algorithms over it are deterministic whenever the builder was fed in a deterministic order.
// Structure is immutable; generic node/edge payloads are not cloned or frozen. Node equality/hash codes and edge
// endpoints must remain stable for the lifetime of every graph containing them. Other mutable payload is shared.
public sealed class DiGraph<TNode, TEdge> where TNode : notnull where TEdge : IEdge<TNode>
{
    private readonly Dictionary<TNode, int> positions;
    private readonly ImmutableArray<TEdge>[] outgoing;
    private readonly ImmutableArray<TEdge>[] incoming;

    public ImmutableArray<TNode> Nodes { get; }
    public ImmutableArray<TEdge> Edges { get; }
    public IEqualityComparer<TNode> Comparer { get; }

    internal DiGraph(ImmutableArray<TNode> nodes, ImmutableArray<TEdge> edges, IEqualityComparer<TNode> comparer)
    {
        Nodes = nodes; Edges = edges; Comparer = comparer;
        positions = new Dictionary<TNode, int>(nodes.Length, comparer);
        for (var i = 0; i < nodes.Length; i++) positions.Add(nodes[i], i);
        var outLists = new List<TEdge>[nodes.Length];
        var inLists = new List<TEdge>[nodes.Length];
        foreach (var edge in edges)
        {
            (outLists[positions[edge.Source]] ??= []).Add(edge);
            (inLists[positions[edge.Target]] ??= []).Add(edge);
        }
        outgoing = [.. outLists.Select(x => x is null ? ImmutableArray<TEdge>.Empty : [.. x])];
        incoming = [.. inLists.Select(x => x is null ? ImmutableArray<TEdge>.Empty : [.. x])];
    }

    public static DiGraphBuilder<TNode, TEdge> CreateBuilder(IEqualityComparer<TNode>? comparer = null) => new(comparer);

    public int NodeCount => Nodes.Length;
    public int EdgeCount => Edges.Length;
    public bool Contains(TNode node) => positions.ContainsKey(node);

    // Edges in insertion order. Unknown nodes are an error, as in NetworkX.
    public ImmutableArray<TEdge> OutEdges(TNode node) => outgoing[Position(node)];
    public ImmutableArray<TEdge> InEdges(TNode node) => incoming[Position(node)];
    public int OutDegree(TNode node) => OutEdges(node).Length;
    public int InDegree(TNode node) => InEdges(node).Length;
    // Distinct neighbours in first-edge order.
    public IEnumerable<TNode> Successors(TNode node) => OutEdges(node).Select(x => x.Target).Distinct(Comparer);
    public IEnumerable<TNode> Predecessors(TNode node) => InEdges(node).Select(x => x.Source).Distinct(Comparer);

    // Materializes a new graph with the same nodes and matching edges, sharing payload objects.
    public DiGraph<TNode, TEdge> Where(Func<TEdge, bool> keep) => new(Nodes, [.. Edges.Where(keep)], Comparer);

    // Materializes the given nodes (in this graph's order) and their edges, sharing payload objects.
    public DiGraph<TNode, TEdge> Subgraph(IEnumerable<TNode> nodes)
    {
        var keep = new HashSet<TNode>(nodes, Comparer);
        foreach (var node in keep) Position(node);
        return new([.. Nodes.Where(keep.Contains)], [.. Edges.Where(x => keep.Contains(x.Source) && keep.Contains(x.Target))], Comparer);
    }

    private int Position(TNode node) =>
        positions.TryGetValue(node, out var position) ? position : throw new KeyNotFoundException($"Node is not in the graph: {node}");
}

public sealed class DiGraphBuilder<TNode, TEdge> where TNode : notnull where TEdge : IEdge<TNode>
{
    private readonly HashSet<TNode> seen;
    private readonly ImmutableArray<TNode>.Builder nodes = ImmutableArray.CreateBuilder<TNode>();
    private readonly ImmutableArray<TEdge>.Builder edges = ImmutableArray.CreateBuilder<TEdge>();
    private readonly IEqualityComparer<TNode> comparer;

    internal DiGraphBuilder(IEqualityComparer<TNode>? comparer)
    {
        this.comparer = comparer ?? EqualityComparer<TNode>.Default;
        seen = new(this.comparer);
    }

    // Adding a node twice keeps its first position.
    public DiGraphBuilder<TNode, TEdge> AddNode(TNode node)
    {
        if (seen.Add(node)) nodes.Add(node);
        return this;
    }

    // Missing endpoints are added as nodes; parallel edges are kept.
    public DiGraphBuilder<TNode, TEdge> AddEdge(TEdge edge)
    {
        AddNode(edge.Source); AddNode(edge.Target);
        edges.Add(edge);
        return this;
    }

    public DiGraph<TNode, TEdge> Build() => new(nodes.ToImmutable(), edges.ToImmutable(), comparer);
}
