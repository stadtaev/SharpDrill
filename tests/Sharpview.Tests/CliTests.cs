using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Sharpview.Tests;

public sealed class CliTests
{
    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(ResearchFixture.Root, $"src/Sharpview.Cli/bin/{configuration}/net10.0/Sharpview.Cli.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = ResearchFixture.Root };
        start.ArgumentList.Add(path);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
    [Fact]
    public async Task Help_and_invalid_arguments_do_not_load_input()
    {
        Assert.Contains("exceptions", (await Run("--help")).Out);
        var bad = await Run("chains", "missing.slnx", "--max-depth", "-1");
        Assert.Equal(2, bad.Exit); Assert.Contains("Depth", bad.Error);
        Assert.Equal(2, (await Run("chains", "missing.slnx", "--dispatch", "guess")).Exit);
        Assert.Equal(2, (await Run("chains", "missing.slnx", "--root", "a", "--root-id", "b")).Exit);
    }
    [Fact]
    public async Task Fresh_process_prints_cross_project_chain_and_evidence()
    {
        var result = await Run("chains", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--locations");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Research.Api.OrdersController.Get() --> Research.Application.Service.Load() --> Research.Infrastructure.Repository.Read()", result.Out);
        Assert.Contains("Repository.cs:", result.Out);
        Assert.DoesNotContain("fatal", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Json_stdout_remains_valid_and_contains_exception_regions()
    {
        var result = await Run("inspect", ResearchFixture.Input, "--format", "json");
        Assert.Equal(1, result.Exit);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.Equal(3, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(doc.RootElement.GetProperty("snapshot").GetProperty("exceptionRegions").GetArrayLength() > 0);
    }
    [Fact]
    public async Task Exceptions_report_action_escape_and_boundary_evidence_separately()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(1, result.Exit);
        Assert.Contains("May escape the controller action", result.Out);
        Assert.Contains("Boundary handling: registration evidence", result.Out);
        Assert.Contains("UseExceptionHandler", result.Out);
        Assert.DoesNotContain("unhandled", result.Out, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Controller_layers_are_explicit_and_distance_is_transitions()
    {
        var result = await Run("controllers", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--layer", "project:Research.Infrastructure=Infrastructure");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Layer: Infrastructure", result.Out);
        Assert.Contains("Transitions: 2", result.Out);
    }
    [Fact]
    public async Task Layer_overview_aggregates_paths_and_evaluates_allowed_dependencies()
    {
        string[] layers = ["--layer", "project:Research.Api=API", "--layer", "project:Research.Application=Application", "--layer", "project:Research.Infrastructure=Infrastructure"];
        var overview = await Run(["layers", ResearchFixture.Input, .. layers]);
        Assert.Contains("Layer edge: API --> Application | DirectCall |", overview.Out);
        Assert.Contains("Layer edge: Application --> Infrastructure | DirectCall |", overview.Out);
        Assert.Contains("Layer edge: Application --> Infrastructure | DeclaredCall |", overview.Out);
        Assert.DoesNotContain("Finding [", overview.Out);
        Assert.Contains("rule evaluated: False", overview.Error);
        var evaluated = await Run(["layers", ResearchFixture.Input, .. layers, "--allow", "API=Application", "--allow", "Application=Infrastructure"]);
        Assert.Equal(1, evaluated.Exit);
        // The fixture's IRepository parameter on a controller action is a real API --> Infrastructure type dependency.
        Assert.Contains("Finding [layer-dependency] Violation: API --> Infrastructure (TypeDependency)", evaluated.Out);
        Assert.DoesNotContain("Violation: API --> Application", evaluated.Out);
        Assert.Contains("violations: 1", evaluated.Error);
    }
    [Fact]
    public async Task Reach_lists_each_callable_once_with_a_shortest_witness()
    {
        var result = await Run("reach", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--layer", "project:Research.Infrastructure=Infrastructure", "--locations");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Root: Research.Api.OrdersController.Get()", result.Out);
        Assert.Contains("  [1] Research.Application.Service.Load() | Project: Research.Application | Layer: unconfigured", result.Out);
        Assert.Contains("  [2] Research.Infrastructure.Repository.Read() | Project: Research.Infrastructure | Layer: Infrastructure", result.Out);
        Assert.Contains("    Path: Research.Api.OrdersController.Get() --> Research.Application.Service.Load() --> Research.Infrastructure.Repository.Read()", result.Out);
        Assert.Contains("| direct call", result.Out);
        var service = await Run("reach", ResearchFixture.Input, "--root", "Research.Application.Service.Load()");
        Assert.Contains("  [1] Research.Infrastructure.Repository.Read()", service.Out);
        var dispatch = await Run("reach", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Interface(Research.Infrastructure.IRepository)");
        Assert.Contains("Boundary: Interface dispatch --> Research.Infrastructure.IRepository.Read() | candidates followed: 0", dispatch.Out);
    }
    [Fact]
    public async Task Absent_root_is_an_invalid_request()
    {
        var result = await Run("chains", ResearchFixture.Input, "--root", "Not.A.Method()");
        Assert.Equal(2, result.Exit); Assert.Contains("No source callable", result.Error);
    }
}
