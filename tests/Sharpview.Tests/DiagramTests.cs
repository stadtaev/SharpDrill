using Sharpview.Core;
using Sharpview.Export.Mermaid;
using Xunit;

namespace Sharpview.Tests;

public sealed class DiagramTests
{
    private static readonly CompilationId Project = new("project");
    private static readonly SourceEvidence Evidence = new("A|file.cs", "hash", 0, 1, 2, 3, false);
    private static readonly TypeUse Void = new(null, "void", NullabilityKind.NotAnnotated, []);
    private static CallableFact Method(string id, BodyStatus body = BodyStatus.Source) =>
        new(new(id), Project, null, null, CallableKind.Method, id + "()", "Public", Void, [], [], body, []);
    private static CallSiteFact Call(string id, string from, string? to, DispatchKind dispatch = DispatchKind.Direct) =>
        new(new(id), new(from), to is null ? null : new CallableId(to), to ?? "unknown", dispatch, [], Evidence, false, []);
    private static ProgramIndex Index(CallableFact[] methods, CallSiteFact[] calls, ImplementationFact[]? implementations = null) =>
        new(new() { Callables = [.. methods], CallSites = [.. calls], Implementations = [.. implementations ?? []],
            Compilations = [new(Project, "/src/Example.csproj", "Example", "net10.0", "Debug", "default", "enabled", [], [])] });

    [Fact]
    public void Projection_preserves_shared_nodes_cycles_and_parallel_call_sites()
    {
        var index = Index([Method("Root"), Method("A"), Method("B"), Method("Shared")],
            [Call("ra", "Root", "A"), Call("ra2", "Root", "A"), Call("rb", "Root", "B"),
             Call("as", "A", "Shared"), Call("bs", "B", "Shared"), Call("sr", "Shared", "Root")]);
        var diagram = CallDiagramQuery.Build(index, new("Root"), new());
        Assert.Equal(4, diagram.Nodes.Length);
        Assert.Equal(6, diagram.Edges.Length);
        Assert.Single(diagram.Nodes, x => x.Callable == new CallableId("Shared"));
        Assert.Equal(2, diagram.Edges.Count(x => x.Source == diagram.Root && x.Target == diagram.Nodes.Single(n => n.Callable == new CallableId("A")).Id));
        Assert.Contains(diagram.Edges, x => x.Target == diagram.Root);
        Assert.True(diagram.IsComplete);
        var markdown = MermaidExporter.Markdown(diagram);
        Assert.Contains("subgraph p0 [\"Example\"]", markdown);
        Assert.Contains("A&#124;file.cs:2:3", markdown);
        Assert.Equal(markdown, MermaidExporter.Markdown(CallDiagramQuery.Build(index, new("Root"), new())));
    }

    [Fact]
    public void Dispatch_unresolved_and_missing_bodies_remain_visible()
    {
        var index = Index([Method("Root"), Method("I", BodyStatus.NoBody), Method("Impl"), Method("External", BodyStatus.MetadataOnly)],
            [Call("i", "Root", "I", DispatchKind.Interface), Call("u", "Root", null), Call("e", "Root", "External")],
            [new(new("I"), new("Impl"), ImplementationKind.Interface)]);
        var declared = CallDiagramQuery.Build(index, new("Root"), new());
        Assert.DoesNotContain(declared.Nodes, x => x.Callable == new CallableId("Impl"));
        Assert.Contains(declared.Edges, x => x.Kind == CallEdgeKind.Declared);
        Assert.Contains(declared.Nodes, x => x.Callable is null);
        Assert.Contains(declared.Boundaries, x => x.Kind == DiagramBoundaryKind.MissingBody);
        Assert.False(declared.IsComplete);
        var candidates = CallDiagramQuery.Build(index, new("Root"), new(FollowCandidates: true));
        Assert.Contains(candidates.Edges, x => x.Kind == CallEdgeKind.Candidate);
        Assert.Contains(candidates.Nodes, x => x.Callable == new CallableId("Impl"));
        Assert.False(candidates.IsComplete);
    }

    [Theory]
    [InlineData(0, 20, 20, 20, DiagramLimit.Depth)]
    [InlineData(20, 1, 20, 20, DiagramLimit.Nodes)]
    [InlineData(20, 20, 1, 20, DiagramLimit.Edges)]
    [InlineData(20, 20, 20, 1, DiagramLimit.Work)]
    public void Limits_are_bounded_and_visible(int depth, int nodes, int edges, int work, DiagramLimit expected)
    {
        var index = Index([Method("Root"), Method("A"), Method("B")], [Call("a", "Root", "A"), Call("b", "Root", "B")]);
        var diagram = CallDiagramQuery.Build(index, new("Root"), new(MaxDepth: depth, MaxNodes: nodes, MaxEdges: edges, MaxExpansions: work));
        Assert.Contains(expected, diagram.Limits);
        Assert.InRange(diagram.Nodes.Length, 1, nodes);
        Assert.InRange(diagram.Edges.Length, 0, edges);
        Assert.InRange(diagram.ExaminedSites, 0, work);
        Assert.False(diagram.IsComplete);
        Assert.Contains("Truncated", MermaidExporter.Markdown(diagram));
    }

    [Fact]
    public void Library_generation_preserves_edge_identity_root_style_and_markdown_safe_labels()
    {
        const string name = "end[\"<T> & `x`\u2028\u2029]```mermaid";
        var index = Index([Method(name), Method("Shared")],
            [Call("one", name, "Shared"), Call("two", name, "Shared"), Call("back", "Shared", name)]);
        var markdown = MermaidExporter.Markdown(CallDiagramQuery.Build(index, new(name), new()));
        var graph = markdown.Split("```mermaid\n")[1].Split("\n```")[0];
        Assert.Contains("&quot;", graph);
        Assert.Contains("&lt;T&gt;", graph);
        Assert.DoesNotContain('\u2028', graph); Assert.DoesNotContain('\u2029', graph);
        Assert.DoesNotContain("```", graph);
        Assert.Contains("e1: direct", graph); Assert.Contains("e2: direct", graph); Assert.Contains("e3: direct", graph);
        Assert.Contains("classDef root fill:#dbeafe,stroke:#2563eb,stroke-width:3px", graph);
        Assert.Contains("class n0 root", graph);
        Assert.Equal(markdown, MermaidExporter.Markdown(CallDiagramQuery.Build(index, new(name), new())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Library_generation_retains_blank_display_labels(string label)
    {
        var diagram = CallDiagramQuery.Build(Index([Method("Root")], []), new("Root"), new());
        diagram = diagram with { Nodes = [diagram.Nodes[0] with { Label = label, Project = label }] };
        var markdown = MermaidExporter.Markdown(diagram);
        Assert.Contains($"subgraph p0 [\"{label}\"]", markdown);
        Assert.Contains($"n0[\"{label}\"]", markdown);
    }

    [Fact]
    public void Escapes_source_labels_and_rejects_invalid_roots_options_and_cancellation()
    {
        const string name = "end[\"<T> & `x`\n]";
        var index = Index([Method(name)], []);
        var markdown = MermaidExporter.Markdown(CallDiagramQuery.Build(index, new(name), new()));
        Assert.Contains("&quot;", markdown);
        Assert.DoesNotContain("<T>", markdown);
        Assert.Equal(2, markdown.Split("```").Length - 1);
        Assert.Throws<ArgumentException>(() => CallDiagramQuery.Build(index, new("absent"), new()));
        Assert.Throws<ArgumentException>(() => CallDiagramQuery.Build(index, new(name), new(MaxNodes: 0)));
        Assert.Throws<OperationCanceledException>(() => CallDiagramQuery.Build(index, new(name), new(), new CancellationToken(true)));
    }
}
