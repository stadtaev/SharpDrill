using System.Collections.Immutable;
using System.Text.Json;
using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

// Independent semantic graphs keep query tests from sharing extraction's assumptions.
public sealed class CoreTests
{
    private static readonly CompilationId Project = new("test-project");
    private static readonly SourceEvidence Evidence = new("Example.cs", "checksum", 0, 10, 1, 1, false);
    private static readonly TypeUse ExceptionType = Type("Exception");
    private static TypeUse Type(string name) => new(new TypeId(name), name, NullabilityKind.NotAnnotated, []);
    private static CallableFact Method(string name) => new(new(name), Project, null, null, CallableKind.Method,
        name + "()", "Public", Type("Void"), [], [], BodyStatus.Source, [Evidence]);
    private static CallSiteFact Call(string owner, string target, string id, DispatchKind dispatch = DispatchKind.Direct,
        ImmutableArray<string> regions = default, bool awaited = false) => new(new(id), new(owner), new(target), target + "()", dispatch,
            [], Evidence, false, Regions(regions), awaited);
    private static ThrowSiteFact Throw(string owner, string id = "origin", TypeUse? type = null, ImmutableArray<string> regions = default) =>
        new(new(id), new(owner), ThrowKind.Explicit, type ?? ExceptionType, Evidence, Regions(regions), true);
    private static ImmutableArray<ExceptionRegionId> Regions(ImmutableArray<string> ids) => ids.IsDefault ? [] : [.. ids.Select(x => new ExceptionRegionId(x))];
    private static ProgramSnapshot Snapshot(CallableFact[] methods, CallSiteFact[] calls, ThrowSiteFact[]? origins = null,
        CatchFact[]? catches = null, ExceptionRegionFact[]? regions = null) => new()
    {
        Types = [
            new(new("Exception"), Project, "Exception", "Public", [], [], []),
            new(new("OtherException"), Project, "OtherException", "Public", [], [], [])],
        Callables = methods.ToImmutableArray(),
        CallSites = calls.ToImmutableArray(),
        ThrowSites = (origins ?? []).ToImmutableArray(),
        Catches = (catches ?? []).ToImmutableArray(),
        ExceptionRegions = (regions ?? []).ToImmutableArray()
    };
    private static CatchFact Handler(string id, string owner, HandlerOutcome outcome, string? subsequent = null,
        TypeUse? caught = null, string? filter = null) => new(new(id), new(owner), caught, filter, Evidence, Evidence, outcome, subsequent is null ? null : new ThrowSiteId(subsequent));
    private static ExceptionRegionFact Region(string id, string owner, params string[] catches) => new(new(id), new(owner), Evidence, [.. catches.Select(x => new CatchId(x))], false);
    private static ExceptionWitness AnalyzeOrigin(ProgramSnapshot snapshot) => Assert.Single(
        ExceptionQuery.Analyze(snapshot, [new("Action")], new()).Results, x => x.Origin == new ThrowSiteId("origin"));

    [Fact]
    public void Exception_summary_closed_recursion_has_no_escape_or_cycle_unknown()
    {
        var snapshot = Snapshot([Method("Action"), Method("Loop")],
            [Call("Action", "Loop", "forward"), Call("Loop", "Action", "back")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        Assert.Empty(result.Roots[new CallableId("Action")].Escapes);
        Assert.Empty(result.Roots[new CallableId("Action")].Unknowns);
        Assert.True(result.Roots[new CallableId("Action")].IsComplete);
    }

    [Fact]
    public void Exception_summary_preserves_origins_across_recursive_calls_and_reconstructs_acyclic_witness()
    {
        var snapshot = Snapshot([Method("Action"), Method("Loop")],
            [Call("Action", "Loop", "forward"), Call("Loop", "Action", "back")],
            [Throw("Loop", "one"), Throw("Loop", "two")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        Assert.Equal(new[] { "one", "two" }, result.Roots[new CallableId("Action")].Escapes.Select(x => x.Origin.Value).Order());
        Assert.Empty(result.Roots[new CallableId("Action")].Unknowns);
        var witness = result.GetWitness(new CallableId("Action"), new ThrowSiteId("one"));
        Assert.Equal(new[] { "Action", "Loop" }, witness.Path.Select(x => x.Value));
        Assert.Equal(new[] { "forward" }, witness.Sites.Select(x => x.Value));
    }

    [Fact]
    public void Exception_summary_transfers_each_diamond_call_site_independently()
    {
        var snapshot = Snapshot([Method("Action"), Method("Left"), Method("Right"), Method("Shared")],
            [Call("Action", "Left", "al"), Call("Action", "Right", "ar"),
             Call("Left", "Shared", "ls", regions: ["try"]), Call("Right", "Shared", "rs")],
            [Throw("Shared")], [Handler("catch", "Left", HandlerOutcome.Intercepts)], [Region("try", "Left", "catch")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        Assert.Single(result.Roots[new CallableId("Action")].Escapes);
        Assert.Empty(result.Crossings[new CallSiteId("ls")].Escapes);
        Assert.Single(result.Crossings[new CallSiteId("rs")].Escapes);
        Assert.Equal(new[] { "Action", "Right", "Shared" }, result.GetWitness(new CallableId("Action"), new ThrowSiteId("origin")).Path.Select(x => x.Value));
    }

    [Theory]
    [InlineData("a", "c")]
    [InlineData("z", "b")]
    public void Exception_summary_witness_is_the_shortest_path_whatever_the_discovery_order(string longSite, string shortSite)
    {
        var snapshot = Snapshot([Method("Action"), Method("A"), Method("B"), Method("C"), Method("Leaf")],
            [Call("Action", "A", longSite), Call("A", "B", "ab"), Call("B", "Leaf", "bl"), Call("Action", "C", shortSite), Call("C", "Leaf", "cl")],
            [Throw("Leaf")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        var witness = result.GetWitness(new CallableId("Action"), new ThrowSiteId("origin"));
        Assert.Equal(new[] { "Action", "C", "Leaf" }, witness.Path.Select(x => x.Value));
        Assert.Equal(new[] { shortSite, "cl" }, witness.Sites.Select(x => x.Value));
    }

    [Fact]
    public void Exception_summary_keeps_known_escape_beside_unknown_boundary()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")],
            [Call("Action", "Leaf", "known"), Call("Action", "Missing", "missing")], [Throw("Leaf")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        Assert.Single(result.Roots[new CallableId("Action")].Escapes);
        Assert.Contains(result.Roots[new CallableId("Action")].Unknowns, x => x.ReasonCode == ExceptionReason.UnresolvedTarget);
        Assert.False(result.Roots[new CallableId("Action")].IsComplete);
    }

    [Theory]
    [InlineData(HandlerOutcome.Rethrows, "Exception")]
    [InlineData(HandlerOutcome.Replaces, "OtherException")]
    public void Exception_summary_preserves_origin_and_applies_handler_transformation_per_site(HandlerOutcome outcome, string expectedType)
    {
        var next = Throw("Middle", "next", Type("OtherException")) with
        { CatchId = new CatchId("catch"), Kind = outcome == HandlerOutcome.Rethrows ? ThrowKind.Rethrow : ThrowKind.Explicit };
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am"), Call("Middle", "Leaf", "ml", regions: ["try"])],
            [Throw("Leaf"), next], [Handler("catch", "Middle", outcome, "next")], [Region("try", "Middle", "catch")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        var escape = Assert.Single(result.Roots[new CallableId("Action")].Escapes);
        Assert.Equal(expectedType, escape.ExceptionType);
        if (outcome == HandlerOutcome.Replaces)
        {
            // The replacement escapes as an origin of its own; the original ends at the handler.
            Assert.Equal(new ThrowSiteId("next"), escape.Origin);
            Assert.Null(escape.SubsequentOrigin);
            Assert.Equal(ExceptionReason.MayEscape, escape.ReasonCode);
            return;
        }
        Assert.Equal(new ThrowSiteId("origin"), escape.Origin);
        Assert.Equal(new ThrowSiteId("next"), escape.SubsequentOrigin);
        Assert.Equal(ExceptionReason.ChangedMayEscape, escape.ReasonCode);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Exception_summary_requires_await_for_task_exception_to_reach_caller_catch(bool awaited, bool complete)
    {
        var snapshot = Snapshot([Method("Action"), Method("Operation") with { ReturnsTask = true }],
            [Call("Action", "Operation", "call", regions: ["try"], awaited: awaited)], [Throw("Operation")],
            [Handler("catch", "Action", HandlerOutcome.Intercepts)], [Region("try", "Action", "catch")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        Assert.Empty(result.Roots[new CallableId("Action")].Escapes);
        Assert.Equal(complete, result.Roots[new CallableId("Action")].IsComplete);
        if (!awaited) Assert.Contains(result.Roots[new CallableId("Action")].Unknowns,
            x => x.ReasonCode == ExceptionReason.UnobservedTask);
    }

    [Fact]
    public void Ownerless_exception_gaps_are_global_caveats_without_consuming_summary_budgets()
    {
        var gap = new AnalysisGap(GapKind.Compilation, Project, null, "Compilation incomplete", Evidence);
        var snapshot = Snapshot([Method("Action"), Method("Other")], [], [Throw("Action")]) with { Gaps = [gap] };
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action"), new("Other")], new(MaxWork: 1, MaxFacts: 1));
        Assert.Equal(gap, Assert.Single(result.GlobalGaps));
        Assert.True(result.IsComplete);
        Assert.False(result.WorkLimitReached); Assert.False(result.FactLimitReached);
        Assert.Single(result.Roots[new CallableId("Action")].Escapes);
        Assert.All(result.Roots.Values, root => { Assert.True(root.IsComplete); Assert.Empty(root.Unknowns); });
    }

    [Fact]
    public void Detailed_exception_query_reports_ownerless_gaps_once_outside_action_witnesses()
    {
        var gap = new AnalysisGap(GapKind.Compilation, Project, null, "Compilation incomplete", Evidence);
        var snapshot = Snapshot([Method("Action"), Method("Other")], [], [Throw("Action"), Throw("Other", "other")]) with { Gaps = [gap] };
        var result = ExceptionQuery.Analyze(snapshot, [new("Action"), new("Other")], new());
        Assert.Equal(gap, Assert.Single(result.GlobalGaps));
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Results.Length);
        Assert.All(result.Results, x => Assert.NotEqual(ExceptionReason.ExtractionGap, x.ReasonCode));
        var limited = ExceptionQuery.Analyze(snapshot, [new("Action"), new("Other")], new(MaxResults: 1));
        Assert.NotNull(Assert.Single(limited.Results).Origin);
        Assert.Single(limited.GlobalGaps); Assert.True(limited.OutputLimitReached);
    }

    [Fact]
    public void Owned_exception_gap_stays_attached_to_its_reached_callable()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "call")]) with
        { Gaps = [new AnalysisGap(GapKind.Unsupported, Project, new CallableId("Leaf"), "Unsupported source body", Evidence)] };
        var result = ExceptionQuery.Analyze(snapshot, [new("Action")], new());
        Assert.Empty(result.GlobalGaps);
        Assert.False(result.IsComplete);
        var witness = Assert.Single(result.Results);
        Assert.Equal(ExceptionReason.ExtractionGap, witness.ReasonCode);
        Assert.Equal(["Action", "Leaf"], witness.Path.Select(x => x.Value));
    }

    [Fact]
    public void Exception_summary_work_and_fact_budgets_mark_all_roots_incomplete()
    {
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am"), Call("Middle", "Leaf", "ml")], [Throw("Leaf")]);
        var work = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")], new(MaxWork: 1));
        Assert.True(work.WorkLimitReached);
        Assert.False(work.Roots[new CallableId("Action")].IsComplete);
        Assert.Contains(work.Roots[new CallableId("Action")].Unknowns, x => x.ReasonCode == ExceptionReason.WorkLimit);
        var facts = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")], new(MaxFacts: 1));
        Assert.True(facts.FactLimitReached);
        Assert.False(facts.Roots[new CallableId("Action")].IsComplete);
        Assert.Contains(facts.Roots[new CallableId("Action")].Unknowns, x => x.ReasonCode == ExceptionReason.FactLimit);
        var exact = ExceptionSummaryQuery.Analyze(new ProgramIndex(Snapshot([Method("Action")], [], [Throw("Action")])),
            [new("Action")], new(MaxWork: 1, MaxFacts: 1));
        Assert.False(exact.WorkLimitReached);
        Assert.False(exact.FactLimitReached);
        Assert.True(exact.Roots[new CallableId("Action")].IsComplete);
    }

    [Fact]
    public void Exception_summary_honors_cancellation_and_rejects_invalid_budgets()
    {
        var index = new ProgramIndex(Snapshot([Method("Action")], []));
        Assert.Throws<OperationCanceledException>(() => ExceptionSummaryQuery.Analyze(index, [new("Action")], token: new CancellationToken(true)));
        Assert.Throws<ArgumentException>(() => ExceptionSummaryQuery.Analyze(index, [new("Action")], new(MaxFacts: 0)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Exception_summary_follows_filters_past_and_stops_at_a_finally_that_may_throw(bool finallyMayThrow, bool hasFilter)
    {
        var region = Region("try", "Action", "catch") with { FinallyMayThrow = finallyMayThrow };
        var handler = Handler("catch", "Action", HandlerOutcome.Intercepts, filter: hasFilter ? "condition" : null);
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"])], [handler], [region]);
        var summary = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]).Roots[new CallableId("Action")];
        if (hasFilter) { Assert.Single(summary.Escapes); Assert.Empty(summary.Unknowns); }
        else { Assert.Empty(summary.Escapes); Assert.Contains(summary.Unknowns, x => x.ReasonCode == ExceptionReason.FinallyUnsupported && x.Origin == new ThrowSiteId("origin")); }
    }

    [Fact]
    public void Exception_summary_budget_marks_crossing_effects_incomplete()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "al")], [Throw("Leaf")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")], new(MaxWork: 1));
        var crossing = result.Crossings[new CallSiteId("al")];
        Assert.False(crossing.IsComplete);
        Assert.Contains(crossing.Unknowns, x => x.ReasonCode == ExceptionReason.WorkLimit);
    }

    [Fact]
    public void Exception_summary_marks_unresolved_crossing_unknown_with_evidence()
    {
        var snapshot = Snapshot([Method("Action")], [Call("Action", "Missing", "missing")]);
        var crossing = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")])
            .Crossings[new CallSiteId("missing")];
        Assert.False(crossing.IsComplete);
        Assert.Contains(crossing.Unknowns, x => x.ReasonCode == ExceptionReason.UnresolvedTarget
            && x.Evidence == Evidence);
    }

    [Fact]
    public void Exception_summary_marks_unbound_type_at_call_site_without_claiming_escape()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "al")],
            [Throw("Leaf") with { StaticExceptionType = null, ExactExceptionType = null, IsExactType = false }]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        Assert.Empty(result.Crossings[new CallSiteId("al")].Escapes);
        Assert.Contains(result.Crossings[new CallSiteId("al")].Unknowns,
            x => x.ReasonCode == ExceptionReason.UnboundExceptionType && x.Origin == new ThrowSiteId("origin"));
        Assert.False(result.Roots[new CallableId("Action")].IsComplete);
    }

    [Fact]
    public void Exception_summary_witness_overload_selects_changed_effect_for_same_origin()
    {
        var snapshot = Snapshot([Method("Action"), Method("Left"), Method("Right"), Method("Leaf")],
            [Call("Action", "Left", "al"), Call("Action", "Right", "ar"),
             Call("Left", "Leaf", "ll", regions: ["try"]), Call("Right", "Leaf", "rl")],
            [Throw("Leaf")], [Handler("catch", "Left", HandlerOutcome.Rethrows)], [Region("try", "Left", "catch")]);
        var result = ExceptionSummaryQuery.Analyze(new ProgramIndex(snapshot), [new("Action")]);
        var escapes = result.Roots[new CallableId("Action")].Escapes;
        Assert.Equal(2, escapes.Length);
        var changed = Assert.Single(escapes, x => x.ReasonCode == ExceptionReason.ChangedMayEscape);
        Assert.Equal(new[] { "Action", "Left", "Leaf" }, result.GetWitness(new CallableId("Action"), changed).Path.Select(x => x.Value));
    }

    [Fact]
    public void Exception_summary_long_chain_keeps_provenance_compact_until_witness_requested()
    {
        const int transitions = 3000;
        var methods = Enumerable.Range(0, transitions + 1).Select(i => Method($"M{i}")).ToArray();
        var calls = Enumerable.Range(0, transitions).Select(i => Call($"M{i}", $"M{i + 1}", $"site{i}")).ToArray();
        var index = new ProgramIndex(Snapshot(methods, calls, [Throw($"M{transitions}")]));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ExceptionSummaryQuery.Analyze(index, [new("M0")]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 80_000_000, $"Summary allocated {allocated:N0} bytes before witness reconstruction.");
        var escape = Assert.Single(result.Roots[new CallableId("M0")].Escapes);
        var witness = result.GetWitness(new CallableId("M0"), escape);
        Assert.Equal(transitions + 1, witness.Path.Length);
        Assert.Equal(transitions, witness.Sites.Length);
        Assert.Equal(new CallableId($"M{transitions}"), witness.Path[^1]);
    }

    [Fact]
    public void Snapshot_ids_serialize_as_plain_strings_and_round_trip()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "al", regions: ["try"])],
            [Throw("Leaf", regions: ["try"]) with { CatchId = new CatchId("catch") }],
            [Handler("catch", "Action", HandlerOutcome.Rethrows, "origin")], [Region("try", "Action", "catch")]);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(snapshot, options);
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            Assert.Equal("Action", root.GetProperty("callables")[0].GetProperty("id").GetString());
            Assert.Equal("al", root.GetProperty("callSites")[0].GetProperty("id").GetString());
            Assert.Equal("try", root.GetProperty("callSites")[0].GetProperty("protectedRegions")[0].GetString());
            var origin = root.GetProperty("throwSites")[0];
            Assert.Equal("origin", origin.GetProperty("id").GetString());
            Assert.Equal("catch", origin.GetProperty("catchId").GetString());
            Assert.Equal("origin", root.GetProperty("catches")[0].GetProperty("subsequentThrow").GetString());
            Assert.Equal("catch", root.GetProperty("exceptionRegions")[0].GetProperty("catches")[0].GetString());
            Assert.Equal("test-project", root.GetProperty("callables")[0].GetProperty("compilation").GetString());
            Assert.Equal("Exception", root.GetProperty("types")[0].GetProperty("id").GetString());
        }
        var back = JsonSerializer.Deserialize<ProgramSnapshot>(json, options)!;
        Assert.Equal(new CallableId("Action"), back.Callables[0].Id);
        Assert.Equal(new CallSiteId("al"), back.CallSites[0].Id);
        Assert.Equal(new ExceptionRegionId("try"), Assert.Single(back.CallSites[0].ProtectedRegions));
        Assert.Equal(new ThrowSiteId("origin"), back.ThrowSites[0].Id);
        Assert.Equal(new CatchId("catch"), back.ThrowSites[0].CatchId);
        Assert.Equal(new ThrowSiteId("origin"), back.Catches[0].SubsequentThrow);
        Assert.Equal(new CatchId("catch"), Assert.Single(back.ExceptionRegions[0].Catches));
        Assert.Equal(Project, back.Callables[0].Compilation);
        Assert.Equal(new TypeId("Exception"), back.Types[0].Id);
    }

    [Fact]
    public void Attribute_arguments_and_hierarchy_links_serialize_as_objects_and_ids_and_round_trip()
    {
        var route = new AttributeFact("RouteAttribute", ["\"orders\""], [new("Name", "\"list\"")]);
        var snapshot = new ProgramSnapshot
        {
            SourceRoot = "/repo",
            Types = [new(new("Derived"), Project, "Derived", "Public", [], [route], [], "Shop", DeclaredTypeKind.Record,
                TypeModifiers.Sealed | TypeModifiers.Generic, new TypeId("Base"), new TypeId("Outer"), "Project|T:Shop.Derived")],
            Callables = [Method("Run") with { Modifiers = CallableModifiers.Override | CallableModifiers.Sealed, Overrides = new CallableId("BaseRun"), Key = "Project|M:Run" }]
        };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(snapshot, options);
        using (var doc = JsonDocument.Parse(json))
        {
            var type = doc.RootElement.GetProperty("types")[0];
            var attribute = type.GetProperty("attributes")[0];
            Assert.Equal("RouteAttribute", attribute.GetProperty("name").GetString());
            Assert.Equal("\"orders\"", attribute.GetProperty("arguments")[0].GetString());
            Assert.Equal("Name", attribute.GetProperty("namedArguments")[0].GetProperty("name").GetString());
            Assert.Equal("\"list\"", attribute.GetProperty("namedArguments")[0].GetProperty("value").GetString());
            Assert.Equal("Base", type.GetProperty("baseClass").GetString());
            Assert.Equal("Outer", type.GetProperty("containingType").GetString());
            Assert.Equal("BaseRun", doc.RootElement.GetProperty("callables")[0].GetProperty("overrides").GetString());
            Assert.Equal("/repo", doc.RootElement.GetProperty("sourceRoot").GetString());
        }
        var back = JsonSerializer.Deserialize<ProgramSnapshot>(json, options)!;
        var backType = Assert.Single(back.Types);
        var backAttribute = Assert.Single(backType.Attributes);
        Assert.Equal("RouteAttribute", backAttribute.Name);
        Assert.Equal(new[] { "\"orders\"" }, backAttribute.Arguments);
        Assert.Equal(new NamedArgumentFact("Name", "\"list\""), Assert.Single(backAttribute.NamedArguments));
        Assert.Equal(("Shop", DeclaredTypeKind.Record, TypeModifiers.Sealed | TypeModifiers.Generic, "Project|T:Shop.Derived"), (backType.Namespace, backType.Kind, backType.Modifiers, backType.Key));
        Assert.Equal((new TypeId("Base"), new TypeId("Outer")), (backType.BaseClass, backType.ContainingType));
        var backRun = Assert.Single(back.Callables);
        Assert.Equal((CallableModifiers.Override | CallableModifiers.Sealed, new CallableId("BaseRun"), "Project|M:Run"), (backRun.Modifiers, backRun.Overrides, backRun.Key));
        Assert.Equal("/repo", back.SourceRoot);
    }

    [Fact]
    public void Evidence_path_is_relative_to_root_with_forward_slashes_or_unchanged_outside_it()
    {
        var inside = Evidence with { Path = "/repo/src/App/Program.cs" };
        Assert.Equal("src/App/Program.cs", inside.RelativeTo("/repo"));
        Assert.Equal("src/App/Program.cs", inside.RelativeTo("/repo/"));
        Assert.Equal("Program.cs", inside.RelativeTo("/repo/src/App"));
        Assert.Equal("/other/Program.cs", (Evidence with { Path = "/other/Program.cs" }).RelativeTo("/repo"));
        Assert.Equal("/repository/Program.cs", (Evidence with { Path = "/repository/Program.cs" }).RelativeTo("/repo"));
        Assert.Equal("..data/Program.cs", (Evidence with { Path = "/repo/..data/Program.cs" }).RelativeTo("/repo"));
        Assert.Equal("Example.cs", Evidence.RelativeTo("/repo"));
        Assert.Equal("/repo/src/App/Program.cs", inside.RelativeTo(""));
    }

    [Fact]
    public void Diamond_preserves_each_witness_without_global_visited_pruning()
    {
        var snapshot = Snapshot([Method("Action"), Method("Left"), Method("Right"), Method("Shared")],
            [Call("Action", "Right", "ar"), Call("Right", "Shared", "rs"), Call("Action", "Left", "al"), Call("Left", "Shared", "ls")], [Throw("Shared")]);
        var chains = CallChainQuery.Enumerate(snapshot, [new("Action")], new());
        Assert.Equal(new[] { "Left,Shared", "Right,Shared" }, chains.Chains.Select(x => string.Join(',', x.Steps.Select(s => s.Target!.Value.Value))));
        Assert.All(chains.Chains, x => Assert.Equal(ChainEnd.Leaf, x.End));
        var exceptions = ExceptionQuery.Analyze(snapshot, [new("Action")], new());
        Assert.Equal(new[] { "Action,Left,Shared", "Action,Right,Shared" }, exceptions.Results.Select(x => string.Join(',', x.Path.Select(p => p.Value))));
        Assert.All(exceptions.Results, x => Assert.Equal(ExceptionStatus.MayEscape, x.Status));
    }

    [Fact]
    public void Cycle_is_path_local_and_does_not_hide_sibling_leaf()
    {
        var snapshot = Snapshot([Method("Action"), Method("Loop"), Method("Leaf")],
            [Call("Action", "Loop", "loop"), Call("Loop", "Action", "back"), Call("Action", "Leaf", "leaf")]);
        var result = CallChainQuery.Enumerate(snapshot, [new("Action")], new());
        Assert.Equal(2, result.Chains.Length);
        Assert.Contains(result.Chains, x => x.End == ChainEnd.Cycle && x.Steps.Length == 2);
        Assert.Contains(result.Chains, x => x.End == ChainEnd.Leaf && x.Steps.Length == 1);
        Assert.Contains(ExceptionQuery.Analyze(snapshot, [new("Action")], new()).Results,
            x => x.Status == ExceptionStatus.Unknown && x.Reason.Contains("Cycle") && x.ReasonCode == ExceptionReason.Cycle);
    }

    [Fact]
    public void Paths_stop_at_dispatch_with_the_declared_binding()
    {
        var snapshot = Snapshot([Method("Action"), Method("Virtual"), Method("Leaf")],
            [Call("Action", "Virtual", "av", DispatchKind.Virtual), Call("Virtual", "Leaf", "vl")]);
        var chain = Assert.Single(CallChainQuery.Enumerate(snapshot, [new("Action")], new()).Chains);
        var step = Assert.Single(chain.Steps);
        Assert.Equal(new CallableId("Virtual"), step.Target);
        Assert.Equal(DispatchKind.Virtual, step.Dispatch);
        Assert.Equal(ChainEnd.DispatchBoundary, chain.End);
    }

    [Fact]
    public void Exact_output_and_work_budget_at_natural_end_is_not_truncation()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "al")], [Throw("Leaf")]);
        var chains = CallChainQuery.Enumerate(snapshot, [new("Action")], new(MaxChains: 1, MaxExpansions: 1));
        Assert.Single(chains.Chains);
        Assert.False(chains.OutputLimitReached);
        Assert.False(chains.WorkLimitReached);
        var exceptions = ExceptionQuery.Analyze(snapshot, [new("Action")], new(MaxResults: 1, MaxExpansions: 1));
        Assert.Single(exceptions.Results);
        Assert.False(exceptions.OutputLimitReached);
        Assert.False(exceptions.WorkLimitReached);
    }

    [Fact]
    public void Depth_and_work_stops_report_unknown_instead_of_claiming_no_exceptions()
    {
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am"), Call("Middle", "Leaf", "ml")], [Throw("Leaf")]);
        var depth = ExceptionQuery.Analyze(snapshot, [new("Action")], new(MaxDepth: 1));
        Assert.True(depth.DepthLimitReached);
        Assert.Equal(ExceptionStatus.Unknown, Assert.Single(depth.Results).Status);
        var work = ExceptionQuery.Analyze(snapshot, [new("Action")], new(MaxExpansions: 1));
        Assert.True(work.WorkLimitReached);
        Assert.Equal(1, work.ExpandedSites);
    }

    [Theory]
    [InlineData(HandlerOutcome.Rethrows)]
    [InlineData(HandlerOutcome.Replaces)]
    public void Changed_exception_retains_subsequent_interception(HandlerOutcome outcome)
    {
        var next = Throw("Middle", "next") with { CatchId = new CatchId("inner"), Kind = outcome == HandlerOutcome.Rethrows ? ThrowKind.Rethrow : ThrowKind.Explicit };
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am", regions: ["outer-region"]), Call("Middle", "Leaf", "ml", regions: ["inner-region"])],
            [Throw("Leaf"), next], [Handler("inner", "Middle", outcome, "next"), Handler("outer", "Action", HandlerOutcome.Intercepts)],
            [Region("inner-region", "Middle", "inner"), Region("outer-region", "Action", "outer")]);
        var witness = AnalyzeOrigin(snapshot);
        if (outcome == HandlerOutcome.Replaces)
        {
            // The original ends at the replacing handler; the replacement is an origin of its own.
            Assert.Equal(ExceptionStatus.Intercepted, witness.Status);
            Assert.Equal(new[] { new CatchId("inner") }, witness.HandlerIds);
            return;
        }
        Assert.Equal(ExceptionStatus.RethrownOrReplaced, witness.Status);
        Assert.Equal(ExceptionStatus.Intercepted, witness.SubsequentStatus);
        Assert.Equal(new ThrowSiteId("next"), witness.SubsequentOrigin);
        Assert.Equal(new[] { new CatchId("inner"), new CatchId("outer") }, witness.HandlerIds);
    }

    [Fact]
    public void Changed_exception_retains_subsequent_filter_uncertainty()
    {
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am", regions: ["outer-region"]), Call("Middle", "Leaf", "ml", regions: ["inner-region"])],
            [Throw("Leaf"), Throw("Middle", "next") with { Kind = ThrowKind.Rethrow, CatchId = new CatchId("inner") }],
            [Handler("inner", "Middle", HandlerOutcome.Rethrows, "next"), Handler("outer", "Action", HandlerOutcome.Intercepts, filter: "enabled")],
            [Region("inner-region", "Middle", "inner"), Region("outer-region", "Action", "outer")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.RethrownOrReplaced, witness.Status);
        Assert.Equal(ExceptionStatus.MayEscape, witness.SubsequentStatus);
        Assert.Equal(new[] { new CatchId("outer") }, witness.PossibleHandlers);
    }

    [Fact]
    public void Unbound_exception_type_does_not_skip_typed_handler()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"]) with { StaticExceptionType = null }],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: Type("OtherException"))], [Region("try", "Action", "catch")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Unknown, witness.Status);
        Assert.Equal(new[] { new CatchId("catch") }, witness.HandlerIds);
        Assert.Equal(new[] { new CatchId("catch") }, witness.PossibleHandlers);
        Assert.Equal(ExceptionReason.UnboundExceptionType, witness.ReasonCode);
    }

    [Fact]
    public void Origin_inside_a_handler_propagates_like_any_other_throw()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action") with { CatchId = new CatchId("catch") }],
            [Handler("catch", "Action", HandlerOutcome.Unknown)]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.MayEscape, witness.Status);
        Assert.Equal(new ThrowSiteId("origin"), witness.Origin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Async_void_and_custom_awaitable_propagation_are_unknown(bool awaited)
    {
        var snapshot = Snapshot([Method("Action"), Method("Operation") with { IsAsync = !awaited }],
            [Call("Action", "Operation", "operation", awaited: awaited)], [Throw("Operation")]);
        Assert.Equal(ExceptionStatus.Unknown, AnalyzeOrigin(snapshot).Status);
    }

    [Fact]
    public void Missing_root_body_and_absent_target_are_visible()
    {
        var missingBody = Snapshot([Method("Action") with { Body = BodyStatus.MetadataOnly }], []);
        Assert.Equal(ExceptionStatus.Unknown, Assert.Single(ExceptionQuery.Analyze(missingBody, [new("Action")], new()).Results).Status);
        var missingTarget = Snapshot([Method("Action")], [Call("Action", "Absent", "missing")]);
        Assert.Equal(ChainEnd.UnknownTarget, Assert.Single(CallChainQuery.Enumerate(missingTarget, [new("Action")], new()).Chains).End);
    }

    [Theory]
    [InlineData(true, ExceptionStatus.MayEscape)]
    [InlineData(false, ExceptionStatus.MayEscape)]
    public void Exact_and_base_typed_origins_have_distinct_narrow_handler_matching(bool exact, ExceptionStatus expected)
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"]) with { IsExactType = exact }],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: Type("DerivedException"))], [Region("try", "Action", "catch")]);
        snapshot = snapshot with { Types = snapshot.Types.Add(new(new("DerivedException"), Project, "DerivedException", "Public", [ExceptionType], [], [])) };
        Assert.Equal(expected, AnalyzeOrigin(snapshot).Status);
    }

    [Fact]
    public void Nonexact_static_type_matching_base_handler_is_intercepted()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"]) with { IsExactType = false }],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: ExceptionType)], [Region("try", "Action", "catch")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Intercepted, witness.Status);
        Assert.False(witness.IsExactType);
    }

    [Fact]
    public void Missing_type_hierarchy_is_unknown_instead_of_false_incompatible_match()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", type: Type("UnindexedException"), regions: ["try"])],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: Type("OtherException"))], [Region("try", "Action", "catch")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.MayEscape, witness.Status);
        Assert.Equal(new[] { new CatchId("catch") }, witness.PossibleHandlers);
    }

    [Fact]
    public void Same_display_name_in_different_assemblies_does_not_match_handler()
    {
        var caught = ExceptionType with { Declaration = new TypeId("OtherAssembly|Exception") };
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"])],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: caught)], [Region("try", "Action", "catch")]);
        Assert.Equal(ExceptionStatus.MayEscape, AnalyzeOrigin(snapshot).Status);
    }

    [Fact]
    public void Exact_construction_beneath_base_conversion_matches_derived_handler_and_preserves_static_type()
    {
        var derived = Type("DerivedException");
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"]) with { ExactExceptionType = derived }],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: derived)], [Region("try", "Action", "catch")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Intercepted, witness.Status);
        Assert.Equal("Exception", witness.StaticExceptionType);
        Assert.Equal("DerivedException", witness.ExactExceptionType);
        Assert.Equal("DerivedException", witness.ExceptionType);
    }

    [Fact]
    public void Different_constructed_generic_arguments_are_not_false_matching_handlers()
    {
        var thrown = new TypeUse(new("GenericException"), "GenericException<int>", NullabilityKind.NotAnnotated, [Type("int")]);
        var caught = thrown with { DisplayName = "GenericException<string>", Components = [Type("string")] };
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", type: thrown, regions: ["try"])],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: caught)], [Region("try", "Action", "catch")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.MayEscape, witness.Status);
        Assert.Empty(witness.HandlerIds);
    }

    [Fact]
    public void Finally_blocks_prevent_claiming_successful_interception()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"])],
            [Handler("catch", "Action", HandlerOutcome.Intercepts)], [Region("try", "Action", "catch") with { FinallyMayThrow = true }]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Unknown, witness.Status);
        Assert.Contains("Finally", witness.Reason);
        Assert.Equal(ExceptionReason.FinallyUnsupported, witness.ReasonCode);
    }

    [Theory]
    [InlineData(true, ExceptionStatus.Intercepted)]
    [InlineData(false, ExceptionStatus.Unknown)]
    public void Task_exception_requires_direct_observation_before_caller_handler(bool awaited, ExceptionStatus expected)
    {
        var snapshot = Snapshot([Method("Action"), Method("Operation") with { ReturnsTask = true, IsAsync = true }],
            [Call("Action", "Operation", "operation", regions: ["try"], awaited: awaited)], [Throw("Operation")],
            [Handler("catch", "Action", HandlerOutcome.Intercepts)], [Region("try", "Action", "catch")]);
        Assert.Equal(expected, AnalyzeOrigin(snapshot).Status);
    }

    [Fact]
    public void Duplicate_relationship_keeps_evidence_and_charges_each_site_to_work_budget()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "first"), Call("Action", "Leaf", "second")]);
        var result = CallChainQuery.Enumerate(snapshot, [new("Action")], new(MaxExpansions: 2));
        Assert.Equal(new[] { new CallSiteId("first"), new CallSiteId("second") }, Assert.Single(Assert.Single(result.Chains).Steps).EvidenceSites);
        Assert.Equal(2, result.ExpandedSites);
        Assert.False(result.WorkLimitReached);
        Assert.True(CallChainQuery.Enumerate(snapshot, [new("Action")], new(MaxExpansions: 1)).WorkLimitReached);
    }

    [Fact]
    public void Query_options_and_cancellation_are_validated()
    {
        var snapshot = Snapshot([Method("Action")], []);
        Assert.Throws<ArgumentException>(() => ExceptionQuery.Analyze(snapshot, [new("Action")], new(MaxExpansions: 0)));
        Assert.Throws<ArgumentException>(() => CallChainQuery.Enumerate(snapshot, [new("Action")], new(MaxChains: 0)));
        Assert.Throws<ArgumentException>(() => CallChainQuery.Enumerate(snapshot, [new("Action")], new(MaxDepth: -1)));
        Assert.Throws<OperationCanceledException>(() => ExceptionQuery.Analyze(snapshot, [new("Action")], new(), new CancellationToken(true)));
        Assert.Throws<OperationCanceledException>(() => CallChainQuery.Enumerate(snapshot, [new("Action")], new(), new CancellationToken(true)));
    }
}
