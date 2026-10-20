using System.Diagnostics;
using System.Text.Json;
using System.Text;
using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

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
        start.ArgumentList.Add(Path.Combine(ResearchFixture.Root, $"src/SharpView.Cli/bin/{configuration}/net10.0/SharpView.Cli.dll"));
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
    [InlineData("--layer", "project:Orders=API")]
    [InlineData("--unknown", "x")]
    public async Task Invalid_options_fail_before_loading(string option, string value)
    {
        var result = await Run("paths", "missing.slnx", option, value);
        Assert.Equal(2, result.Exit);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("Analysis input not found", result.Error);
    }
    [Theory]
    [InlineData("projects", "--root", "X()")]
    [InlineData("projects", "--output", "x.md")]
    [InlineData("paths", "--follow-candidates")]
    [InlineData("exceptions", "--root", "X()")]
    [InlineData("exceptions", "--declared-only")]
    [InlineData("projects", "--follow-candidates")]
    [InlineData("exceptions", "--uncaught", "--caught")]
    [InlineData("exceptions", "--uncaught", "--max-depth", "3")]
    [InlineData("exceptions", "--caught", "--max-paths", "0")]
    [InlineData("reach", "--max-paths", "5")]
    [InlineData("inspect", "--methods", "--json")]
    [InlineData("inspect", "--output", "x.json")]
    [InlineData("inspect", "--root", "X()")]
    public async Task Invalid_requests_fail_before_loading(string command, params string[] options)
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
    [InlineData("layers")]
    public async Task Removed_commands_are_rejected(string command)
    {
        var result = await Run(command, "missing.slnx");
        Assert.Equal(2, result.Exit); Assert.Contains("Unknown command", result.Error);
    }
    [Theory]
    [InlineData("exceptions", "--dispatch", "direct")]
    [InlineData("exceptions", "--summary")]
    [InlineData("exceptions", "--paths")]
    [InlineData("paths", "--layer", "project:X=X")]
    [InlineData("reach", "--allow", "A=B")]
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
        var result = await Run("projects", "missing.slnx", "--follow-candidates");
        Assert.Equal(2, result.Exit); Assert.Contains("projects does not accept --follow-candidates", result.Error);
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
    public async Task Inspect_landscape_includes_catch_protected_regions_and_static_types()
    {
        var result = await Run("inspect", ResearchFixture.Input);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Subsequent origin:", result.Out);
        Assert.Contains("Protected region:", result.Out);
        Assert.Contains("Filter: enabled", result.Out);
        Assert.Contains("static; runtime type may differ", result.Out);
    }
    [Fact]
    public async Task Default_lists_every_throw_with_method_type_and_project()
    {
        var result = await Run("exceptions", ResearchFixture.Input);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Throw: Research.Infrastructure.StorageException\n  At: Research.Infrastructure.Repository.Read() | Type: Research.Infrastructure.Repository | Project: Research.Infrastructure |", result.Out.Replace("\r\n", "\n"));
        Assert.Contains("Throw: rethrow of the caught exception", result.Out);
        Assert.Contains("Inside try in the same method: yes", result.Out);
        Assert.Contains("Throws: ", result.Error);
    }
    [Fact]
    public async Task Caught_reports_where_thrown_and_where_caught_with_the_handler_outcome()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--caught");
        Assert.Equal(1, result.Exit);
        var text = result.Out.Replace("\r\n", "\n");
        Assert.Contains("  Caught by: catch (Research.Infrastructure.StorageException) in Service.Caught  Research.Application", text);
        Assert.Contains("  Thrown:    Repository.Read  Research.Infrastructure", text);
        Assert.Contains("  Path:\n       OrdersController.", text);
        Assert.Contains("Handler:   rethrows it", text);
        Assert.Contains("Handler:   throws a replacement", text);
        Assert.Contains("filter decides at runtime", text);
        Assert.DoesNotContain("Service.Incompatible()", text);
    }
    [Fact]
    public async Task Miscaught_reports_the_try_whose_clauses_do_not_match_and_what_happens_next()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--miscaught");
        Assert.Equal(1, result.Exit);
        var text = result.Out.Replace("\r\n", "\n");
        Assert.Contains("Miscaught: Research.Infrastructure.StorageException", text);
        Assert.Contains("  Missed by: try in Service.Incompatible (catches only System.ArgumentException)  Research.Application", text);
        Assert.Contains("  Then:      may escape the controller action", text);
        Assert.Contains("    -> Service.Incompatible\n", text);
        Assert.Contains("miscaught: 1;", result.Error);
    }
    [Fact]
    public async Task Projects_print_the_reference_tree_with_build_settings()
    {
        var result = await Run("projects", ResearchFixture.Input);
        Assert.Equal(0, result.Exit);
        var text = result.Out.Replace("\r\n", "\n");
        Assert.Contains("Research.Api [net10.0 | Nullable: enable | Nullable warnings as errors: no | All warnings as errors: no | Compiler warnings: 0 | Errors: 0]\n└── Research.Application [", text);
        Assert.Contains("\n    └── Research.Infrastructure [net10.0 | Nullable: enable | Nullable warnings as errors: yes |", text);
        Assert.Contains("references: 2; unloaded references: 0", result.Error);
        var path = Path.Combine(Path.GetTempPath(), $"sharpview-projects-{Guid.NewGuid():N}.md");
        try
        {
            var saved = await Run("projects", ResearchFixture.Input, "--mermaid", "--output", path);
            Assert.Contains("Saved diagram: " + Path.GetFullPath(path), saved.Out);
            var document = await File.ReadAllTextAsync(path);
            Assert.Contains("```mermaid", document);
            Assert.Contains("p1 ---> p2", document);
            Assert.Contains("| Research.Infrastructure | net10.0 | enable | yes | no | 0 | 0 |", document);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ownerless_exception_gaps_are_reported_once_as_global_caveats(bool summary)
    {
        var result = await Run(summary ? ["exceptions", ResearchFixture.Input, "--uncaught"] : ["exceptions", ResearchFixture.Input, "--caught"]);
        Assert.Equal(1, result.Exit);
        // Gaps are grouped by cause with a count and the first location.
        var gap = Assert.Single(result.Error.Split('\n'), x => x.StartsWith("Gap [", StringComparison.Ordinal));
        Assert.Contains("Top-level statements have no owning callable", gap);
        Assert.Contains("Extraction gaps: 1 (1 kind)", result.Error);
        Assert.Contains("global caveats: 1", result.Error);
        Assert.DoesNotContain("Unknown [ExtractionGap]", result.Out);
        Assert.DoesNotContain("Reason: Top-level statements", result.Out);
        if (!summary) Assert.Contains("witnesses: 13; unknown: 5", result.Error);
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
    [Fact]
    public async Task Interface_calls_are_followed_by_default_and_declared_only_stops_with_a_hint()
    {
        const string action = "Research.Api.OrdersController.Interface(Research.Infrastructure.IRepository)";
        // The fixture's interface has no implementation in source, so following has nowhere to go and says so.
        var followed = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", action);
        Assert.Contains("Interface dispatch: no source implementation found", followed.Out);
        Assert.DoesNotContain("were not followed", followed.Error);
        var declared = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--declared-only", "--root", action);
        Assert.Contains("Unknown [DispatchBoundary]: Interface dispatch: runtime origins unknown.", declared.Out);
        Assert.Contains("1 interface/virtual call sites were not followed (--declared-only)", declared.Error);
        Assert.Contains("Rerun without --declared-only", declared.Error);
        var caught = await Run("exceptions", ResearchFixture.Input, "--caught", "--declared-only", "--root", action);
        Assert.Contains("1 interface/virtual call sites were not followed (--declared-only)", caught.Error);
    }
    [Theory]
    [InlineData("--max-depth", "3")]
    [InlineData("--max-paths", "5")]
    [InlineData("--follow-candidates")]
    public async Task Exception_views_have_no_depth_or_path_limits_and_no_follow_flag(string option, params string[] value)
    {
        var result = await Run(["exceptions", "missing.slnx", "--uncaught", option, .. value]);
        Assert.Equal(2, result.Exit); Assert.Contains($"exceptions does not accept {option}", result.Error);
    }
}
