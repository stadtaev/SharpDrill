using System.Collections.Immutable;
using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

public sealed class IndexTests
{
    private static readonly CompilationId Project = new("project-id");
    private static readonly SourceEvidence Evidence = new("Example.cs", "checksum", 0, 10, 1, 1, false);
    private static TypeUse Use(string name) => new(new TypeId(name), name, NullabilityKind.NotAnnotated, []);
    private static TypeFact Type(string name, params string[] bases) =>
        new(new(name), Project, name, "Public", [.. bases.Select(Use)], [], []);
    private static CallableFact Method(string name, string? type = null) => new(new(name), Project, type is null ? null : new TypeId(type), null,
        CallableKind.Method, name + "()", "Public", Use("Void"), [], [], BodyStatus.Source, [Evidence]);
    private static CallSiteFact Call(string id, string caller, string? target, string display) =>
        new(new(id), new(caller), target is null ? null : new CallableId(target), display, DispatchKind.Direct, [], Evidence, false, []);

    private static ProgramIndex Index() => new(new ProgramSnapshot
    {
        Compilations = [new(Project, "/repo/src/Orders.Api/Orders.Api.csproj", "Orders.Api", "net10.0", "Debug", "Latest", "Enable", [], [])],
        // A -> B -> A is a malformed but possible metadata shape; ancestry must still terminate.
        Types = [Type("Child", "Base", "IFace"), Type("Base", "Root"), Type("Root"), Type("IFace", "Unknown"), Type("A", "B"), Type("B", "A")],
        Callables = [Method("Caller", "Child"), Method("Zeta", "Child"), Method("Alpha", "Child"), Method("Free"), Method("Iface.Run", "IFace"), Method("Impl.Run", "Child")],
        CallSites = [Call("s3", "Caller", "Zeta", "Zeta()"), Call("s1", "Caller", "Alpha", "Alpha()"), Call("s2", "Caller", "Alpha", "Alpha()"),
            Call("s0", "Free", "Alpha", "Alpha()"), Call("s4", "Caller", null, "<unresolved>")],
        ThrowSites = [new(new("t2"), new("Alpha"), ThrowKind.Explicit, Use("Exception"), Evidence, []),
            new(new("t1"), new("Alpha"), ThrowKind.Rethrow, null, Evidence, [], CatchId: new CatchId("c1"))],
        Catches = [new(new("c1"), new("Alpha"), null, null, Evidence, Evidence, HandlerOutcome.Rethrows, new ThrowSiteId("t1"))],
        ExceptionRegions = [new(new("r1"), new("Alpha"), Evidence, [new CatchId("c1")], false)],
        Gaps = [new(GapKind.Unsupported, Project, new CallableId("Alpha"), "second", Evidence), new(GapKind.Unsupported, Project, new CallableId("Alpha"), "first", Evidence),
            new(GapKind.ProjectLoad, null, null, "global", null)],
        Implementations = [new(new("Iface.Run"), new("Impl.Run"), ImplementationKind.Interface)]
    });

    [Fact]
    public void Lookups_resolve_facts_and_report_missing_ids()
    {
        var index = Index();
        Assert.Equal("Alpha()", index.Callable(new("Alpha")).DisplaySignature);
        Assert.False(index.TryGetCallable(new("Missing"), out _));
        Assert.Throws<KeyNotFoundException>(() => index.Callable(new("Missing")));
        Assert.Equal("Child", index.DeclaringType(new("Alpha"))!.DisplayName);
        Assert.Null(index.DeclaringType(new("Free")));
        Assert.Equal("Orders.Api", index.ProjectName(Project));
        Assert.Equal("metadata", index.ProjectName(new("metadata|System.Runtime")));
        Assert.Equal("t2", index.ThrowSite(new("t2")).Id.Value);
        Assert.Equal(new ThrowSiteId("t1"), index.Catch(new("c1")).SubsequentThrow);
        Assert.Equal(new ExceptionRegionId("r1"), Assert.Single(index.RegionsOf(new("c1"))).Id);
        Assert.Empty(index.RegionsOf(new("unknown")));
    }

    [Fact]
    public void Grouped_views_are_deterministically_ordered()
    {
        var index = Index();
        // Outgoing: target display, then site ID; unresolved display sorts by ordinal like any other string.
        Assert.Equal(["<unresolved>:s4", "Alpha():s1", "Alpha():s2", "Zeta():s3"],
            index.Outgoing(new("Caller")).Select(x => $"{x.TargetDisplay}:{x.Id.Value}"));
        Assert.Equal(["s0", "s1", "s2"], index.Incoming(new("Alpha")).Select(x => x.Id.Value));
        Assert.Equal(["t1", "t2"], index.ThrowSites(new("Alpha")).Select(x => x.Id.Value));
        Assert.Equal(["c1"], index.Catches(new("Alpha")).Select(x => x.Id.Value));
        // Gaps keep snapshot order; unowned gaps are not attributed to callables.
        Assert.Equal(["second", "first"], index.Gaps(new("Alpha")).Select(x => x.Message));
        Assert.Equal(["Alpha()", "Caller()", "Impl.Run()", "Zeta()"], index.Members(new("Child")).Select(x => x.DisplaySignature));
        Assert.Empty(index.Outgoing(new("Zeta")));
        Assert.Empty(index.Incoming(new("Caller")));
    }

    [Fact]
    public void Ancestors_are_transitive_breadth_first_and_cycle_safe()
    {
        var index = Index();
        Assert.Equal(["Base", "IFace", "Root", "Unknown"], index.Ancestors(new("Child")).Select(x => x.Value));
        Assert.Equal(["B"], index.Ancestors(new("A")).Select(x => x.Value));
        Assert.Empty(index.Ancestors(new("Root")));
    }

    [Fact]
    public void Implementation_candidates_are_navigable_both_ways()
    {
        var index = Index();
        Assert.Equal(new CallableId("Impl.Run"), Assert.Single(index.Implementations(new("Iface.Run"))).Implementation);
        Assert.Equal(new CallableId("Iface.Run"), Assert.Single(index.Declarations(new("Impl.Run"))).Declared);
        Assert.Empty(index.Implementations(new("Impl.Run")));
    }
}
