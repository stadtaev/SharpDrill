using System.Collections.Immutable;
using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

// What the exception analyses do with library calls, compiler-provided members and interface/virtual calls.
public sealed class ExceptionScopeTests
{
    private static readonly CompilationId Project = new("test-project");
    private static readonly SourceEvidence Evidence = new("Example.cs", "checksum", 0, 10, 1, 1, false);
    private static readonly TypeUse ExceptionType = new(new TypeId("Exception"), "Exception", NullabilityKind.NotAnnotated, []);
    private static readonly CallableId Action = new("Action");

    private static CallableFact Method(string name, BodyStatus body = BodyStatus.Source) => new(new(name), Project, null, null, CallableKind.Method,
        name + "()", "Public", ExceptionType, [], [], body, [Evidence]);
    private static CallSiteFact Call(string owner, string target, string id, DispatchKind dispatch = DispatchKind.Direct, string? region = null) =>
        new(new(id), new(owner), new CallableId(target), target + "()", dispatch, [], Evidence, false,
            region is null ? [] : [new ExceptionRegionId(region)]);
    private static ThrowSiteFact Throw(string owner, string id = "origin") => new(new(id), new(owner), ThrowKind.Explicit, ExceptionType, Evidence, [], true);
    private static ProgramSnapshot Snapshot(CallableFact[] methods, CallSiteFact[] calls, ThrowSiteFact[]? throws = null, ImplementationFact[]? implementations = null) => new()
    {
        Types = [new(new("Exception"), Project, "Exception", "Public", [], [], [])],
        Callables = [.. methods], CallSites = [.. calls], ThrowSites = [.. throws ?? []], Implementations = [.. implementations ?? []],
    };
    private static ImplementationFact Implements(string declared, string implementation) => new(new(declared), new(implementation), ImplementationKind.Interface);
    private static ProgramSnapshot Guarded(ProgramSnapshot snapshot) => snapshot with
    {
        Catches = [new CatchFact(new("c"), Action, null, null, Evidence, Evidence, HandlerOutcome.Intercepts, null)],
        ExceptionRegions = [new ExceptionRegionFact(new("r"), Action, Evidence, [new CatchId("c")], false)],
    };

    [Fact]
    public void Library_callees_are_counted_and_never_reported_as_unknown()
    {
        var snapshot = Snapshot([Method("Action"), Method("Middle"), Method("Lib", BodyStatus.MetadataOnly)],
            [Call("Action", "Middle", "am"), Call("Middle", "Lib", "ml")]);
        var detailed = ExceptionQuery.Analyze(snapshot, [Action], new());
        Assert.Empty(detailed.Results);
        Assert.Equal([new CallableId("Lib")], detailed.LibraryCalls.AsEnumerable());
        Assert.True(detailed.IsComplete);
        var summary = ExceptionSummaryQuery.Analyze(snapshot, [Action]);
        Assert.Empty(summary.Roots[Action].Unknowns);
        Assert.True(summary.IsComplete);
        Assert.Equal([new CallableId("Lib")], summary.LibraryCallsFrom(Action).AsEnumerable());
        Assert.Empty(summary.LibraryCallsFrom(new("Middle")).Except([new CallableId("Lib")]));
    }

    [Fact]
    public void Strict_reports_third_party_calls_that_no_catch_meets_whatever_the_catch_type()
    {
        var vendor = new CompilationId("metadata|Vendor");
        var other = new TypeUse(new TypeId("Other"), "Other", NullabilityKind.NotAnnotated, []);
        var snapshot = Snapshot([Method("Action"), Method("Bare"), Method("Narrow"), Method("Logs"), Method("Send", BodyStatus.MetadataOnly) with { Compilation = vendor },
                Method("Console", BodyStatus.MetadataOnly)],
            [Call("Action", "Bare", "ab"), Call("Action", "Narrow", "an"), Call("Action", "Logs", "al"), Call("Action", "Console", "ac"),
             Call("Bare", "Send", "bare"), Call("Narrow", "Send", "narrow", region: "n"), Call("Logs", "Send", "logs", region: "l")]) with
        {
            Catches = [new(new("narrow"), new("Narrow"), other, null, Evidence, Evidence, HandlerOutcome.Intercepts, null),
                new(new("log"), new("Logs"), null, null, Evidence, Evidence, HandlerOutcome.Rethrows, null)],
            ExceptionRegions = [new(new("n"), new("Narrow"), Evidence, [new CatchId("narrow")], false), new(new("l"), new("Logs"), Evidence, [new CatchId("log")], false)],
            ThirdPartyAssemblies = [vendor],
        };
        var strict = ExceptionSummaryQuery.Analyze(snapshot, [Action], new(ThirdPartyCallOrigins: true));
        // Any catch wraps the call, whatever its type; a catch that only rethrows does not, and .NET calls are not third-party.
        var escapes = strict.Roots[Action].Escapes;
        Assert.Equal(["bare", "logs"], escapes.Select(x => strict.ThirdPartyCalls[x.Origin].Value).Distinct().Order());
        Assert.Equal(["Action", "Bare"], strict.GetWitness(Action, escapes.First(x => strict.ThirdPartyCalls[x.Origin].Value == "bare")).Path.Select(x => x.Value));
        Assert.True(strict.IsComplete);
        Assert.Empty(ExceptionSummaryQuery.Analyze(snapshot, [Action]).Roots[Action].Escapes);
    }

    [Fact]
    public void A_root_without_a_body_is_still_unknown_in_both_analyses()
    {
        var snapshot = Snapshot([Method("Action", BodyStatus.MetadataOnly)], []);
        Assert.Equal(ExceptionReason.MissingBody, Assert.Single(ExceptionQuery.Analyze(snapshot, [Action], new()).Results).ReasonCode);
        Assert.Equal(ExceptionReason.MissingBody, Assert.Single(ExceptionSummaryQuery.Analyze(snapshot, [Action]).Roots[Action].Unknowns).ReasonCode);
    }

    [Fact]
    public void Compiler_provided_callees_hold_no_code_to_analyse()
    {
        var snapshot = Snapshot([Method("Action"), Method("Auto", BodyStatus.Synthesized)], [Call("Action", "Auto", "aa")], [Throw("Action")]);
        var detailed = ExceptionQuery.Analyze(snapshot, [Action], new());
        Assert.Equal(ExceptionStatus.MayEscape, Assert.Single(detailed.Results).Status);
        Assert.Empty(detailed.LibraryCalls);
        var summary = ExceptionSummaryQuery.Analyze(snapshot, [Action]);
        Assert.Empty(summary.Roots[Action].Unknowns);
        Assert.Single(summary.Roots[Action].Escapes);
        Assert.Empty(summary.LibraryCallsFrom(Action));
    }

    [Fact]
    public void A_dispatch_call_to_a_library_member_nothing_overrides_is_a_library_call()
    {
        var snapshot = Snapshot([Method("Action"), Method("ILib", BodyStatus.MetadataOnly)], [Call("Action", "ILib", "ai", DispatchKind.Interface)]);
        var detailed = ExceptionQuery.Analyze(snapshot, [Action], new());
        Assert.Empty(detailed.Results);
        Assert.Equal([new CallableId("ILib")], detailed.LibraryCalls.AsEnumerable());
        var summary = ExceptionSummaryQuery.Analyze(snapshot, [Action]);
        Assert.Empty(summary.Roots[Action].Unknowns);
        Assert.Equal([new CallableId("ILib")], summary.LibraryCallsFrom(Action).AsEnumerable());
    }

    private static ProgramSnapshot Injected() => Snapshot([Method("Action"), Method("I", BodyStatus.NoBody), Method("Impl")],
        [Call("Action", "I", "ai", DispatchKind.Interface)], [Throw("Impl")], [Implements("I", "Impl")]);

    [Fact]
    public void An_interface_call_with_a_source_implementation_stops_unless_candidates_are_followed()
    {
        var detailed = ExceptionQuery.Analyze(Injected(), [Action], new());
        Assert.Equal(ExceptionReason.DispatchBoundary, Assert.Single(detailed.Results).ReasonCode);
        var summary = ExceptionSummaryQuery.Analyze(Injected(), [Action]);
        Assert.Empty(summary.Roots[Action].Escapes);
        Assert.Equal(ExceptionReason.DispatchBoundary, Assert.Single(summary.Roots[Action].Unknowns).ReasonCode);
    }

    [Fact]
    public void Followed_candidates_reach_the_implementation_and_are_marked_as_possibilities()
    {
        var detailed = ExceptionQuery.Analyze(Injected(), [Action], new(FollowCandidates: true));
        var witness = Assert.Single(detailed.Results);
        Assert.Equal(ExceptionStatus.MayEscape, witness.Status);
        Assert.True(witness.ViaCandidate);
        Assert.Equal([Action, new("Impl")], witness.Path.AsEnumerable());
        Assert.Equal([new CallSiteId("ai")], witness.Sites.AsEnumerable());

        var summary = ExceptionSummaryQuery.Analyze(Injected(), [Action], new(FollowCandidates: true));
        var escape = Assert.Single(summary.Roots[Action].Escapes);
        Assert.True(escape.ViaCandidate);
        Assert.Empty(summary.Roots[Action].Unknowns);
        var path = summary.GetWitness(Action, escape);
        Assert.Equal([Action, new("Impl")], path.Path.AsEnumerable());
        Assert.Equal([new CallSiteId("ai")], path.Sites.AsEnumerable());
    }

    [Fact]
    public void Following_candidates_without_any_source_implementation_stays_unknown_and_says_why()
    {
        var snapshot = Snapshot([Method("Action"), Method("I", BodyStatus.NoBody)], [Call("Action", "I", "ai", DispatchKind.Interface)]);
        var detailed = ExceptionQuery.Analyze(snapshot, [Action], new(FollowCandidates: true));
        Assert.Contains("no source implementation found", Assert.Single(detailed.Results).Reason);
        var summary = ExceptionSummaryQuery.Analyze(snapshot, [Action], new(FollowCandidates: true));
        Assert.Contains("no source implementation found", Assert.Single(summary.Roots[Action].Unknowns).Message);
    }

    [Fact]
    public void A_catch_around_the_interface_call_applies_to_what_the_implementation_throws()
    {
        var snapshot = Guarded(Snapshot([Method("Action"), Method("I", BodyStatus.NoBody), Method("Impl")],
            [Call("Action", "I", "ai", DispatchKind.Interface, region: "r")], [Throw("Impl")], [Implements("I", "Impl")]));
        var witness = Assert.Single(ExceptionQuery.Analyze(snapshot, [Action], new(FollowCandidates: true)).Results);
        Assert.Equal(ExceptionStatus.Intercepted, witness.Status);
        Assert.True(witness.ViaCandidate);
        Assert.Equal([new CatchId("c")], witness.HandlerIds.AsEnumerable());
        var caught = Assert.Single(ExceptionHandlingQuery.Caught(ExceptionQuery.Analyze(snapshot, [Action], new(FollowCandidates: true))));
        Assert.True(caught.ViaCandidate);
        var summary = ExceptionSummaryQuery.Analyze(snapshot, [Action], new(FollowCandidates: true));
        Assert.Empty(summary.Roots[Action].Escapes);
        Assert.Equal([new ThrowSiteId("origin")], summary.Crossings[new("ai")].InterceptedOrigins.AsEnumerable());
    }

    [Fact]
    public void A_proven_direct_path_replaces_the_same_effect_through_a_candidate()
    {
        var snapshot = Guarded(Snapshot([Method("Action"), Method("I", BodyStatus.NoBody), Method("Impl")],
            [Call("Action", "I", "ai", DispatchKind.Interface, region: "r"), Call("Action", "Impl", "ad", region: "r")], [Throw("Impl")], [Implements("I", "Impl")]));
        var caught = Assert.Single(ExceptionHandlingQuery.Caught(ExceptionQuery.Analyze(snapshot, [Action], new(FollowCandidates: true))));
        Assert.False(caught.ViaCandidate);
        var open = Snapshot([Method("Action"), Method("I", BodyStatus.NoBody), Method("Impl")],
            [Call("Action", "I", "ai", DispatchKind.Interface), Call("Action", "Impl", "ad")], [Throw("Impl")], [Implements("I", "Impl")]);
        var escape = Assert.Single(ExceptionSummaryQuery.Analyze(open, [Action], new(FollowCandidates: true)).Roots[Action].Escapes);
        Assert.False(escape.ViaCandidate);
    }

    [Fact]
    public void Uncertainty_witnesses_never_crowd_out_origin_witnesses()
    {
        var targets = Enumerable.Range(1, 5).Select(x => $"I{x}").ToArray();
        var snapshot = Snapshot([Method("Action"), Method("Leaf"), .. targets.Select(x => Method(x, BodyStatus.NoBody))],
            [.. targets.Select(x => Call("Action", x, "a" + x, DispatchKind.Interface)), Call("Action", "Leaf", "al")], [Throw("Leaf")]);
        var result = ExceptionQuery.Analyze(snapshot, [Action], new(MaxResults: 2));
        Assert.Single(result.Results, x => x.Origin is not null);
        Assert.Equal(2, result.Results.Count(x => x.Origin is null));
        Assert.True(result.UnknownLimitReached);
        Assert.False(result.OutputLimitReached);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void The_same_uncertainty_reached_by_several_paths_is_reported_once_per_action()
    {
        var snapshot = Snapshot([Method("Action"), Method("Left"), Method("Right"), Method("Shared", BodyStatus.NoBody)],
            [Call("Action", "Left", "al"), Call("Action", "Right", "ar"), Call("Left", "Shared", "ls"), Call("Right", "Shared", "rs")]);
        var result = ExceptionQuery.Analyze(snapshot, [Action], new());
        Assert.Equal(ExceptionReason.MissingBody, Assert.Single(result.Results).ReasonCode);
    }
}
