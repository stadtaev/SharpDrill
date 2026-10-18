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
        var bad = await Run("paths", "missing.slnx", "--max-depth", "-1");
        Assert.Equal(2, bad.Exit); Assert.Contains("Depth", bad.Error);
        Assert.Equal(2, (await Run("paths", "missing.slnx", "--root", "a", "--root", "b")).Exit);
    }
    [Fact]
    public async Task Fresh_process_prints_cross_project_chain_and_evidence()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--locations");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Research.Api.OrdersController.Get() --> Research.Application.Service.Load() --> Research.Infrastructure.Repository.Read()", result.Out);
        Assert.Contains("Repository.cs:", result.Out);
        Assert.DoesNotContain("fatal", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Json_stdout_remains_valid_and_contains_exception_regions()
    {
        var result = await Run("inspect", ResearchFixture.Input, "--json");
        Assert.Equal(1, result.Exit);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.Equal(3, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(doc.RootElement.GetProperty("snapshot").GetProperty("exceptionRegions").GetArrayLength() > 0);
    }
    [Fact]
    public async Task Exceptions_report_action_escape_and_boundary_evidence_separately()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(1, result.Exit);
        Assert.Contains("May escape: Research.Infrastructure.StorageException", result.Out);
        Assert.Contains("Boundary registration evidence (runtime coverage unknown)", result.Out);
        Assert.Contains("UseExceptionHandler", result.Out);
        Assert.DoesNotContain("unhandled", result.Out, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Paths_show_projects_and_distance_is_transitions()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Project: Research.Infrastructure | Type: Research.Infrastructure.Repository", result.Out);
        Assert.Contains("Transitions: 2", result.Out);
    }
    [Fact]
    public async Task Reach_lists_each_callable_once_with_a_shortest_witness()
    {
        var result = await Run("reach", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--locations");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Root: Research.Api.OrdersController.Get()", result.Out);
        Assert.Contains("  [1] Research.Application.Service.Load() | Project: Research.Application", result.Out);
        Assert.Contains("  [2] Research.Infrastructure.Repository.Read() | Project: Research.Infrastructure", result.Out);
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
        var result = await Run("paths", ResearchFixture.Input, "--root", "Not.A.Method()");
        Assert.Equal(2, result.Exit); Assert.Contains("No source callable", result.Error);
    }
}
