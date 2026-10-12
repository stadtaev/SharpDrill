using System.Diagnostics;
using System.Text.Json;
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
    [InlineData("--max-chains", "0")]
    [InlineData("--max-expansions", "-1")]
    [InlineData("--max-depth", "oops")]
    [InlineData("--format", "yaml")]
    [InlineData("--layer", "API")]
    [InlineData("--layer", "class:Orders=API")]
    [InlineData("--unknown", "x")]
    public async Task Invalid_options_fail_before_loading(string option, string value)
    {
        var result = await Run("chains", "missing.slnx", option, value);
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
    [InlineData("chains", "--allow", "API=Application")]
    [InlineData("chains", "--follow-candidates")]
    [InlineData("reach", "--dispatch", "direct")]
    [InlineData("reach", "--max-chains", "5")]
    public async Task Invalid_layer_requests_fail_before_loading(string command, params string[] options)
    {
        var result = await Run([command, "missing.slnx", .. options]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("Analysis input not found", result.Error);
    }
    [Fact]
    public async Task Traversal_limits_have_nonzero_status_and_visible_marker()
    {
        var result = await Run("chains", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--max-depth", "0");
        Assert.Equal(1, result.Exit);
        Assert.Contains("[max depth]", result.Out);
        Assert.Contains("depth", result.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Dispatch_policies_preserve_declared_binding_and_mark_exclusion()
    {
        string[] common = ["chains", ResearchFixture.Input, "--root", "Research.Application.Service.Interface(Research.Infrastructure.IRepository)"];
        var declared = await Run([.. common, "--dispatch", "declared"]);
        var direct = await Run([.. common, "--dispatch", "direct"]);
        Assert.Equal(1, declared.Exit); Assert.Equal(1, direct.Exit);
        Assert.Contains(" --> Research.Infrastructure.IRepository.Read() [interface target]", declared.Out);
        Assert.Contains(" --> [interface call: Research.Infrastructure.IRepository.Read()]", direct.Out);
    }
    [Fact]
    public async Task Snapshot_ids_round_trip_and_json_is_deterministic()
    {
        var first = await Run("inspect", ResearchFixture.Input, "--format", "json");
        var second = await Run("inspect", ResearchFixture.Input, "--format", "json");
        Assert.Equal(1, first.Exit); Assert.Equal(first.Out, second.Out);
        using var doc = JsonDocument.Parse(first.Out);
        var method = doc.RootElement.GetProperty("snapshot").GetProperty("callables").EnumerateArray()
            .Single(x => x.GetProperty("displaySignature").GetString() == "Research.Api.OrdersController.Get()");
        var id = method.GetProperty("id").GetString()!;
        var result = await Run("chains", ResearchFixture.Input, "--root-id", id);
        Assert.Equal(1, result.Exit);
        Assert.Contains("OrdersController.Get() --> Research.Application.Service.Load()", result.Out);
    }
    [Fact]
    public async Task Output_refuses_to_overwrite_an_input_file()
    {
        var before = await File.ReadAllBytesAsync(ResearchFixture.Input);
        var result = await Run("inspect", ResearchFixture.Input, "--format", "json", "--output", ResearchFixture.Input);
        Assert.Equal(2, result.Exit);
        Assert.Equal(before, await File.ReadAllBytesAsync(ResearchFixture.Input));
    }
    [Fact]
    public async Task Complete_landscape_includes_catch_protected_regions_and_static_types()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Replace()");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Rethrown or replaced", result.Out);
        Assert.Contains("Subsequent origin:", result.Out);
        Assert.Contains("Protected region:", result.Out);
        Assert.Contains("Filter: enabled", result.Out);
        Assert.Contains("static; runtime type may differ", result.Out);
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
