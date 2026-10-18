using System.Collections.Immutable;

namespace SharpView.Graph;

// Stateful breadth-first search producing a shortest-path tree. It can run in phases: sources and extra edges may be
// added between Run calls (e.g. explore one edge kind first, then continue from the tree with another), and a shared
// work budget bounds the edges examined. Visiting order follows node discovery and out-edge insertion order, so the
// first shortest path found for each node is deterministic. When the budget stops an improvement phase,
// distances and witnesses retain valid admitted paths; optimality is guaranteed only after the queue is drained.
public sealed class BreadthFirstSearch<TNode, TEdge> where TNode : notnull where TEdge : IEdge<TNode>
{
    private readonly DiGraph<TNode, TEdge> graph;
    private readonly Func<TNode, int, bool> expand;
    private readonly Func<TEdge, int> cost;
    private readonly CancellationToken cancellationToken;
    // Parent links point at immutable path versions. A budget stop during a distance improvement must not
    // silently rewrite descendants' witnesses before those descendants can be relaxed themselves.
    private sealed record Entry(int Distance, TEdge? Parent, Entry? Previous)
    {
        public bool HasParent => Previous is not null;
    }
    private readonly Dictionary<TNode, Entry> tree;
    private readonly Dictionary<TNode, int> examined;
    private readonly HashSet<TNode> expanded;
    private readonly List<TNode> discovered = [];
    private readonly List<TNode> dequeued = [];
    private readonly Queue<(TNode Node, int Distance)> queue = new();

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
        if (!tree.TryGetValue(node, out var entry)) throw new KeyNotFoundException($"Node was not discovered: {node}");
        while (entry.HasParent) { path.Add(entry.Parent!); entry = entry.Previous!; }
        path.Reverse();
        return [.. path];
    }

    public void AddSource(TNode node)
    {
        if (!graph.Contains(node)) throw new KeyNotFoundException($"Node is not in the graph: {node}");
        cancellationToken.ThrowIfCancellationRequested();
        if (tree.TryGetValue(node, out var previous) && previous.Distance == 0) return;
        if (!tree.ContainsKey(node)) discovered.Add(node);
        tree[node] = new(0, default, null);
        Reopen(node, 0);
    }

    // Improves the target through a known source, reopening it when its distance decreases; equal paths keep the first parent.
    public bool Relax(TEdge edge)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!graph.Contains(edge.Source)) throw new KeyNotFoundException($"Node is not in the graph: {edge.Source}");
        if (!graph.Contains(edge.Target)) throw new KeyNotFoundException($"Node is not in the graph: {edge.Target}");
        if (!tree.TryGetValue(edge.Source, out var source)) throw new InvalidOperationException($"Edge source was not discovered: {edge.Source}");
        var distance = source.Distance + 1;
        if (tree.TryGetValue(edge.Target, out var target) && target.Distance <= distance) return false;
        if (!tree.ContainsKey(edge.Target)) discovered.Add(edge.Target);
        tree[edge.Target] = new(distance, edge, source);
        Reopen(edge.Target, distance);
        return true;
    }

    public bool TryGetParent(TNode node, out TEdge parent)
    {
        if (tree.TryGetValue(node, out var entry) && entry.HasParent) { parent = entry.Parent!; return true; }
        parent = default!;
        return false;
    }

    // Compute a path property once per retained parent entry, without allocating complete paths. Previous
    // path versions remain valid when a budget stops before an improved ancestor can reopen descendants.
    public IReadOnlyDictionary<TNode, TValue> FoldPaths<TValue>(TValue sourceValue, Func<TValue, TEdge, TValue> project)
    {
        var values = new Dictionary<Entry, TValue>(ReferenceEqualityComparer.Instance);
        var result = new Dictionary<TNode, TValue>(graph.Comparer);
        var pending = new Stack<Entry>();
        foreach (var node in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = tree[node];
            while (!values.ContainsKey(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!current.HasParent) { values[current] = sourceValue; break; }
                pending.Push(current); current = current.Previous!;
            }
            while (pending.TryPop(out var entry)) values[entry] = project(values[entry.Previous!], entry.Parent!);
            result[node] = values[tree[node]];
        }
        return result;
    }

    private void Reopen(TNode node, int distance)
    {
        expanded.Remove(node);
        examined.Remove(node);
        queue.Enqueue((node, distance));
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
        while (!BudgetExhausted && queue.TryDequeue(out var queued))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, distance) = queued;
            if (tree[node].Distance != distance) continue;
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
