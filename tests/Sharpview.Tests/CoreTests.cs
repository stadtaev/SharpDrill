using System.Collections.Immutable;
using Sharpview.Core;
using Xunit;

namespace Sharpview.Tests;

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
            [], Evidence, false, regions.IsDefault ? [] : regions, awaited);
    private static ThrowSiteFact Throw(string owner, string id = "origin", TypeUse? type = null, ImmutableArray<string> regions = default) =>
        new(id, new(owner), ThrowKind.Explicit, type ?? ExceptionType, Evidence, regions.IsDefault ? [] : regions, true);
    private static ProgramSnapshot Snapshot(CallableFact[] methods, CallSiteFact[] calls, ThrowSiteFact[]? origins = null,
        CatchFact[]? catches = null, ExceptionRegionFact[]? regions = null) => new([], [
            new(new("Exception"), Project, "Exception", "Public", [], [], []),
            new(new("OtherException"), Project, "OtherException", "Public", [], [], [])],
            methods.ToImmutableArray(), calls.ToImmutableArray(), [], (origins ?? []).ToImmutableArray(), [],
            (catches ?? []).ToImmutableArray(), (regions ?? []).ToImmutableArray(), [], []);
    private static CatchFact Handler(string id, string owner, HandlerOutcome outcome, string? subsequent = null,
        TypeUse? caught = null, string? filter = null) => new(id, new(owner), caught, filter, Evidence, Evidence, outcome, subsequent);
    private static ExceptionRegionFact Region(string id, string owner, params string[] catches) => new(id, new(owner), Evidence, catches.ToImmutableArray(), false);
    private static ExceptionWitness AnalyzeOrigin(ProgramSnapshot snapshot) => Assert.Single(
        ExceptionQuery.Analyze(snapshot, [new("Action")], new()).Results, x => x.Origin == "origin");

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
            x => x.Status == ExceptionStatus.Unknown && x.Reason.Contains("Cycle"));
    }

    [Theory]
    [InlineData(ChainDispatchPolicy.Declared, false)]
    [InlineData(ChainDispatchPolicy.Direct, true)]
    public void Dispatch_policy_preserves_binding_and_marks_excluded_target(ChainDispatchPolicy policy, bool excluded)
    {
        var snapshot = Snapshot([Method("Action"), Method("Virtual"), Method("Leaf")],
            [Call("Action", "Virtual", "av", DispatchKind.Virtual), Call("Virtual", "Leaf", "vl")]);
        var chain = Assert.Single(CallChainQuery.Enumerate(snapshot, [new("Action")], new(Dispatch: policy)).Chains);
        var step = Assert.Single(chain.Steps);
        Assert.Equal(new CallableId("Virtual"), step.Target);
        Assert.Equal(excluded, step.IsExcludedDispatch);
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
        var exceptions = ExceptionQuery.Analyze(snapshot, [new("Action")], new(MaxChains: 1, MaxExpansions: 1));
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
        var next = Throw("Middle", "next") with { CatchId = "inner", Kind = outcome == HandlerOutcome.Rethrows ? ThrowKind.Rethrow : ThrowKind.Explicit };
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am", regions: ["outer-region"]), Call("Middle", "Leaf", "ml", regions: ["inner-region"])],
            [Throw("Leaf"), next], [Handler("inner", "Middle", outcome, "next"), Handler("outer", "Action", HandlerOutcome.Intercepts)],
            [Region("inner-region", "Middle", "inner"), Region("outer-region", "Action", "outer")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.RethrownOrReplaced, witness.Status);
        Assert.Equal(ExceptionStatus.Intercepted, witness.SubsequentStatus);
        Assert.Equal("next", witness.SubsequentOrigin);
        Assert.Equal(new[] { "inner", "outer" }, witness.HandlerIds);
    }

    [Fact]
    public void Changed_exception_retains_subsequent_filter_uncertainty()
    {
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Leaf")],
            [Call("Action", "Middle", "am", regions: ["outer-region"]), Call("Middle", "Leaf", "ml", regions: ["inner-region"])],
            [Throw("Leaf"), Throw("Middle", "next") with { Kind = ThrowKind.Rethrow, CatchId = "inner" }],
            [Handler("inner", "Middle", HandlerOutcome.Rethrows, "next"), Handler("outer", "Action", HandlerOutcome.Intercepts, filter: "enabled")],
            [Region("inner-region", "Middle", "inner"), Region("outer-region", "Action", "outer")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.RethrownOrReplaced, witness.Status);
        Assert.Equal(ExceptionStatus.Unknown, witness.SubsequentStatus);
        Assert.Contains("filter", witness.Reason);
    }

    [Fact]
    public void Unbound_exception_type_does_not_skip_typed_handler()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"]) with { StaticExceptionType = null }],
            [Handler("catch", "Action", HandlerOutcome.Intercepts, caught: Type("OtherException"))], [Region("try", "Action", "catch")]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Unknown, witness.Status);
        Assert.Equal(new[] { "catch" }, witness.HandlerIds);
        Assert.Contains("matching", witness.Reason);
    }

    [Fact]
    public void Handler_origin_is_retained_with_scoped_uncertainty()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action") with { CatchId = "catch" }],
            [Handler("catch", "Action", HandlerOutcome.Unknown)]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Unknown, witness.Status);
        Assert.Equal("origin", witness.Origin);
        Assert.Contains("inside a handler", witness.Reason);
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
    [InlineData(false, ExceptionStatus.Unknown)]
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
        Assert.Equal(ExceptionStatus.Unknown, AnalyzeOrigin(snapshot).Status);
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
        Assert.Equal(ExceptionStatus.Unknown, AnalyzeOrigin(snapshot).Status);
    }

    [Fact]
    public void Finally_blocks_prevent_claiming_successful_interception()
    {
        var snapshot = Snapshot([Method("Action")], [], [Throw("Action", regions: ["try"])],
            [Handler("catch", "Action", HandlerOutcome.Intercepts)], [Region("try", "Action", "catch") with { HasFinally = true }]);
        var witness = AnalyzeOrigin(snapshot);
        Assert.Equal(ExceptionStatus.Unknown, witness.Status);
        Assert.Contains("Finally", witness.Reason);
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
        Assert.Throws<ArgumentException>(() => CallChainQuery.Enumerate(snapshot, [new("Action")], new(Dispatch: (ChainDispatchPolicy)42)));
        Assert.Throws<ArgumentException>(() => CallChainQuery.Enumerate(snapshot, [new("Action")], new(MaxDepth: -1)));
        Assert.Throws<OperationCanceledException>(() => ExceptionQuery.Analyze(snapshot, [new("Action")], new(), new CancellationToken(true)));
        Assert.Throws<OperationCanceledException>(() => CallChainQuery.Enumerate(snapshot, [new("Action")], new(), new CancellationToken(true)));
    }
}
