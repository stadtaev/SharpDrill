using System.Diagnostics;
using System.Text.Json;
using System.Text;
using Sharpview.Core;
using Xunit;

namespace Sharpview.Tests;

public sealed class CliContractTests
{
    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ResearchFixture.Root
        };
        start.ArgumentList.Add(Path.Combine(ResearchFixture.Root, $"src/Sharpview.Cli/bin/{configuration}/net10.0/Sharpview.Cli.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
    [Theory]
    [InlineData("--max-paths", "0")]
    [InlineData("--budget", "-1")]
    [InlineData("--max-depth", "oops")]
    [InlineData("--format", "yaml")]
    [InlineData("--layer", "API")]
    [InlineData("--layer", "class:Orders=API")]
    [InlineData("--unknown", "x")]
    public async Task Invalid_options_fail_before_loading(string option, string value)
    {
        var result = await Run("paths", "missing.slnx", option, value);
        Assert.Equal(2, result.Exit);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("Analysis input not found", result.Error);
    }
    [Theory]
    [InlineData("layers")]
    [InlineData("layers", "--layer", "project:Orders=API", "--root", "X()")]
    [InlineData("layers", "--layer", "project:Orders=API", "--max-depth", "3")]
    [InlineData("layers", "--layer", "project:Orders=API", "--allow", "API")]
    [InlineData("layers", "--layer", "project:Orders=API", "--allow", "API=")]
    [InlineData("layers", "--layer", "project:Orders=API", "--allow", "API=Application,")]
    [InlineData("paths", "--allow", "API=Application")]
    [InlineData("paths", "--follow-candidates")]
    [InlineData("reach", "--max-paths", "5")]
    [InlineData("inspect", "--methods", "--json")]
    [InlineData("inspect", "--output", "x.json")]
    [InlineData("inspect", "--root", "X()")]
    public async Task Invalid_layer_requests_fail_before_loading(string command, params string[] options)
    {
        var result = await Run([command, "missing.slnx", .. options]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("Analysis input not found", result.Error);
    }
    [Fact]
    public async Task Traversal_limits_have_nonzero_status_and_visible_marker()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--max-depth", "0");
        Assert.Equal(1, result.Exit);
        Assert.Contains("[max depth]", result.Out);
        Assert.Contains("depth", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Paths_preserve_the_declared_binding_at_dispatch()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Application.Service.Interface(Research.Infrastructure.IRepository)");
        Assert.Equal(1, result.Exit);
        Assert.Contains(" --> Research.Infrastructure.IRepository.Read() [interface target]", result.Out);
    }
    [Theory]
    [InlineData("chains")]
    [InlineData("controllers")]
    [InlineData("methods")]
    public async Task Removed_commands_are_rejected(string command)
    {
        var result = await Run(command, "missing.slnx");
        Assert.Equal(2, result.Exit); Assert.Contains("Unknown command", result.Error);
    }
    [Theory]
    [InlineData("exceptions", "--dispatch", "direct")]
    [InlineData("exceptions", "--summary")]
    [InlineData("exceptions", "--max-facts", "5")]
    [InlineData("paths", "--max-chains", "5")]
    [InlineData("reach", "--max-expansions", "5")]
    [InlineData("paths", "--root-id", "x")]
    [InlineData("inspect", "--format", "json")]
    public async Task Removed_options_are_rejected_before_loading(string command, params string[] options)
    {
        var result = await Run([command, "missing.slnx", .. options]);
        Assert.Equal(2, result.Exit); Assert.Contains("Unknown option", result.Error);
    }
    [Fact]
    public async Task Option_of_another_command_names_the_command()
    {
        var result = await Run("layers", "missing.slnx", "--layer", "project:X=X", "--follow-candidates");
        Assert.Equal(2, result.Exit); Assert.Contains("layers does not accept --follow-candidates", result.Error);
    }
    [Fact]
    public async Task Snapshot_ids_round_trip_and_json_is_deterministic()
    {
        var first = await Run("inspect", ResearchFixture.Input, "--json");
        var second = await Run("inspect", ResearchFixture.Input, "--json");
        Assert.Equal(1, first.Exit); Assert.Equal(first.Out, second.Out);
        using var saved = new MemoryStream(Encoding.UTF8.GetBytes(first.Out));
        var restored = await SnapshotJson.ReadAsync(saved);
        var index = new ProgramIndex(restored.Snapshot);
        var action = Assert.Single(restored.Snapshot.Callables, x => x.DisplaySignature == "Research.Api.OrdersController.Get()");
        var reach = Assert.Single(ReachabilityQuery.Analyze(index, [action.Id], new()).Roots);
        Assert.Contains(reach.Reached, x => index.Callable(x.Callable).DisplaySignature == "Research.Application.Service.Load()");
        using var doc = JsonDocument.Parse(first.Out);
        var method = doc.RootElement.GetProperty("snapshot").GetProperty("callables").EnumerateArray()
            .Single(x => x.GetProperty("displaySignature").GetString() == "Research.Api.OrdersController.Get()");
        var id = method.GetProperty("id").GetString()!;
        var result = await Run("paths", ResearchFixture.Input, "--root", id);
        Assert.Equal(1, result.Exit);
        Assert.Contains("OrdersController.Get() --> Research.Application.Service.Load()", result.Out);
    }
    [Fact]
    public async Task Output_refuses_to_overwrite_an_input_file()
    {
        var before = await File.ReadAllBytesAsync(ResearchFixture.Input);
        var result = await Run("inspect", ResearchFixture.Input, "--json", "--output", ResearchFixture.Input);
        Assert.Equal(2, result.Exit);
        Assert.Equal(before, await File.ReadAllBytesAsync(ResearchFixture.Input));
    }
    [Fact]
    public async Task Complete_landscape_includes_catch_protected_regions_and_static_types()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--paths", "--root", "Research.Api.OrdersController.Replace()");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Rethrown or replaced", result.Out);
        Assert.Contains("Subsequent origin:", result.Out);
        Assert.Contains("Protected region:", result.Out);
        Assert.Contains("Filter: enabled", result.Out);
        Assert.Contains("static; runtime type may differ", result.Out);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ownerless_exception_gaps_are_reported_once_as_global_caveats(bool summary)
    {
        var result = await Run(summary ? ["exceptions", ResearchFixture.Input] : ["exceptions", ResearchFixture.Input, "--paths"]);
        Assert.Equal(1, result.Exit);
        Assert.Equal(6, result.Error.Split('\n').Count(x => x.StartsWith("Gap [", StringComparison.Ordinal)));
        Assert.Contains("global caveats: 6", result.Error);
        Assert.DoesNotContain("Unknown [ExtractionGap]", result.Out);
        Assert.DoesNotContain("Reason: Top-level/initializer", result.Out);
        if (!summary) Assert.Contains("witnesses: 26; unknown: 18", result.Error);
    }

    [Fact]
    public async Task Depth_limited_reach_reports_one_stop_without_unexamined_dispatch_sites()
    {
        var result = await Run("reach", ResearchFixture.Input, "--root", "Research.Application.Service.Interface(Research.Infrastructure.IRepository)", "--max-depth", "0");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Boundary: depth limit", result.Out);
        Assert.DoesNotContain("Boundary: Interface", result.Out);
        Assert.Contains("boundaries: 1", result.Error);
        Assert.Contains("examined sites: 0", result.Error);
    }

    [Fact]
    public async Task Missing_restoration_is_rejected_without_creating_assets_or_changing_source()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpview no restore " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "Unrestored.csproj");
        var source = Path.Combine(directory, "Sample.cs");
        const string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
        const string code = "public static class Sample { public static void Run() { } }";
        try
        {
            await File.WriteAllTextAsync(input, project);
            await File.WriteAllTextAsync(source, code);
            var result = await Run("inspect", input);
            Assert.Equal(2, result.Exit);
            Assert.Contains("not restored", result.Error);
            Assert.False(File.Exists(Path.Combine(directory, "obj", "project.assets.json")));
            Assert.Equal(project, await File.ReadAllTextAsync(input));
            Assert.Equal(code, await File.ReadAllTextAsync(source));
        }
        finally { Directory.Delete(directory, true); }
    }
}
