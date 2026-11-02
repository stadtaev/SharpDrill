using System.Collections.Immutable;
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
    // Where the throw "origin" ends when analysed from "Action": exactly one ending in these snapshots.
    private static ExceptionWitness AnalyzeOrigin(ProgramSnapshot snapshot)
    {
        var index = new ProgramIndex(snapshot);
        return Assert.Single(ExceptionHandlingQuery.Fates(index, ExceptionSummaryQuery.Analyze(index, [new("Action")]), new("origin")));
    }

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
    public void A_miss_reports_every_fate_of_what_got_past_it_across_actions()
    {
        // Middle's catch misses; one action lets the exception escape, the other catches it further up.
        var snapshot = Snapshot([Method("Action"), Method("Other"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am"), Call("Other", "Middle", "om", regions: ["outer"]), Call("Middle", "Leaf", "ml", regions: ["inner"])],
            [Throw("Leaf")], [Handler("narrow", "Middle", HandlerOutcome.Intercepts, caught: Type("OtherException")), Handler("all", "Other", HandlerOutcome.Intercepts)],
            [Region("inner", "Middle", "narrow"), Region("outer", "Other", "all")]);
        var index = new ProgramIndex(snapshot);
        var summary = ExceptionSummaryQuery.Analyze(index, [new("Action"), new("Other")]);
        var miscaught = Assert.Single(ExceptionHandlingQuery.Miscaught(index, summary));
        Assert.Equal(new ExceptionRegionId("inner"), miscaught.Missed.Region);
        Assert.Equal([ExceptionStatus.Intercepted, ExceptionStatus.MayEscape], miscaught.Outcomes.AsEnumerable());
        Assert.Equal([new CallableId("Action")], miscaught.EscapesFrom.AsEnumerable());
        Assert.Equal(["Action", "Middle", "Leaf"], miscaught.Example.Path.Select(x => x.Value));
        Assert.Equal(new CatchId("all"), Assert.Single(ExceptionHandlingQuery.Caught(index, summary)).Catch);
    }

    [Fact]
    public void A_catch_above_recursion_is_found_with_an_acyclic_example()
    {
        var snapshot = Snapshot([Method("Action"), Method("Loop"), Method("Leaf")],
            [Call("Action", "Loop", "al", regions: ["try"]), Call("Loop", "Loop", "again"), Call("Loop", "Leaf", "ll")],
            [Throw("Leaf")], [Handler("catch", "Action", HandlerOutcome.Intercepts)], [Region("try", "Action", "catch")]);
        var index = new ProgramIndex(snapshot);
        var summary = ExceptionSummaryQuery.Analyze(index, [new("Action")]);
        var caught = Assert.Single(ExceptionHandlingQuery.Caught(index, summary));
        Assert.Equal(["Action", "Loop", "Leaf"], caught.Example.Path.Select(x => x.Value));
        Assert.True(summary.IsComplete);
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
    public void Owned_exception_gap_stays_attached_to_its_reached_callable()
    {
        var snapshot = Snapshot([Method("Action"), Method("Leaf")], [Call("Action", "Leaf", "call")]) with
        { Gaps = [new AnalysisGap(GapKind.Unsupported, Project, new CallableId("Leaf"), "Unsupported source body", Evidence)] };
        var result = ExceptionSummaryQuery.Analyze(snapshot, [new("Action")]);
        Assert.Empty(result.GlobalGaps);
        Assert.False(result.IsComplete);
        Assert.Equal(ExceptionReason.ExtractionGap, Assert.Single(result.Roots[new CallableId("Action")].Unknowns).ReasonCode);
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
        Assert.Equal(ExceptionReason.MissingBody, Assert.Single(ExceptionSummaryQuery.Analyze(missingBody, [new("Action")]).Roots[new CallableId("Action")].Unknowns).ReasonCode);
        var missingTarget = Snapshot([Method("Action")], [Call("Action", "Absent", "missing")]);
        Assert.Equal(CallTargetKind.Unresolved, new ProgramIndex(missingTarget).CallGraph.Resolve(new("missing")).Kind);
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
    public void Query_options_and_cancellation_are_validated()
    {
        var snapshot = Snapshot([Method("Action")], []);
        Assert.Throws<ArgumentException>(() => ExceptionSummaryQuery.Analyze(snapshot, [new("Action")], new(MaxWork: 0)));
        Assert.Throws<OperationCanceledException>(() => ExceptionSummaryQuery.Analyze(snapshot, [new("Action")], new(), new CancellationToken(true)));
    }
}
