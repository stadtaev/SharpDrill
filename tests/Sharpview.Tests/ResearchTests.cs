using Sharpview.Core;
using Sharpview.Roslyn;
using Xunit;

namespace Sharpview.Tests;

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
        Assert.Equal(12, S.ControllerActions.Length);
        Assert.DoesNotContain(S.ControllerActions, x => x.Method == Method("Research.Api.OrdersController.Helper()").Id);
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
    [InlineData("Interface(Research.Infrastructure.IRepository)", "Interface")]
    [InlineData("Recursive()", "Cycle")]
    public void Uncertain_boundaries_are_visible(string action, string reason)
    {
        var report = ExceptionQuery.Analyze(S, [Method("Research.Api.OrdersController." + action).Id], new());
        Assert.Contains(report.Results, x => x.Status == ExceptionStatus.Unknown && x.Reason.Contains(reason, StringComparison.OrdinalIgnoreCase));
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
