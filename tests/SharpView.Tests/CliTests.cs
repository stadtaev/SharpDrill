using System.Diagnostics;
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
        var bad = await Run("diagram", "missing.slnx", "--max-depth", "-1");
        Assert.Equal(2, bad.Exit); Assert.Contains("Depth", bad.Error);
        Assert.Equal(2, (await Run("diagram", "missing.slnx", "--root", "a", "--root", "b")).Exit);
    }
    [Fact]
    public async Task Fresh_process_prints_cross_project_chain_and_evidence()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Research.Api.OrdersController.Get()", "--locations");
        // A complete result exits 0: the top-level-statements gap in Program.cs lies on no reported path.
        Assert.Equal(0, result.Exit);
        Assert.Contains("   Path:       OrdersController.Get -> Service.Load -> Repository.Read\n   Locations:  Research.Api/OrdersController.cs:8\n               Research.Application/Service.cs:5\n", result.Out.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("fatal", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Exceptions_name_the_global_handler_that_receives_an_escape()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(0, result.Exit);
        // Each finding names its registration, so a block copied out of a CI log reads on its own.
        Assert.Equal("1. Research.Infrastructure.StorageException\n   Thrown:     Repository.Read  Research.Infrastructure/Repository.cs:6\n"
            + "   Handled:    by the global exception handler UseExceptionHandler  Research.Api/Program.cs:5\n   Path:       OrdersController.Get -> Service.Load -> Repository.Read\n", result.Out.ReplaceLineEndings("\n"));
        Assert.Equal("1 action analysed: 1 uncaught exception (1 handled by a global exception handler).\n", result.Error.ReplaceLineEndings("\n"));
    }
    [Fact]
    public async Task Absent_root_is_an_invalid_request()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Not.A.Method()");
        Assert.Equal(2, result.Exit); Assert.Contains("No controller action matches --root Not.A.Method()", result.Error);
    }
}
