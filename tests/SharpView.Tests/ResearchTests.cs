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
    [InlineData("Get", "escapes")]
    [InlineData("Caught", "caught")]
    [InlineData("Unrelated", "escapes")]
    [InlineData("Incompatible", "escapes")]
    [InlineData("Rethrow", "rethrown, escapes")]
    [InlineData("Replace", "caught")]
    [InlineData("Filter(bool)", "escapes")]
    [InlineData("AwaitCaught", "caught")]
    [InlineData("Unobserved", "unknown")]
    [InlineData("Nested", "caught")]
    public void The_catch_around_each_call_site_decides_the_fate_of_the_repository_throw(string action, string expected)
    {
        var index = new ProgramIndex(S);
        var root = Method("Research.Api.OrdersController." + (action.Contains('(') ? action : action + "()")).Id;
        var origin = Assert.Single(S.ThrowSites, x => x.Owner == Method("Research.Infrastructure.Repository." + (action is "AwaitCaught" or "Unobserved" ? "ReadAsync()" : "Read()")).Id).Id;
        var summary = ExceptionSummaryQuery.Analyze(index, [root]);
        var caught = ExceptionHandlingQuery.Caught(index, summary).Where(x => x.Origin == origin).ToArray();
        var fate = summary.Roots[root].Escapes.FirstOrDefault(x => x.Origin == origin) is { } escape
            ? escape.ReasonCode == ExceptionReason.ChangedMayEscape ? "rethrown, escapes" : "escapes"
            : summary.Roots[root].Unknowns.Any(x => x.Origin == origin) ? "unknown" : caught.Length > 0 ? "caught" : "none";
        Assert.Equal(expected, fate);
        Assert.All(caught, x => Assert.Equal(3, x.Example.Path.Length));
        // The replacement is an origin of its own and escapes.
        if (action == "Replace") Assert.Contains(summary.Roots[root].Escapes, x => x.ExceptionType == "System.InvalidOperationException");
        // The inner catch rethrows, the outer one takes it.
        if (action == "Nested") Assert.Equal([HandlerOutcome.Rethrows, HandlerOutcome.Intercepts], caught.Select(x => index.Catch(x.Catch).Outcome));
    }
    [Fact]
    public void Interface_without_implementation_is_uncertain_and_recursion_alone_is_not()
    {
        var index = new ProgramIndex(S);
        CallableId Root(string action) => Method("Research.Api.OrdersController." + action).Id;
        var summary = ExceptionSummaryQuery.Analyze(index, [Root("Interface(Research.Infrastructure.IRepository)"), Root("Recursive()")], new(FollowCandidates: true));
        Assert.Contains(summary.Roots[Root("Interface(Research.Infrastructure.IRepository)")].Unknowns, x => x.ReasonCode == ExceptionReason.DispatchBoundary);
        Assert.True(summary.Roots[Root("Recursive()")].IsComplete);
    }
    // Same code as the "Writing your own rule" example in docs/overview.md; keeps the documentation honest.
    private static IEnumerable<Finding> NoDirectInfrastructure(ProgramIndex index, LayerMap layers)
    {
        foreach (var action in AspNetConventions.ControllerActions(index).Select(x => x.Method))
            foreach (var site in index.Outgoing(action).Where(x => x.BoundTarget is { } target && layers.LayerOf(target) == "Infrastructure"))
                yield return new("no-direct-infrastructure", Verdict.Violation,
                    index.Callable(action).DisplaySignature, $"Calls {site.TargetDisplay} directly.", [site.Evidence]);
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
    public void Fixture_uncertainty_is_only_what_the_analysis_cannot_decide()
    {
        var summary = ExceptionSummaryQuery.Analyze(S, [.. Actions.Select(x => x.Method)], new(FollowCandidates: true));
        // Library calls, compiler-provided members and recursion are not uncertainty; ownerless gaps stay global.
        Assert.Equal([ExceptionReason.DispatchBoundary, ExceptionReason.UnobservedTask],
            summary.Roots.Values.SelectMany(x => x.Unknowns).Select(x => x.ReasonCode).Distinct().Order());
        Assert.Single(summary.GlobalGaps);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void Every_fixture_example_has_a_reason_code_consistent_with_its_final_status()
    {
        var index = new ProgramIndex(S);
        var summary = ExceptionSummaryQuery.Analyze(index, [.. Actions.Select(x => x.Method)], new(FollowCandidates: true));
        var miscaught = ExceptionHandlingQuery.Miscaught(index, summary);
        ExceptionWitness[] witnesses = [.. ExceptionHandlingQuery.Caught(index, summary).Select(x => x.Example), .. miscaught.Select(x => x.Example),
            .. miscaught.Select(x => x.Undetermined).OfType<ExceptionWitness>()];
        Assert.NotEmpty(witnesses);
        ExceptionReason[] outcomes = [ExceptionReason.InterceptedByHandler, ExceptionReason.MayEscape, ExceptionReason.ChangedMayEscape];
        foreach (var witness in witnesses)
        {
            var final = witness.SubsequentStatus ?? witness.Status;
            Assert.NotEqual(ExceptionReason.Unspecified, witness.ReasonCode);
            if (final == ExceptionStatus.Intercepted) Assert.Equal(ExceptionReason.InterceptedByHandler, witness.ReasonCode);
            else if (final == ExceptionStatus.MayEscape)
                Assert.Equal(witness.Status == ExceptionStatus.RethrownOrReplaced ? ExceptionReason.ChangedMayEscape : ExceptionReason.MayEscape, witness.ReasonCode);
            else Assert.DoesNotContain(witness.ReasonCode, outcomes);
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
    public void Call_graph_keeps_each_call_site_and_invents_no_sibling_edges()
    {
        var index = new ProgramIndex(S);
        var quiet = Method("Research.Infrastructure.Repository.Quiet()").Id;
        Assert.Equal([quiet, quiet], index.CallGraph.Graph.OutEdges(Method("Research.Application.Service.Repeat()").Id).Select(x => x.Target));
        Assert.DoesNotContain(index.CallGraph.Graph.OutEdges(Method("Research.Infrastructure.Repository.Read()").Id), x => x.Target == quiet);
    }
}
