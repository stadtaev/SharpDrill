using SharpView.Graph;
using Xunit;

namespace SharpView.Tests;

public sealed class GraphTests
{
    private sealed record Edge(string Source, string Target, string Label = "") : IEdge<string>;
    private static DiGraph<string, Edge> Graph(params (string From, string To)[] edges)
    {
        var builder = DiGraph<string, Edge>.CreateBuilder();
        foreach (var (from, to) in edges) builder.AddEdge(new(from, to, $"{from}>{to}"));
        return builder.Build();
    }

    [Fact]
    public void Graph_keeps_insertion_order_parallel_edges_and_both_adjacency_directions()
    {
        var builder = DiGraph<string, Edge>.CreateBuilder().AddNode("Z").AddNode("Z");
        builder.AddEdge(new("A", "B", "first")).AddEdge(new("A", "B", "second")).AddEdge(new("C", "B")).AddEdge(new("A", "C"));
        var graph = builder.Build();
        Assert.Equal(["Z", "A", "B", "C"], graph.Nodes.AsEnumerable());
        Assert.Equal(4, graph.EdgeCount);
        Assert.Equal(["first", "second", ""], graph.OutEdges("A").Select(x => x.Label));
        Assert.Equal(["B", "C"], graph.Successors("A"));
        Assert.Equal(["A", "C"], graph.Predecessors("B"));
        Assert.Equal((3, 3), (graph.OutDegree("A"), graph.InDegree("B")));
        Assert.Empty(graph.OutEdges("Z"));
        Assert.False(graph.Contains("Missing"));
        Assert.Throws<KeyNotFoundException>(() => graph.OutEdges("Missing"));
    }

    [Fact]
    public void Where_and_subgraph_materialize_graphs_that_keep_order()
    {
        var graph = Graph(("A", "B"), ("B", "C"), ("A", "C"), ("C", "D"));
        var filtered = graph.Where(x => x.Target != "C");
        Assert.Equal(graph.Nodes.AsEnumerable(), filtered.Nodes.AsEnumerable());
        Assert.Equal(["A>B", "C>D"], filtered.Edges.Select(x => x.Label));
        var sub = graph.Subgraph(["C", "A", "B"]);
        Assert.Equal(["A", "B", "C"], sub.Nodes.AsEnumerable());
        Assert.Equal(["A>B", "B>C", "A>C"], sub.Edges.Select(x => x.Label));
        Assert.Throws<KeyNotFoundException>(() => graph.Subgraph(["Missing"]));
    }

    [Fact]
    public void Quotient_buckets_edges_by_group_and_key_and_keeps_unassigned_edges()
    {
        var graph = Graph(("api1", "app1"), ("api2", "app1"), ("app1", "infra"), ("api1", "lib"), ("lib", "app1"), ("app1", "app2"));
        string? Group(string node) => node == "lib" ? null : node.TrimEnd('1', '2');
        var quotient = graph.Quotient(Group, x => x.Source == x.Target ? "self" : "edge");
        Assert.Equal(["api>app:edge:2", "app>infra:edge:1", "app>app:edge:1"],
            quotient.Edges.Select(x => $"{x.From}>{x.To}:{x.Key}:{x.Edges.Length}"));
        Assert.Equal(["api1>lib", "lib>app1"], quotient.Unassigned.Select(x => x.Label));
    }

    [Fact]
    public void Quotient_keeps_isolated_groups_memberships_and_parallel_supporting_edges()
    {
        var builder = DiGraph<string, Edge>.CreateBuilder().AddNode("isolated").AddNode("ungrouped");
        builder.AddEdge(new("a1", "b", "first")).AddEdge(new("a2", "b", "second")).AddEdge(new("a1", "b", "parallel"));
        var quotient = builder.Build().Quotient(n => n == "ungrouped" ? null : n.TrimEnd('1', '2'), _ => "calls");
        Assert.Equal(["isolated", "a", "b"], quotient.Nodes.Select(n => n.Group));
        Assert.Equal(["a1", "a2"], Assert.Single(quotient.Nodes, n => n.Group == "a").Members.AsEnumerable());
        Assert.Equal(["isolated"], quotient.Nodes[0].Members.AsEnumerable());
        Assert.Equal(["first", "second", "parallel"], Assert.Single(quotient.Edges).Edges.Select(e => e.Label));
        Assert.Equal(["ungrouped"], quotient.UnassignedNodes.AsEnumerable());
    }

    [Fact]
    public void Strongly_connected_components_find_cycles_and_self_loops()
    {
        var graph = Graph(("A", "B"), ("B", "C"), ("C", "A"), ("C", "D"), ("D", "D"), ("E", "D"));
        var components = graph.StronglyConnectedComponents();
        Assert.Equal(["D", "A,B,C", "E"], components.Select(x => string.Join(",", x)));
        Assert.Equal([true, true, false], components.Select(graph.IsCyclic));
        // Deep chains do not overflow the stack: the algorithm is iterative.
        var builder = DiGraph<string, Edge>.CreateBuilder();
        for (var i = 0; i < 100_000; i++) builder.AddEdge(new($"n{i}", $"n{i + 1}"));
        Assert.Equal(100_001, builder.Build().StronglyConnectedComponents().Length);
    }
}
