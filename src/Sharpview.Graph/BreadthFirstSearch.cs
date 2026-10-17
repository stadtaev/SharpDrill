using System.Collections.Immutable;

namespace Sharpview.Graph;

// Stateful breadth-first search producing a shortest-path tree. It can run in phases: sources and extra edges may be
// added between Run calls (e.g. explore one edge kind first, then continue from the tree with another), and a shared
// work budget bounds the edges examined. Visiting order follows node discovery and out-edge insertion order, so the
// first shortest path found for each node is deterministic.
public sealed class BreadthFirstSearch<TNode, TEdge> where TNode : notnull where TEdge : IEdge<TNode>
{
    private readonly DiGraph<TNode, TEdge> graph;
    private readonly Func<TNode, int, bool> expand;
    private readonly Func<TEdge, int> cost;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<TNode, (int Distance, TEdge? Parent, bool HasParent)> tree;
    private readonly Dictionary<TNode, int> examined;
    private readonly HashSet<TNode> expanded;
    private readonly List<TNode> discovered = [];
    private readonly List<TNode> dequeued = [];
    private readonly Queue<TNode> queue = new();

    // expand(node, distance) decides whether a dequeued node's out-edges are examined (e.g. depth limits).
    // cost(edge) is charged against the budget before the edge is examined; the default is 1 per edge.
    public BreadthFirstSearch(DiGraph<TNode, TEdge> graph, Func<TNode, int, bool>? expand = null, Func<TEdge, int>? cost = null,
        int budget = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        this.graph = graph;
        this.expand = expand ?? ((_, _) => true);
        this.cost = cost ?? (_ => 1);
        this.cancellationToken = cancellationToken;
        Budget = budget;
        tree = new(graph.Comparer); examined = new(graph.Comparer); expanded = new(graph.Comparer);
    }

    public int Budget { get; }
    public int Spent { get; private set; }
    public bool BudgetExhausted { get; private set; }
    // Nodes in discovery order (sources first), and in the order they were taken from the queue.
    public IReadOnlyList<TNode> Discovered => discovered;
    public IReadOnlyList<TNode> Dequeued => dequeued;

    public bool IsDiscovered(TNode node) => tree.ContainsKey(node);
    public int Distance(TNode node) => tree.TryGetValue(node, out var entry) ? entry.Distance : throw new KeyNotFoundException($"Node was not discovered: {node}");
    // True when every out-edge of the node was examined; a budget stop can leave a node partially expanded.
    public bool IsExpanded(TNode node) => expanded.Contains(node);
    // Number of the node's out-edges (in insertion order) that were examined.
    public int ExaminedEdges(TNode node) => examined.GetValueOrDefault(node);

    // The tree edges from a source to the node; empty for a source.
    public ImmutableArray<TEdge> PathTo(TNode node)
    {
        var path = new List<TEdge>();
        for (var current = node; tree.TryGetValue(current, out var entry) && entry.HasParent; current = entry.Parent!.Source) path.Add(entry.Parent!);
        if (!tree.ContainsKey(node)) throw new KeyNotFoundException($"Node was not discovered: {node}");
        path.Reverse();
        return [.. path];
    }

    public void AddSource(TNode node)
    {
        if (!graph.Contains(node)) throw new KeyNotFoundException($"Node is not in the graph: {node}");
        if (tree.TryAdd(node, (0, default, false))) { discovered.Add(node); queue.Enqueue(node); }
    }

    // Discovers the edge's target through it when the source is known and the target is not; returns whether it did.
    public bool Relax(TEdge edge)
    {
        if (!tree.TryGetValue(edge.Source, out var source)) throw new InvalidOperationException($"Edge source was not discovered: {edge.Source}");
        if (!tree.TryAdd(edge.Target, (source.Distance + 1, edge, true))) return false;
        discovered.Add(edge.Target); queue.Enqueue(edge.Target);
        return true;
    }

    // Consumes budget for work outside the graph's edges; false (and exhausted) when it does not fit.
    public bool Charge(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount > Budget - Spent) { BudgetExhausted = true; return false; }
        Spent += amount;
        return true;
    }

    // Expands queued nodes until the queue is empty or the budget is exhausted; follow(edge) decides which examined
    // edges are traversed.
    public void Run(Func<TEdge, bool>? follow = null)
    {
        follow ??= _ => true;
        while (!BudgetExhausted && queue.TryDequeue(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dequeued.Add(node);
            if (!expand(node, tree[node].Distance) || BudgetExhausted) continue;
            var edges = graph.OutEdges(node);
            var count = 0;
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Charge(cost(edge))) break;
                examined[node] = ++count;
                if (follow(edge)) Relax(edge);
            }
            if (count == edges.Length) expanded.Add(node);
        }
    }
}
