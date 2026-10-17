using System.Collections.Immutable;
using Sharpview.Core;
using Xunit;

namespace Sharpview.Tests;

public sealed class ReachabilityTests
{
    private static readonly CompilationId Project = new("project");
    private static readonly SourceEvidence Evidence = new("Example.cs", "checksum", 0, 1, 1, 1, false);
    private static readonly TypeUse Void = new(new TypeId("Void"), "Void", NullabilityKind.NotAnnotated, []);
    private static CallableFact Method(string name, BodyStatus body = BodyStatus.Source) => new(new(name), Project, null, null,
        CallableKind.Method, name + "()", "Public", Void, [], [], body, []);
    private static CallSiteFact Call(string id, string caller, string? target, DispatchKind dispatch = DispatchKind.Direct) =>
        new(new(id), new(caller), target is null ? null : new CallableId(target), (target ?? "<unresolved>") + "()", dispatch, [], Evidence, false, []);
    private static ProgramIndex Index(CallableFact[] methods, CallSiteFact[] calls, ImplementationFact[]? implementations = null) =>
        new(new ProgramSnapshot { Callables = [.. methods], CallSites = [.. calls], Implementations = [.. implementations ?? []] });
    private static RootReach Reach(ProgramIndex index, ReachOptions? options = null) =>
        Assert.Single(ReachabilityQuery.Analyze(index, [new("Root")], options ?? new()).Roots);
    private static string Describe(ReachedCallable x) => $"{x.Callable.Value}@{x.Distance}:{string.Join(">", x.Witness.Select(s => s.Site.Value))}";

    [Fact]
    public void Diamond_reports_the_shared_callable_once_with_a_deterministic_shortest_witness()
    {
        var index = Index([Method("Root"), Method("Left"), Method("Right"), Method("Shared")],
            [Call("r-right", "Root", "Right"), Call("r-left", "Root", "Left"), Call("l-shared", "Left", "Shared"), Call("x-shared", "Right", "Shared")]);
        var reach = Reach(index);
        // Outgoing order is target display then site ID, so Left is expanded first and supplies the witness.
        Assert.Equal(["Left@1:r-left", "Right@1:r-right", "Shared@2:r-left>l-shared"], reach.Reached.Select(Describe));
        Assert.Empty(reach.Boundaries);
        Assert.Equal(reach.Reached.Select(Describe), Reach(index).Reached.Select(Describe));
    }

    [Fact]
    public void Cycles_terminate_and_shorter_paths_win()
    {
        var reach = Reach(Index([Method("Root"), Method("A"), Method("B")],
            [Call("ra", "Root", "A"), Call("ab", "A", "B"), Call("ba", "B", "A"), Call("br", "B", "Root"), Call("rb", "Root", "B")]));
        Assert.Equal(["A@1:ra", "B@1:rb"], reach.Reached.Select(Describe));
    }

    [Fact]
    public void Depth_and_work_limits_are_explicit()
    {
        var index = Index([Method("Root"), Method("A"), Method("B")], [Call("ra", "Root", "A"), Call("ab", "A", "B")]);
        var shallow = Reach(index, new(MaxDepth: 1));
        Assert.True(shallow.DepthLimitReached);
        Assert.Equal(["A@1:ra"], shallow.Reached.Select(Describe));
        Assert.Equal((new CallableId("A"), ReachBoundaryKind.DepthLimit), (Assert.Single(shallow.Boundaries).Caller, shallow.Boundaries[0].Kind));
        var budget = ReachabilityQuery.Analyze(index, [new("Root")], new(MaxExpansions: 1));
        Assert.True(budget.WorkLimitReached);
        Assert.Equal(1, budget.ExpandedSites);
        Assert.Throws<ArgumentException>(() => ReachabilityQuery.Analyze(index, [new("Missing")], new()));
        Assert.Throws<ArgumentException>(() => ReachabilityQuery.Analyze(index, [new("Root")], new(MaxDepth: -1)));
    }

    [Fact]
    public void Unresolved_targets_and_missing_bodies_are_boundaries()
    {
        var reach = Reach(Index([Method("Root"), Method("External", BodyStatus.MetadataOnly)], [Call("u", "Root", null), Call("e", "Root", "External")]));
        Assert.Equal(["External@1:e"], reach.Reached.Select(Describe));
        Assert.Equal([ReachBoundaryKind.Unresolved, ReachBoundaryKind.MissingBody], reach.Boundaries.Select(x => x.Kind));
    }

    [Fact]
    public void Definitely_reachable_callables_are_never_labelled_candidate_only()
    {
        // Root --I.Run (interface)--> Impl.Run at distance 1, but Root --> B --> Impl.Run directly at distance 2.
        var index = Index([Method("Root"), Method("I.Run", BodyStatus.NoBody), Method("Impl.Run"), Method("B"), Method("Leaf")],
            [Call("a-iface", "Root", "I.Run", DispatchKind.Interface), Call("b", "Root", "B"), Call("b-impl", "B", "Impl.Run"), Call("leaf", "Impl.Run", "Leaf")],
            [new(new("I.Run"), new("Impl.Run"), ImplementationKind.Interface)]);
        var reach = Reach(index, new(FollowCandidates: true));
        Assert.Equal(["B@1:b", "Impl.Run@1:a-iface", "Leaf@2:a-iface>leaf"], reach.Reached.Select(Describe));
        Assert.All(reach.Reached, x => Assert.False(x.ViaCandidate));
        Assert.Equal(1, Assert.Single(reach.Boundaries, x => x.Kind == ReachBoundaryKind.Dispatch).CandidatesFollowed);
    }

    [Fact]
    public void Shorter_candidate_route_reopens_a_direct_reachable_method_at_the_depth_limit()
    {
        var index = Index([Method("Root"), Method("A"), Method("B"), Method("C"), Method("I", BodyStatus.NoBody), Method("Leaf"), Method("End")],
            [Call("ra", "Root", "A"), Call("ab", "A", "B"), Call("bc", "B", "C"), Call("ri", "Root", "I", DispatchKind.Interface),
                Call("cl", "C", "Leaf"), Call("le", "Leaf", "End")],
            [new(new("I"), new("C"), ImplementationKind.Interface)]);
        var reach = Reach(index, new(FollowCandidates: true, MaxDepth: 3));
        var c = Assert.Single(reach.Reached, x => x.Callable.Value == "C");
        Assert.Equal(1, c.Distance);
        Assert.False(c.ViaCandidate);
        Assert.True(c.Witness.Single().Candidate);
        Assert.Equal(3, Assert.Single(reach.Reached, x => x.Callable.Value == "End").Distance);
        Assert.False(reach.DepthLimitReached);
        Assert.Single(reach.Boundaries, x => x.Kind == ReachBoundaryKind.Dispatch);
    }

    [Fact]
    public void Unresolved_sites_are_charged_individually_in_outgoing_order()
    {
        var index = Index([Method("Root"), Method("A")],
            [Call("known", "Root", "A") with { TargetDisplay = "0" }, Call("u1", "Root", null), Call("u2", "Root", null)]);
        var result = ReachabilityQuery.Analyze(index, [new("Root")], new(MaxExpansions: 2));
        Assert.True(result.WorkLimitReached);
        Assert.Equal(2, result.ExpandedSites);
        Assert.Equal("A", Assert.Single(result.Roots[0].Reached).Callable.Value);
        Assert.Equal("u1", Assert.Single(result.Roots[0].Boundaries).Site!.Value.Value);
    }

    [Fact]
    public void Depth_boundary_is_suppressed_for_cycles_but_retains_dispatch_and_unresolved_uncertainty()
    {
        var cycle = Index([Method("Root"), Method("A")], [Call("ra", "Root", "A"), Call("ar", "A", "Root")]);
        Assert.Empty(Reach(cycle, new(MaxDepth: 1)).Boundaries);
        var uncertain = Index([Method("Root"), Method("A"), Method("I", BodyStatus.NoBody)],
            [Call("ra", "Root", "A"), Call("ar", "A", "Root"), Call("u", "A", null), Call("i", "A", "I", DispatchKind.Interface)]);
        var reach = Reach(uncertain, new(MaxDepth: 1));
        Assert.True(reach.DepthLimitReached);
        Assert.Equal([ReachBoundaryKind.DepthLimit, ReachBoundaryKind.Unresolved, ReachBoundaryKind.Dispatch], reach.Boundaries.Select(b => b.Kind));
    }

    [Fact]
    public void Candidate_budget_stop_keeps_witnesses_consistent_and_reports_only_followed_candidates()
    {
        var index = Index([Method("Root"), Method("A"), Method("B"), Method("C"), Method("D"), Method("X"),
                Method("I1", BodyStatus.NoBody), Method("I2", BodyStatus.NoBody), Method("E")],
            [Call("ra", "Root", "A"), Call("rx", "Root", "I1", DispatchKind.Interface), Call("rc", "Root", "I2", DispatchKind.Interface),
                Call("ab", "A", "B"), Call("bc", "B", "C"), Call("cd", "C", "D"), Call("xe", "X", "E")],
            [new(new("I1"), new("X"), ImplementationKind.Interface), new(new("I2"), new("C"), ImplementationKind.Interface)]);
        var reach = Reach(index, new(FollowCandidates: true, MaxExpansions: 6));
        Assert.True(reach.WorkLimitReached);
        Assert.All(reach.Reached, x => { Assert.Equal(x.Distance, x.Witness.Length); Assert.Equal(x.WitnessUsesCandidate, x.Witness.Any(step => step.Candidate)); });
        var stoppedBeforeCandidates = Reach(index, new(FollowCandidates: true, MaxExpansions: 3));
        Assert.True(stoppedBeforeCandidates.WorkLimitReached);
        Assert.All(stoppedBeforeCandidates.Boundaries.Where(x => x.Kind == ReachBoundaryKind.Dispatch), x => Assert.Equal(0, x.CandidatesFollowed));
    }

    [Fact]
    public void Long_chain_keeps_parent_links_without_eager_quadratic_witness_arrays()
    {
        const int length = 5000;
        var methods = Enumerable.Range(0, length + 1).Select(i => Method(i == 0 ? "Root" : $"N{i}")).ToArray();
        var calls = Enumerable.Range(0, length).Select(i => Call($"s{i}", i == 0 ? "Root" : $"N{i}", $"N{i + 1}")).ToArray();
        var index = Index(methods, calls);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var reach = Reach(index, new(MaxDepth: length));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(length, reach.Reached.Length);
        // Eagerly copying all 12.5 million ReachStep references exceeds 100 MB even before the step objects.
        // A generous allocation ceiling verifies the storage contract without asserting timing or exact layout.
        Assert.True(allocated < 80_000_000, $"Reach allocated {allocated:N0} bytes before witnesses were requested.");
        var last = Assert.Single(reach.Reached, x => x.Callable.Value == $"N{length}");
        Assert.Equal(length, last.Witness.Length);
        Assert.Equal(length, last.Distance);
    }

    [Fact]
    public void Dispatch_stops_unless_candidates_are_followed_and_then_marks_them()
    {
        var index = Index([Method("Root"), Method("IRepo.Read", BodyStatus.NoBody), Method("Sql.Read"), Method("Cached.Read"), Method("Base.Run"), Method("Derived.Run")],
            [Call("i", "Root", "IRepo.Read", DispatchKind.Interface), Call("v", "Root", "Base.Run", DispatchKind.Virtual)],
            [new(new("IRepo.Read"), new("Sql.Read"), ImplementationKind.Interface), new(new("Sql.Read"), new("Cached.Read"), ImplementationKind.Override),
                new(new("Base.Run"), new("Derived.Run"), ImplementationKind.Override)]);
        var stopped = Reach(index);
        Assert.Empty(stopped.Reached);
        Assert.Equal(["Virtual Base.Run 0", "Interface IRepo.Read 0"], stopped.Boundaries.Select(x => $"{x.Dispatch} {x.DeclaredTarget!.Value.Value} {x.CandidatesFollowed}"));
        var followed = Reach(index, new(FollowCandidates: true));
        // Base.Run has a source body, so the declared virtual target is itself a candidate; overrides of overrides are included.
        Assert.Equal(["Base.Run@1:v", "Cached.Read@1:i", "Derived.Run@1:v", "Sql.Read@1:i"], followed.Reached.Select(Describe));
        Assert.All(followed.Reached, x => Assert.True(x.ViaCandidate && x.Witness.Single().Candidate));
        Assert.Equal([2, 2], followed.Boundaries.Select(x => x.CandidatesFollowed));
    }
}
