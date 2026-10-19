using System.Collections.Immutable;
using SharpView.Core;
using SharpView.Roslyn;
using Xunit;

namespace SharpView.Tests;

public sealed class ResearchFixture : IAsyncLifetime
{
    public static string Root => FindRoot();
    public static string Input => Path.Combine(Root, "tests/Fixtures/ResearchSolution/ResearchSolution.slnx");
    public ProgramSnapshot Snapshot { get; private set; } = null!;
    public async Task InitializeAsync() { MSBuildBootstrap.RegisterSdk(); Snapshot = await RoslynProgramLoader.LoadAsync(new(Input)); }
    public Task DisposeAsync() => Task.CompletedTask;
    private static string FindRoot() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "sharpview.slnx"))) d = d.Parent; return d?.FullName ?? throw new Exception("Repository not found"); }
}
public sealed class ResearchTests(ResearchFixture fixture) : IClassFixture<ResearchFixture>
{
    private ProgramSnapshot S => fixture.Snapshot;
    private ImmutableArray<ControllerActionFact> Actions => AspNetConventions.ControllerActions(new ProgramIndex(S));
    private CallableFact Method(string name) => Assert.Single(S.Callables, x => x.DisplaySignature == name);
    [Fact]
    public void Source_bindings_link_projects_and_preserve_locations()
    {
        var service = Method("Research.Application.Service.Load()");
        var call = Assert.Single(S.CallSites, x => x.Caller == service.Id);
        Assert.Equal(Method("Research.Infrastructure.Repository.Read()").Id, call.BoundTarget);
        Assert.Equal(DispatchKind.Direct, call.Dispatch);
        Assert.True(call.Evidence.StartLine > 0);
        Assert.True(File.Exists(call.Evidence.Path));
        Assert.Contains(S.BoundaryHandling, x => x.Registration == "UseExceptionHandler");
        Assert.Equal(12, Actions.Length);
        Assert.DoesNotContain(Actions, x => x.Method == Method("Research.Api.OrdersController.Helper()").Id);
    }
    [Theory]
    [InlineData("Get", ExceptionStatus.MayEscape)]
    [InlineData("Caught", ExceptionStatus.Intercepted)]
    [InlineData("Unrelated", ExceptionStatus.MayEscape)]
    [InlineData("Incompatible", ExceptionStatus.MayEscape)]
    [InlineData("Rethrow", ExceptionStatus.RethrownOrReplaced)]
    [InlineData("Replace", ExceptionStatus.RethrownOrReplaced)]
    [InlineData("Filter(bool)", ExceptionStatus.Unknown)]
    [InlineData("AwaitCaught", ExceptionStatus.Intercepted)]
    [InlineData("Unobserved", ExceptionStatus.Unknown)]
    [InlineData("Nested", ExceptionStatus.RethrownOrReplaced)]
    public void Exception_witness_uses_protected_call_site(string action, ExceptionStatus expected)
    {
        var signature = "Research.Api.OrdersController." + (action.Contains('(') ? action : action + "()");
        var result = ExceptionQuery.Analyze(S, [Method(signature).Id], new());
        var witness = Assert.Single(result.Results, x => x.Origin is not null && S.ThrowSites.Single(t => t.Id == x.Origin).Owner == Method("Research.Infrastructure.Repository." + (action == "AwaitCaught" || action == "Unobserved" ? "ReadAsync()" : "Read()")).Id);
        Assert.Equal(expected, witness.Status);
        Assert.Equal(3, witness.Path.Length);
        Assert.NotEmpty(witness.Evidence);
        if (action == "Replace") Assert.Equal("System.InvalidOperationException", witness.ExceptionType);
        if (action == "Nested") Assert.Equal(ExceptionStatus.Intercepted, witness.SubsequentStatus);
    }
    [Theory]
    [InlineData("Interface(Research.Infrastructure.IRepository)", "Interface", ExceptionReason.DispatchBoundary)]
    [InlineData("Recursive()", "Cycle", ExceptionReason.Cycle)]
    public void Uncertain_boundaries_are_visible(string action, string reason, ExceptionReason code)
    {
        var report = ExceptionQuery.Analyze(S, [Method("Research.Api.OrdersController." + action).Id], new());
        Assert.Contains(report.Results, x => x.Status == ExceptionStatus.Unknown && x.Reason.Contains(reason, StringComparison.OrdinalIgnoreCase) && x.ReasonCode == code);
        if (code == ExceptionReason.DispatchBoundary)
            Assert.Contains(report.Results, x => x.ReasonCode == code && x.Dispatch == DispatchKind.Interface);
    }
    // Same code as the "Writing your own rule" example in docs/overview.md; keeps the documentation honest.
    private static IEnumerable<Finding> NoDirectInfrastructure(ProgramIndex index, LayerMap layers)
    {
        var actions = AspNetConventions.ControllerActions(index).Select(x => x.Method).ToImmutableArray();
        foreach (var root in ReachabilityQuery.Analyze(index, actions, new ReachOptions(FollowCandidates: true)).Roots)
            foreach (var hit in root.Reached.Where(x => x.DirectDistance == 1 && layers.LayerOf(x.Callable) == "Infrastructure"))
                yield return new("no-direct-infrastructure", Verdict.Violation,
                    index.Callable(root.Root).DisplaySignature, $"Calls {index.Callable(hit.Callable).DisplaySignature} directly.",
                    [index.CallSite(hit.DirectWitness[0].Site).Evidence]);
    }
    [Fact]
    public void Documented_custom_rule_example_runs_on_the_fixture()
    {
        var index = new ProgramIndex(S);
        var layers = new LayerMap(index, new([new(LayerScope.Project, "Research.Infrastructure", "Infrastructure")]));
        var findings = NoDirectInfrastructure(index, layers).ToArray();
        // Controllers only call the application layer directly; mapping the application project as Infrastructure flips that.
        Assert.Empty(findings);
        var strict = new LayerMap(index, new([new(LayerScope.Project, "Research.Application", "Infrastructure")]));
        Assert.Contains(NoDirectInfrastructure(index, strict), x => x.Verdict == Verdict.Violation && x.Subject == "Research.Api.OrdersController.Get()");
    }
    [Fact]
    public void Documented_rule_does_not_treat_candidate_shortcut_as_a_direct_call()
    {
        var compilation = new CompilationId("project");
        var evidence = new SourceEvidence("Example.cs", "hash", 0, 1, 1, 1, false);
        var voidType = new TypeUse(null, "void", NullabilityKind.NotAnnotated, []);
        CallableFact Callable(string id, string type, BodyStatus body = BodyStatus.Source) =>
            new(new(id), compilation, new TypeId(type), null, CallableKind.Method, type + "." + id + "()",
                "Public", voidType, [], [], body, []);
        CallSiteFact Site(string id, string from, string to, DispatchKind dispatch = DispatchKind.Direct) =>
            new(new(id), new(from), new CallableId(to), to, dispatch, [], evidence, false, []);
        TypeFact Type(string name) => new(new(name), compilation, name, "Public", [], [], []);
        var snapshot = new ProgramSnapshot
        {
            Types = [Type("Controller") with { Attributes = [new("Microsoft.AspNetCore.Mvc.ControllerAttribute", [], [])] },
                Type("Service"), Type("IRepo") with { Kind = DeclaredTypeKind.Interface }, Type("Repo")],
            Callables = [Callable("Action", "Controller"), Callable("Middle", "Service"),
                Callable("Declared", "IRepo", BodyStatus.NoBody), Callable("Implementation", "Repo")],
            CallSites = [Site("dispatch", "Action", "Declared", DispatchKind.Interface), Site("first", "Action", "Middle"),
                Site("second", "Middle", "Implementation")],
            Implementations = [new(new("Declared"), new("Implementation"), ImplementationKind.Interface)]
        };
        var index = new ProgramIndex(snapshot);
        var layers = new LayerMap(index, new([new(LayerScope.Type, "Repo", "Infrastructure")]));
        Assert.Empty(NoDirectInfrastructure(index, layers));
        var direct = Site("actual-direct", "Action", "Implementation") with { Evidence = evidence with { SpanStart = 17 } };
        index = new ProgramIndex(snapshot with { CallSites = snapshot.CallSites.Add(direct) });
        layers = new LayerMap(index, new([new(LayerScope.Type, "Repo", "Infrastructure")]));
        var violation = Assert.Single(NoDirectInfrastructure(index, layers));
        Assert.Equal(Verdict.Violation, violation.Verdict);
        Assert.Equal(17, Assert.Single(violation.Evidence).SpanStart);
    }

    [Fact]
    public void Detailed_fixture_exception_counts_exclude_global_gap_duplication()
    {
        var report = ExceptionQuery.Analyze(S, [.. Actions.Select(x => x.Method)], new());
        // Library calls and compiler-provided members are not uncertainty; what remains is genuine (filters, dispatch, cycles...).
        Assert.Equal(13, report.Results.Length);
        Assert.Equal(5, report.Results.Count(x => x.Status == ExceptionStatus.Unknown || x.SubsequentStatus == ExceptionStatus.Unknown));
        Assert.Equal(6, report.GlobalGaps.Length);
        Assert.False(report.IsComplete);
        Assert.DoesNotContain(report.Results, x => x.ReasonCode == ExceptionReason.ExtractionGap);
    }

    [Fact]
    public void Every_fixture_witness_has_a_reason_code_consistent_with_its_final_status()
    {
        var report = ExceptionQuery.Analyze(S, [.. Actions.Select(x => x.Method)], new());
        Assert.NotEmpty(report.Results);
        ExceptionReason[] outcomes = [ExceptionReason.InterceptedByHandler, ExceptionReason.MayEscape, ExceptionReason.ChangedMayEscape];
        foreach (var witness in report.Results)
        {
            var final = witness.SubsequentStatus ?? witness.Status;
            Assert.NotEqual(ExceptionReason.Unspecified, witness.ReasonCode);
            if (final == ExceptionStatus.Intercepted) Assert.Equal(ExceptionReason.InterceptedByHandler, witness.ReasonCode);
            else if (final == ExceptionStatus.MayEscape)
                Assert.Equal(witness.Status == ExceptionStatus.RethrownOrReplaced ? ExceptionReason.ChangedMayEscape : ExceptionReason.MayEscape, witness.ReasonCode);
            else Assert.DoesNotContain(witness.ReasonCode, outcomes);
            Assert.Equal(witness.ReasonCode == ExceptionReason.DispatchBoundary, witness.Dispatch is not null);
        }
    }
    [Fact]
    public void Interface_dispatch_has_a_declared_member_and_implementation_facts_are_candidates_only()
    {
        // The fixture's IRepository has no implementer, so the interface-dispatched call has a declared member but no candidates.
        var call = Assert.Single(S.CallSites, x => x.Caller == Method("Research.Application.Service.Interface(Research.Infrastructure.IRepository)").Id);
        Assert.Equal(DispatchKind.Interface, call.Dispatch);
        Assert.Equal(Method("Research.Infrastructure.IRepository.Read()").Id, call.BoundTarget);
        Assert.DoesNotContain(S.Implementations, x => x.Declared == call.BoundTarget);
        Assert.All(S.Implementations, x => Assert.Contains(S.Callables, c => c.Id == x.Implementation && c.Body != BodyStatus.MetadataOnly));
    }
    [Fact]
    public void Throw_expression_rethrow_and_filter_facts_are_retained()
    {
        Assert.Contains(S.ThrowSites, x => x.Owner == Method("Research.Infrastructure.Repository.Expression()").Id && x.IsExactType);
        Assert.Contains(S.ThrowSites, x => x.Kind == ThrowKind.Rethrow);
        Assert.Contains(S.Catches, x => x.Filter == "enabled");
        var staticThrow = Assert.Single(S.ThrowSites, x => x.Owner == Method("Research.Infrastructure.Repository.StaticType(System.Exception)").Id);
        Assert.False(staticThrow.IsExactType);
        Assert.Equal("System.Exception", staticThrow.StaticExceptionType?.DisplayName);
    }
    [Fact]
    public void Call_paths_do_not_invent_sibling_edges_and_retain_duplicates()
    {
        var root = Method("Research.Application.Service.Branch(bool)");
        var paths = CallChainQuery.Enumerate(S, [root.Id], new());
        Assert.Equal(2, paths.Chains.Length);
        Assert.DoesNotContain(paths.Chains, x => x.Steps.Any(s => s.Caller == Method("Research.Infrastructure.Repository.Read()").Id && s.Target == Method("Research.Infrastructure.Repository.Quiet()").Id));
        var repeat = CallChainQuery.Enumerate(S, [Method("Research.Application.Service.Repeat()").Id], new());
        Assert.Equal(2, Assert.Single(Assert.Single(repeat.Chains).Steps).EvidenceSites.Length);
    }
    [Fact]
    public void Graph_limits_are_honest_and_cancellation_is_observed()
    {
        var quiet = Method("Research.Infrastructure.Repository.Quiet()").Id;
        Assert.False(CallChainQuery.Enumerate(S, [quiet], new(MaxChains: 1)).OutputLimitReached);
        var root = Method("Research.Application.Service.Branch(bool)").Id;
        Assert.True(CallChainQuery.Enumerate(S, [root], new(MaxChains: 1)).OutputLimitReached);
        Assert.Equal(ChainEnd.DepthLimit, Assert.Single(CallChainQuery.Enumerate(S, [root], new(MaxDepth: 0)).Chains).End);
        Assert.True(CallChainQuery.Enumerate(S, [root], new(MaxExpansions: 1)).WorkLimitReached);
        Assert.Throws<OperationCanceledException>(() => CallChainQuery.Enumerate(S, [quiet], new(), new CancellationToken(true)));
    }
}
