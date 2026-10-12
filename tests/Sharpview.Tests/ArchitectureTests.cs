using System.Collections.Immutable;
using Sharpview.Core;
using Xunit;

namespace Sharpview.Tests;

public sealed class ArchitectureTests
{
    private static readonly CompilationId Api = new("api"), App = new("app"), Infra = new("infra"), Tools = new("tools"), Metadata = new("metadata|System.Runtime");
    private static SourceEvidence At(string path, int start) => new(path, "checksum", start, 1, 1, start + 1, false);
    private static CompilationFact Project(CompilationId id, string name) => new(id, $"/repo/{name}/{name}.csproj", name, "net10.0", "Debug", "Latest", "Enable", [], []);
    private static TypeFact Type(string name, CompilationId compilation, string ns) => new(new(name), compilation, name, "Public", [], [], [], ns);
    private static TypeUse Use(string name) => new(new TypeId(name), name, NullabilityKind.NotAnnotated, []);
    private static CallableFact Method(string name, string type, CompilationId compilation) => new(new(name), compilation, new TypeId(type), null,
        CallableKind.Method, name + "()", "Public", Use("Void"), [], [], compilation == Metadata ? BodyStatus.MetadataOnly : BodyStatus.Source, []);
    private static CallSiteFact Call(string id, string caller, string? target, SourceEvidence evidence, DispatchKind dispatch = DispatchKind.Direct) =>
        new(new(id), new(caller), target is null ? null : new CallableId(target), target ?? "<unresolved>", dispatch, [], evidence, false, []);

    private static readonly ImmutableArray<LayerMapping> Mappings = [new(LayerScope.Project, "Orders.Api", "API"),
        new(LayerScope.Project, "Orders.Application", "Application"), new(LayerScope.Project, "Orders.Infrastructure", "Infrastructure")];

    private static ProgramIndex Program() => new(new ProgramSnapshot
    {
        Compilations = [Project(Api, "Orders.Api"), Project(App, "Orders.Application"), Project(Infra, "Orders.Infrastructure"), Project(Tools, "Tools")],
        Types = [Type("Controller", Api, "Orders.Api"), Type("Service", App, "Orders.Application"), Type("IRepo", App, "Orders.Application"),
            Type("Repo", Infra, "Orders.Infrastructure"), Type("Tool", Tools, "Tools"), Type("Console", Metadata, "System")],
        Callables = [Method("Get", "Controller", Api), Method("Load", "Service", App), Method("IRepo.Read", "IRepo", App), Method("Repo.Read", "Repo", Infra),
            Method("Run", "Tool", Tools), Method("WriteLine", "Console", Metadata)],
        CallSites = [Call("c2", "Get", "Load", At("b.cs", 5)), Call("c1", "Get", "Load", At("a.cs", 9)), Call("c3", "Load", "IRepo.Read", At("s.cs", 1), DispatchKind.Interface),
            Call("c4", "Load", "WriteLine", At("s.cs", 2)), Call("c5", "Get", null, At("a.cs", 3)), Call("c6", "Run", "Load", At("t.cs", 1))],
        Dependencies = [new(new TypeId("Repo"), null, Use("IRepo"), DependencyKind.Field, At("r.cs", 4)),
            new(new TypeId("Controller"), new CallableId("Get"), new TypeUse(null, "T", NullabilityKind.Oblivious, []), DependencyKind.Signature, null),
            new(new TypeId("Console"), new CallableId("WriteLine"), Use("Repo"), DependencyKind.Signature, null)],
        Implementations = [new(new("IRepo.Read"), new("Repo.Read"), ImplementationKind.Interface)]
    });

    [Fact]
    public void Layer_map_uses_longest_prefix_project_tie_break_and_namespace_boundaries()
    {
        var index = new ProgramIndex(new ProgramSnapshot
        {
            Compilations = [Project(Api, "Orders.Api"), Project(Infra, "Orders.Infrastructure"), Project(Tools, "Tools")],
            Types = [Type("Controller", Api, "Orders.Api"), Type("Repo", Infra, "Orders.Infrastructure.Data"), Type("Lookalike", Tools, "Orders.InfrastructureX"),
                Type("Global", Tools, ""), Type("Console", Metadata, "System"), Type("Json", Metadata, "Microsoft.Json")],
            Callables = [Method("Get", "Controller", Api), Method("WriteLine", "Console", Metadata)]
        });
        var map = new LayerMap(index, new([new(LayerScope.Project, "Orders", "Root"), new(LayerScope.Namespace, "Orders.Api", "NamespaceApi"),
            new(LayerScope.Project, "Orders.Api", "API"), new(LayerScope.Namespace, "Orders.Infrastructure", "Infrastructure"),
            new(LayerScope.Namespace, "Orders.Infrastructure.Data", "Data"), new(LayerScope.Namespace, "System", "Framework")]));
        Assert.Equal("API", map.LayerOf(new TypeId("Controller")));          // equal length: project wins
        Assert.Equal("Data", map.LayerOf(new TypeId("Repo")));               // longest namespace prefix
        Assert.Null(map.LayerOf(new TypeId("Lookalike")));                   // prefix must end at a namespace boundary
        Assert.Null(map.LayerOf(new TypeId("Global")));
        Assert.Equal("Framework", map.LayerOf(new TypeId("Console")));       // referenced types can be mapped by namespace
        Assert.Null(map.LayerOf(new TypeId("Json")));
        Assert.Null(map.LayerOf(new TypeId("Missing")));
        Assert.Equal("API", map.LayerOf(new CallableId("Get")));
        Assert.Equal("Framework", map.LayerOf(new CallableId("WriteLine")));
        Assert.Equal(new[] { "Root", "NamespaceApi", "API", "Infrastructure", "Data", "Framework" }, map.Model.Layers.AsEnumerable());
        // A type scope covers the type and its nested types; a namespace scope never matches a type name.
        var types = new LayerMap(index, new([new(LayerScope.Type, "Repo", "RepoType"), new(LayerScope.Namespace, "Repo", "NotAType")]));
        Assert.Equal("RepoType", types.LayerOf(new TypeId("Repo")));
        Assert.Null(types.LayerOf(new TypeId("Controller")));
    }

    [Fact]
    public void Layer_graph_aggregates_each_edge_kind_with_stable_witnesses()
    {
        var index = Program();
        var graph = LayerGraph.Build(index, new LayerMap(index, new(Mappings)));
        Assert.Equal([
            "(unmapped)-->Application DirectCall 1 t.cs Run()-->Load()",
            "API-->Application DirectCall 2 a.cs Get()-->Load()",
            "Application-->Application DeclaredCall 1 s.cs Load()-->IRepo.Read()",
            "Application-->Infrastructure CandidateCall 1 s.cs Load()-->Repo.Read()",
            "Infrastructure-->Application TypeDependency 1 r.cs Repo-->IRepo"],
            graph.Edges.Select(x => $"{x.From}-->{x.To} {x.Kind} {x.Count} {x.Evidence?.Path} {x.WitnessSource}-->{x.WitnessTarget}"));
        // Console.WriteLine is an unmapped referenced target; the metadata-owned signature fact is not a program edge.
        Assert.Equal(1, graph.ExternalReferences);
        Assert.Equal(1, graph.UnresolvedCalls);
    }

    [Fact]
    public void Allowed_dependency_rule_reports_violations_and_unknowns_but_not_candidates_or_passes()
    {
        var index = Program();
        var graph = LayerGraph.Build(index, new LayerMap(index, new(Mappings)));
        Assert.Empty(LayerRules.AllowedDependencies(graph, new(Mappings)));
        var findings = LayerRules.AllowedDependencies(graph, new(Mappings, [new("API", "Application")]));
        Assert.Equal([
            "Unknown (unmapped) --> Application (DirectCall)",
            "Violation Infrastructure --> Application (TypeDependency)"],
            findings.Select(x => $"{x.Verdict} {x.Subject}"));
        Assert.All(findings, x => Assert.Equal(LayerRules.AllowedDependenciesRuleId, x.RuleId));
        Assert.Equal("r.cs", Assert.Single(findings, x => x.Verdict == Verdict.Violation).Evidence.Single().Path);
        var allowed = LayerRules.AllowedDependencies(graph, new(Mappings, [new("API", "Application"), new("Infrastructure", "Application")]));
        Assert.Equal(Verdict.Unknown, Assert.Single(allowed).Verdict);
    }
}
