using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SharpView.Tests;

public sealed class CliTests
{
    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(ResearchFixture.Root, $"src/SharpView.Cli/bin/{configuration}/net10.0/SharpView.Cli.dll");
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
        var version = await Run("--version");
        Assert.Equal(0, version.Exit); Assert.Matches(@"^\d+\.\d+\.\d+\S*\s*$", version.Out);
        var bad = await Run("paths", "missing.slnx", "--max-depth", "-1");
        Assert.Equal(2, bad.Exit); Assert.Contains("Depth", bad.Error);
        Assert.Equal(2, (await Run("paths", "missing.slnx", "--root", "a", "--root", "b")).Exit);
    }
    [Fact]
    public async Task Fresh_process_prints_cross_project_chain_and_evidence()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--locations");
        // A complete result exits 0: the top-level-statements gap in Program.cs lies on no reported path.
        Assert.Equal(0, result.Exit);
        Assert.Contains("OrdersController.Get -> Service.Load -> Repository.Read", result.Out);
        Assert.Contains("    at Research.Infrastructure/Repository.cs:6", result.Out);
        Assert.Equal("1 path from 1 root.\n", result.Error.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("fatal", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Json_stdout_remains_valid_and_contains_exception_regions()
    {
        var result = await Run("inspect", ResearchFixture.Input, "--json");
        Assert.Equal(0, result.Exit);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.Equal(3, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(doc.RootElement.GetProperty("snapshot").GetProperty("exceptionRegions").GetArrayLength() > 0);
    }
    [Fact]
    public async Task Exceptions_list_escapes_reaching_a_global_handler_under_its_registration()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(0, result.Exit);
        // The registered middleware is named once, as the section header, not repeated per finding.
        Assert.Equal("Handled by a global exception handler:\n  - UseExceptionHandler  Research.Api/Program.cs:5\n\n1. Research.Infrastructure.StorageException\n   Thrown:     Repository.Read  Research.Infrastructure/Repository.cs:6\n   Path:       OrdersController.Get -> Service.Load -> Repository.Read\n", result.Out.ReplaceLineEndings("\n"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Out, "UseExceptionHandler"));
        Assert.Equal("1 action analysed: 1 uncaught exception (1 handled by a global exception handler).\n", result.Error.ReplaceLineEndings("\n"));
    }
    [Fact]
    public async Task Paths_show_projects_and_distance_is_transitions()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(0, result.Exit);
        Assert.Equal("OrdersController.Get -> Service.Load -> Repository.Read -> StorageException.ctor\n", result.Out.ReplaceLineEndings("\n"));
    }
    [Fact]
    public async Task Reach_lists_each_callable_once_with_a_shortest_witness()
    {
        var result = await Run("reach", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--locations");
        Assert.Equal(0, result.Exit);
        Assert.StartsWith("OrdersController.Get\n  [1] Service.Load  (Research.Application)\n      at Research.Api/OrdersController.cs:8\n  [2] Repository.Read  (Research.Infrastructure)  via Service.Load\n", result.Out.ReplaceLineEndings("\n"));
        var service = await Run("reach", ResearchFixture.Input, "--root", "Research.Application.Service.Load()");
        Assert.Contains("  [1] Repository.Read  (Research.Infrastructure)", service.Out);
        var dispatch = await Run("reach", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Interface(Research.Infrastructure.IRepository)");
        Assert.Equal(1, dispatch.Exit);
        Assert.Contains("  Stops at: interface call to IRepository.Read (--follow-candidates follows its implementations)  Research.Application/Service.cs:12", dispatch.Out);
    }
    [Fact]
    public async Task Absent_root_is_an_invalid_request()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Not.A.Method()");
        Assert.Equal(2, result.Exit); Assert.Contains("No source callable", result.Error);
    }
}
