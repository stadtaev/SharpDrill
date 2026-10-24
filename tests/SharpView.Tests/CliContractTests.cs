using System.Diagnostics;
using System.Text.Json;
using System.Text;
using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

public sealed class CliContractTests
{
    private static Task<(int Exit, string Out, string Error)> Run(params string[] args) => RunIn(ResearchFixture.Root, args);
    private static async Task<(int Exit, string Out, string Error)> RunIn(string directory, params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
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
        Assert.DoesNotContain("Input not found", result.Error);
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
        Assert.DoesNotContain("Input not found", result.Error);
    }
    [Fact]
    public async Task Without_an_input_the_solution_in_the_current_directory_is_analysed()
    {
        var fromDirectory = await RunIn(Path.GetDirectoryName(ResearchFixture.Input)!, "projects");
        Assert.Equal(0, fromDirectory.Exit);
        Assert.Equal((await Run("projects", ResearchFixture.Input)).Out, fromDirectory.Out);
        Assert.Equal(fromDirectory.Out, (await Run("projects", Path.GetDirectoryName(ResearchFixture.Input)!)).Out);
    }
    [Fact]
    public async Task A_directory_without_one_input_names_what_it_found()
    {
        var none = await RunIn(Path.Combine(ResearchFixture.Root, "docs"), "projects");
        Assert.Equal(2, none.Exit);
        Assert.Contains("No .sln, .slnx or .csproj in", none.Error);
        var directory = Directory.CreateTempSubdirectory("sharpview-");
        try
        {
            foreach (var name in new[] { "A.sln", "B.slnx", "C.csproj" }) File.WriteAllText(Path.Combine(directory.FullName, name), "");
            var several = await Run("projects", directory.FullName);
            Assert.Equal(2, several.Exit);
            Assert.Contains(": A.sln, B.slnx. Pass one.", several.Error);
        }
        finally { directory.Delete(true); }
    }
    [Fact]
    public async Task Traversal_limits_have_nonzero_status_and_visible_marker()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--max-depth", "0");
        Assert.Equal(1, result.Exit);
        Assert.Equal("OrdersController.Get  (ends: --max-depth)\n", result.Out.ReplaceLineEndings("\n"));
        Assert.Contains("1 path cut at --max-depth 0", result.Error);
    }
    [Fact]
    public async Task Paths_preserve_the_declared_binding_at_dispatch()
    {
        var result = await Run("paths", ResearchFixture.Input, "--root", "Research.Application.Service.Interface(Research.Infrastructure.IRepository)");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Service.Interface -> [interface call Research.Infrastructure.IRepository.Read()]", result.Out);
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
        Assert.Equal(0, first.Exit); Assert.Equal(first.Out, second.Out);
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
        Assert.Equal(0, result.Exit);
        Assert.Contains("OrdersController.Get -> Service.Load", result.Out);
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
        Assert.Equal(0, result.Exit);
        Assert.Contains("Subsequent origin:", result.Out);
        Assert.Contains("Gaps (code the analysis does not model):\n  [Unsupported] x1 Top-level statements", result.Out.ReplaceLineEndings("\n"));
        Assert.Contains("Protected region:", result.Out);
        Assert.Contains("Filter: enabled", result.Out);
        Assert.Contains("static; runtime type may differ", result.Out);
    }
    [Fact]
    public async Task Default_lists_every_throw_with_method_type_and_project()
    {
        var result = await Run("exceptions", ResearchFixture.Input);
        Assert.Equal(0, result.Exit);
        Assert.Contains("Research.Infrastructure.StorageException  Repository.Read  Research.Infrastructure/Repository.cs:6\n", result.Out.ReplaceLineEndings("\n"));
        Assert.Contains("System.Exception (or a subclass)  Repository.StaticType", result.Out);
        // Rethrows are not new exceptions.
        Assert.DoesNotContain("rethrow", result.Out, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("5 throws in source.\n", result.Error.ReplaceLineEndings("\n"));
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
        Assert.Contains("Handler:   throws a different exception instead", text);
        Assert.Contains("Handler:   handles it (only if the filter accepts it)", text);
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
        Assert.StartsWith("Handled by a global exception handler: UseExceptionHandler (Research.Api/Program.cs:5)\nMiscaught: ", text);
        Assert.Contains("12 actions analysed: 1 miscaught (1 handled by a global exception handler).", result.Error);
    }
    [Fact]
    public async Task Projects_print_the_reference_tree_with_build_settings()
    {
        var result = await Run("projects", ResearchFixture.Input);
        Assert.Equal(0, result.Exit);
        var text = result.Out.Replace("\r\n", "\n");
        Assert.Equal("Research.Api [net10.0, nullable enable, 0 warnings]\n└── Research.Application [net10.0, nullable enable, 0 warnings]\n    └── Research.Infrastructure [net10.0, nullable enable, 0 warnings, nullable warnings are errors]\n", text);
        Assert.Empty(result.Error);
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
    public async Task Gaps_off_every_path_stay_out_of_exception_reports(bool summary)
    {
        var result = await Run(summary ? ["exceptions", ResearchFixture.Input, "--uncaught"] : ["exceptions", ResearchFixture.Input, "--caught"]);
        // Exit 1 for the fixture's genuine stops (an interface without implementation, a task never awaited), not for Program.cs.
        Assert.Equal(1, result.Exit);
        Assert.DoesNotContain("Top-level", result.Error);
        Assert.DoesNotContain("Top-level", result.Out);
        Assert.Contains("Not followed: 1 interface or virtual call without a source implementation (e.g. Research.Application/Service.cs:12)", result.Error);
        Assert.Contains("1 task that is never awaited (e.g. Research.Application/Service.cs:15)", result.Error);
    }

    [Fact]
    public async Task Depth_limited_reach_reports_one_stop_without_unexamined_dispatch_sites()
    {
        var result = await Run("reach", ResearchFixture.Input, "--root", "Research.Application.Service.Interface(Research.Infrastructure.IRepository)", "--max-depth", "0");
        Assert.Equal(1, result.Exit);
        Assert.Equal("Service.Interface\n  Stops at: --max-depth in Service.Interface  Research.Application/Service.cs:12\n", result.Out.ReplaceLineEndings("\n"));
        Assert.Contains("deeper than --max-depth 0", result.Error);
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
        Assert.Contains("Not followed: 1 interface or virtual call without a source implementation", followed.Error);
        var declared = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--declared-only", "--root", action);
        Assert.Contains("Not followed: 1 interface or virtual call not followed (--declared-only)", declared.Error);
        var caught = await Run("exceptions", ResearchFixture.Input, "--caught", "--declared-only", "--root", action);
        Assert.Contains("1 interface or virtual call not followed (--declared-only)", caught.Error);
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
