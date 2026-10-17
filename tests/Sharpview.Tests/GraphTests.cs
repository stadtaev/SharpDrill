using Sharpview.Graph;
using Xunit;

namespace Sharpview.Tests;

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
    public void Where_and_subgraph_are_views_that_keep_order()
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
    public void Breadth_first_search_builds_a_deterministic_shortest_path_tree()
    {
        var graph = Graph(("R", "L"), ("R", "X"), ("L", "S"), ("X", "S"), ("S", "R"));
        var search = new BreadthFirstSearch<string, Edge>(graph);
        search.AddSource("R");
        search.Run();
        Assert.Equal(["R", "L", "X", "S"], search.Discovered);
        Assert.Equal(2, search.Distance("S"));
        Assert.Equal(["R>L", "L>S"], search.PathTo("S").Select(x => x.Label));
        Assert.Empty(search.PathTo("R"));
        Assert.Equal(["R", "L", "X", "S"], graph.ReachableFrom(["R"]).AsEnumerable());
        Assert.Equal(["L", "S", "R", "X"], graph.ReachableFrom(["L"]).AsEnumerable());
    }

    [Fact]
    public void Search_runs_in_phases_with_expand_rules_costs_and_a_budget()
    {
        var graph = Graph(("R", "A"), ("A", "B"), ("R", "C"));
        var phased = new BreadthFirstSearch<string, Edge>(graph);
        phased.AddSource("R");
        phased.Run(x => x.Target != "C");
        Assert.Equal(["R", "A", "B"], phased.Discovered);
        Assert.True(phased.Relax(graph.OutEdges("R")[1]));
        Assert.False(phased.Relax(graph.OutEdges("R")[0]));
        phased.Run();
        Assert.Equal(["R", "A", "B", "C"], phased.Discovered);

        var shallow = new BreadthFirstSearch<string, Edge>(graph, expand: (_, distance) => distance < 1);
        shallow.AddSource("R"); shallow.Run();
        Assert.Equal(["R", "A", "C"], shallow.Discovered);
        Assert.False(shallow.IsExpanded("A"));

        var budget = new BreadthFirstSearch<string, Edge>(graph, cost: x => x.Target == "C" ? 0 : 1, budget: 1);
        budget.AddSource("R"); budget.Run();
        Assert.True(budget.BudgetExhausted);
        Assert.Equal(1, budget.Spent);
        Assert.Equal(["R", "A", "C"], budget.Discovered);                    // the free edge still fits after the budget is spent
        Assert.True(budget.IsExpanded("R"));
        Assert.Equal(0, budget.ExaminedEdges("A"));
        Assert.Throws<InvalidOperationException>(() => new BreadthFirstSearch<string, Edge>(graph).Relax(graph.Edges[0]));
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
