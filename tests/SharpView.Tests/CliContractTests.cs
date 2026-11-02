using System.Diagnostics;
using Xunit;

namespace SharpView.Tests;

public sealed class CliContractTests
{
    private static Task<(int Exit, string Out, string Error)> Run(params string[] args) => RunIn(ResearchFixture.Root, args);
    private static Task<(int Exit, string Out, string Error)> RunIn(string directory, params string[] args) => RunAs(directory, null, args);
    private static async Task<(int Exit, string Out, string Error)> RunAs(string directory, string? home, params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        if (home is not null) { start.Environment["HOME"] = home; start.Environment["USERPROFILE"] = home; }
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
    [InlineData("--max-nodes", "0")]
    [InlineData("--budget", "-1")]
    [InlineData("--max-depth", "oops")]
    [InlineData("--format", "yaml")]
    [InlineData("--layer", "project:Orders=API")]
    [InlineData("--unknown", "x")]
    public async Task Invalid_options_fail_before_loading(string option, string value)
    {
        var result = await Run("diagram", "missing.slnx", option, value);
        Assert.Equal(2, result.Exit);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("Input not found", result.Error);
    }
    [Theory]
    [InlineData("projects", "--root", "X()")]
    [InlineData("projects", "--output", "x.md")]
    [InlineData("exceptions", "--root", "X()")]
    [InlineData("exceptions", "--declared-only")]
    [InlineData("projects", "--declared-only")]
    [InlineData("exceptions", "--uncaught", "--caught")]
    [InlineData("exceptions", "--strict")]
    [InlineData("exceptions", "--miscaught", "--strict")]
    [InlineData("diagram", "--strict")]
    [InlineData("exceptions", "--uncaught", "--max-depth", "3")]
    [InlineData("exceptions", "--format", "json")]
    [InlineData("exceptions", "--caught", "--format", "msbuild")]
    [InlineData("exceptions", "--format", "msbuild", "--locations")]
    public async Task Invalid_requests_fail_before_loading(string command, params string[] options)
    {
        var result = await Run([command, "missing.slnx", .. options]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("Input not found", result.Error);
    }
    [Fact]
    public async Task Skill_installs_at_the_repository_root_refreshes_its_own_file_and_keeps_foreign_ones()
    {
        var repository = Directory.CreateTempSubdirectory("sharpview-skill-");
        try
        {
            Directory.CreateDirectory(Path.Combine(repository.FullName, ".git"));
            var nested = Directory.CreateDirectory(Path.Combine(repository.FullName, "src", "Api")).FullName;
            var (copilot, skill) = (Path.Combine(repository.FullName, ".github", "skills", "sharpview", "SKILL.md"), Path.Combine(repository.FullName, ".claude", "skills", "sharpview", "SKILL.md"));
            var first = await RunIn(nested, "skill");
            Assert.Equal(0, first.Exit);
            Assert.Equal($"{copilot}\n{skill}\n", first.Out.ReplaceLineEndings("\n"));
            var text = File.ReadAllText(skill);
            Assert.Equal(text, File.ReadAllText(copilot));
            // Frontmatter holds only name and description: the version marker follows it, out of every agent's YAML parser.
            Assert.Matches("^---\nname: sharpview\ndescription: [^\n]+\n---\n<!-- Written by `sharpview skill` ", text);
            // The skill must keep up with the CLI: every command, view and warning code is named.
            foreach (var term in new[] { "sharpview projects", "sharpview exceptions --uncaught", "--strict", "--caught", "--miscaught", 
                "sharpview diagram", "--format msbuild", "SV0000", "SV0001", "SV0002", "SV0003", "SV0004", "SV0005" })
                Assert.Contains(term, text);
            Assert.Equal(0, (await RunIn(nested, "skill")).Exit);
            File.WriteAllText(skill, "my own skill");
            var foreign = await RunIn(nested, "skill");
            Assert.Equal(2, foreign.Exit);
            Assert.Contains("was not written by sharpview", foreign.Error);
            Assert.Equal("my own skill", File.ReadAllText(skill));
            // A refusal leaves the other folder as it was.
            Assert.Equal(text, File.ReadAllText(copilot));
            Assert.Equal(2, (await RunIn(nested, "skill", "--global")).Exit);
        }
        finally { repository.Delete(true); }
    }
    [Fact]
    public void Skill_hint_asks_for_an_install_or_an_update_and_otherwise_stays_quiet()
    {
        var root = Directory.CreateTempSubdirectory("sharpview-hint-");
        try
        {
            var (repository, home) = (Directory.CreateDirectory(Path.Combine(root.FullName, "repo")).FullName, Directory.CreateDirectory(Path.Combine(root.FullName, "home")).FullName);
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            var nested = Directory.CreateDirectory(Path.Combine(repository, "src")).FullName;
            void Write(string folder, string text) { Directory.CreateDirectory(Path.Combine(folder, "skills", "sharpview")); File.WriteAllText(Path.Combine(folder, "skills", "sharpview", "SKILL.md"), text); }
            static string Ours(string version) => $"---\nname: sharpview\ndescription: x\n# Written by `sharpview skill` {version}. Rerun it to refresh this file.\n---\n";
            Assert.Equal("Tip: run `sharpview skill` so GitHub Copilot and Claude Code can use SharpView in this repository.", Cli.SkillCommand.HintFor(nested, home, "0.3.0"));
            Write(Path.Combine(home, ".copilot"), Ours("0.2.1"));
            Assert.Equal($"Tip: the SharpView skill in {Path.Combine(home, ".copilot", "skills", "sharpview", "SKILL.md")} is from 0.2.1; run `sharpview skill --user` to update it.",
                Cli.SkillCommand.HintFor(nested, home, "0.3.0"));
            // Local builds do not compare versions; a current skill, or one sharpview did not write, needs nothing.
            Assert.Null(Cli.SkillCommand.HintFor(nested, home, "0.0.0-dev"));
            Write(Path.Combine(home, ".copilot"), Ours("0.3.0"));
            Assert.Null(Cli.SkillCommand.HintFor(nested, home, "0.3.0"));
            // The marker after the frontmatter, as written now, is read the same way.
            Write(Path.Combine(home, ".copilot"), "---\nname: sharpview\ndescription: x\n---\n<!-- Written by `sharpview skill` 0.2.1. Rerun it. -->\n");
            Assert.Contains("is from 0.2.1;", Cli.SkillCommand.HintFor(nested, home, "0.3.0"));
            // A planted marker cannot put terminal escape sequences on the console.
            Write(Path.Combine(home, ".copilot"), Ours("\u001b]0;pwned\u0007"));
            Assert.Null(Cli.SkillCommand.HintFor(nested, home, "0.3.0"));
            Directory.Delete(Path.Combine(home, ".copilot"), true);
            Write(Path.Combine(repository, ".github"), "---\nname: sharpview\n---\nour own words");
            Assert.Null(Cli.SkillCommand.HintFor(nested, home, "0.3.0"));
        }
        finally { root.Delete(true); }
    }
    [Fact]
    public async Task Skill_user_installs_for_copilot_and_claude_code()
    {
        var home = Directory.CreateTempSubdirectory("sharpview-home-");
        try
        {
            var result = await RunAs(home.FullName, home.FullName, "skill", "--user");
            Assert.Equal(0, result.Exit);
            foreach (var agent in new[] { ".copilot", ".claude" })
                Assert.StartsWith("---\nname: sharpview\n", File.ReadAllText(Path.Combine(home.FullName, agent, "skills", "sharpview", "SKILL.md")));
        }
        finally { home.Delete(true); }
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
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md");
        try
        {
            var result = await Run("diagram", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--max-depth", "0", "--output", path);
            Assert.Equal(1, result.Exit);
            Assert.Contains("The diagram is cut short (depth); raise --max-depth", result.Out);
            Assert.Contains("Truncated: Depth limit reached", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("chains")]
    [InlineData("controllers")]
    [InlineData("methods")]
    [InlineData("layers")]
    [InlineData("reach")]
    [InlineData("paths")]
    [InlineData("inspect")]
    public async Task Removed_commands_are_rejected(string command)
    {
        var result = await Run(command, "missing.slnx");
        Assert.Equal(2, result.Exit); Assert.Contains("Unknown command", result.Error);
    }
    [Theory]
    [InlineData("exceptions", "--dispatch", "direct")]
    [InlineData("exceptions", "--summary")]
    [InlineData("exceptions", "--paths")]
    [InlineData("diagram", "--layer", "project:X=X")]
    [InlineData("diagram", "--allow", "A=B")]
    [InlineData("exceptions", "--max-facts", "5")]
    [InlineData("diagram", "--max-chains", "5")]
    [InlineData("diagram", "--max-expansions", "5")]
    [InlineData("diagram", "--root-id", "x")]
    [InlineData("diagram", "--follow-candidates")]
    [InlineData("diagram", "--max-paths", "5")]
    [InlineData("projects", "--methods")]
    [InlineData("projects", "--json")]
    public async Task Removed_options_are_rejected_before_loading(string command, params string[] options)
    {
        var result = await Run([command, "missing.slnx", .. options]);
        Assert.Equal(2, result.Exit); Assert.Contains("Unknown option", result.Error);
    }
    [Fact]
    public async Task Option_of_another_command_names_the_command()
    {
        var result = await Run("projects", "missing.slnx", "--declared-only");
        Assert.Equal(2, result.Exit); Assert.Contains("projects does not accept --declared-only", result.Error);
    }
    [Fact]
    public async Task Exception_output_is_byte_identical_across_runs()
    {
        var first = await Run("exceptions", ResearchFixture.Input, "--caught");
        var second = await Run("exceptions", ResearchFixture.Input, "--caught");
        Assert.Equal(1, first.Exit); Assert.NotEmpty(first.Out);
        Assert.Equal((first.Out, first.Error), (second.Out, second.Error));
    }
    [Fact]
    public async Task Unmatched_root_suggests_actions_by_text_then_by_method_name()
    {
        var text = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "orderscontroller.get");
        Assert.Equal(2, text.Exit);
        Assert.Contains("No controller action matches --root orderscontroller.get exactly. Did you mean:\n  Research.Api.OrdersController.Get()\n", text.Error.ReplaceLineEndings("\n"));
        var name = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Shop.OrdersController.GET(int)");
        Assert.Contains("Did you mean:\n  Research.Api.OrdersController.Get()\n", name.Error.ReplaceLineEndings("\n"));
        var none = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--root", "Nothing()");
        Assert.Contains("No controller action matches --root Nothing(). Pass an action's full signature", none.Error);
    }
    [Fact]
    public async Task Output_refuses_to_overwrite_an_input_file()
    {
        var before = await File.ReadAllBytesAsync(ResearchFixture.Input);
        var result = await Run("projects", ResearchFixture.Input, "--mermaid", "--output", ResearchFixture.Input);
        Assert.Equal(2, result.Exit);
        Assert.Equal(before, await File.ReadAllBytesAsync(ResearchFixture.Input));
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
        Assert.Contains("   Caught by:  Service.Caught  Research.Application/Service.cs:6\n               catch (Research.Infrastructure.StorageException)\n", text);
        Assert.Contains("   Thrown:     Repository.Read  Research.Infrastructure", text);
        Assert.Contains("   Path:       OrdersController.", text);
        Assert.Contains("Handler:    rethrows it", text);
        Assert.Contains("Handler:    throws a different exception instead", text);
        Assert.Contains("Handler:    handles it (only if the filter accepts it)", text);
        Assert.DoesNotContain("Service.Incompatible()", text);
        // Findings follow an overview by exception type, each numbered and separated by a blank line.
        Assert.StartsWith("Overview: 8 caught exceptions\n  8  Research.Infrastructure.StorageException\n\n1. Research.Infrastructure.StorageException\n", text);
    }
    [Fact]
    public async Task Miscaught_reports_the_try_whose_clauses_do_not_match_and_what_happens_next()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--miscaught");
        Assert.Equal(1, result.Exit);
        var text = result.Out.Replace("\r\n", "\n");
        Assert.Contains("1. Research.Infrastructure.StorageException", text);
        Assert.Contains("   Missed by:  Service.Incompatible  Research.Application/Service.cs:8\n               try { ... } catch (System.ArgumentException)\n", text);
        Assert.Contains("   Then:       may escape OrdersController.Incompatible\n   Handled:    by the global exception handler UseExceptionHandler  Research.Api/Program.cs:5\n", text);
        Assert.Contains("OrdersController.Incompatible -> Service.Incompatible -> Repository.Read\n", text);
        // A miss inside an async void method cannot be caught further up: the process ends.
        Assert.Contains("   Missed by:  Service.Fire  Research.Application/Service.cs:19\n               try { ... } catch (System.ArgumentException)\n"
            + "   Then:       leaves an async void method, where no caller can catch it; the process ends\n", text);
        Assert.StartsWith("Overview: 2 miscaught exceptions (1 handled by a global exception handler)\n", text);
        Assert.Contains("13 actions analysed: 2 miscaught (1 handled by a global exception handler).", result.Error);
    }
    [Fact]
    public async Task Msbuild_format_prints_one_canonical_warning_per_finding()
    {
        var result = await Run("exceptions", ResearchFixture.Input, "--format", "msbuild", "--strict");
        Assert.Equal(1, result.Exit);
        var lines = result.Out.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        // Without a view: the async void crash, the uncaught findings (all reach the fixture's exception middleware), then the miscaught ones.
        Assert.Equal(["SV0005", "SV0002", "SV0002", "SV0002", "SV0003", "SV0003"], lines.Select(x => x.Split(": warning ")[1][..6]));
        var repository = Path.Combine(Path.GetDirectoryName(ResearchFixture.Input)!, "Research.Infrastructure", "Repository.cs");
        Assert.Equal($"{repository}(8,64): warning SV0005: Research.Infrastructure.StorageException escapes async void Service.Fire, where no caller can catch it, "
            + "and ends the process; reached from OrdersController.Fire; path: OrdersController.Fire -> Service.Fire -> Repository.ReadAsync", lines[0]);
        var service = Path.Combine(Path.GetDirectoryName(ResearchFixture.Input)!, "Research.Application", "Service.cs");
        Assert.Equal($"{service}(8,45): warning SV0003: Research.Infrastructure.StorageException thrown in Repository.Read (Repository.cs:6) passes through this try, "
            + "which catches only System.ArgumentException; then may escape OrdersController.Incompatible to the global exception handler UseExceptionHandler; path: OrdersController.Incompatible -> Service.Incompatible -> Repository.Read", lines[5]);
        Assert.Contains(lines, x => x.EndsWith("warning SV0002: Research.Infrastructure.StorageException may escape OrdersController.Rethrow to the global exception handler UseExceptionHandler, "
            + "rethrown in Service.Rethrow (Service.cs:9); path: OrdersController.Rethrow -> Service.Rethrow -> Repository.Read", StringComparison.Ordinal));
        // Both views print one summary line between them.
        Assert.Single(result.Error.Split('\n'), x => x.StartsWith("13 actions analysed: ", StringComparison.Ordinal));
        Assert.Contains("13 actions analysed: 3 uncaught exceptions (3 handled by a global exception handler); 1 crash from async void; every third-party call has a catch on the way; "
            + "2 miscaught (1 handled by a global exception handler).", result.Error);
        // A view narrows the warnings to that view.
        Assert.Equal([lines[4], lines[5]], (await Run("exceptions", ResearchFixture.Input, "--miscaught", "--format", "msbuild")).Out.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
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

    [Fact]
    public async Task Strict_adds_a_third_party_count_and_leaves_framework_calls_out()
    {
        var plain = await Run("exceptions", ResearchFixture.Input, "--uncaught");
        var strict = await Run("exceptions", ResearchFixture.Input, "--uncaught", "--strict");
        // The fixture calls only .NET itself, so strict mode adds no section.
        Assert.Equal(plain.Out, strict.Out);
        // The skill tip is for a person at a terminal; redirected stderr (agents, CI) never gets it.
        Assert.DoesNotContain("Tip:", strict.Error);
        Assert.Contains("13 actions analysed: 3 uncaught exceptions (3 handled by a global exception handler); 1 crash from async void; every third-party call has a catch on the way.", strict.Error);
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
    public async Task Missing_restoration_is_rejected_without_creating_assets_or_changing_source_unless_restore_is_asked()
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
            var result = await Run("projects", input);
            Assert.Equal(2, result.Exit);
            Assert.Contains("not restored", result.Error);
            Assert.False(File.Exists(Path.Combine(directory, "obj", "project.assets.json")));
            Assert.Equal(project, await File.ReadAllTextAsync(input));
            Assert.Equal(code, await File.ReadAllTextAsync(source));
            var restored = await Run("projects", input, "--restore");
            Assert.Equal(0, restored.Exit);
            Assert.StartsWith("Unrestored [net10.0", restored.Out);
            Assert.True(File.Exists(Path.Combine(directory, "obj", "project.assets.json")));
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
    [InlineData("--max-nodes", "5")]
    public async Task Exception_views_have_no_depth_or_size_limits(string option, params string[] value)
    {
        var result = await Run(["exceptions", "missing.slnx", "--uncaught", option, .. value]);
        Assert.Equal(2, result.Exit); Assert.Contains($"exceptions does not accept {option}", result.Error);
    }
    [Fact]
    public async Task Uncaught_merges_throws_of_one_method_and_refers_to_a_shared_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpview paths " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "Paths.csproj");
        try
        {
            await File.WriteAllTextAsync(input, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(directory, "Code.cs"), """
                using Microsoft.AspNetCore.Mvc;
                public interface IStore { int Load(int id); }
                public sealed class SqlStore : IStore { public int Load(int id) => Cache.Get(id); }
                public sealed class FileStore : IStore { public int Load(int id) => 0; }
                public static class Cache
                {
                    public static int Get(int id) => id < 0 ? throw new System.ArgumentException("negative") : Decode(id);
                    private static int Decode(int id)
                    {
                        if (id == 1) throw new System.FormatException("one");
                        if (id == 2) throw new System.FormatException("two");
                        return Parse(id);
                    }
                    private static int Parse(int id) => id == 3 ? throw new System.OverflowException() : id;
                }
                [ApiController]
                public sealed class ItemsController(IStore store) : ControllerBase
                {
                    [HttpGet("a")] public int A(int id) => Pass(id);
                    [HttpGet("b")] public int B(int id) => Pass(id);
                    private int Pass(int id) => store.Load(id);
                }
                """);
            using (var restore = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", ["restore", input]) { RedirectStandardOutput = true })!)
            { await restore.StandardOutput.ReadToEndAsync(); await restore.WaitForExitAsync(); Assert.Equal(0, restore.ExitCode); }
            var result = await RunIn(directory, "exceptions", input, "--uncaught");
            Assert.Equal(0, result.Exit);
            Assert.EndsWith("""
                1. System.ArgumentException
                   Thrown:     Cache.Get  Code.cs:7
                   Escapes:    ItemsController.A, ItemsController.B
                   Path:       ItemsController.A -> ItemsController.Pass ~> SqlStore.Load (1 of 2 implementations) -> Cache.Get

                2. System.FormatException
                   Thrown:     Cache.Decode  Code.cs:10, 11
                   Escapes:    ItemsController.A, ItemsController.B
                   Path:       as in 1 up to Cache.Get, then -> Cache.Decode

                3. System.OverflowException
                   Thrown:     Cache.Parse  Code.cs:14
                   Escapes:    ItemsController.A, ItemsController.B
                   Path:       as in 1 up to Cache.Get, then -> Cache.Decode -> Cache.Parse

                """, result.Out.ReplaceLineEndings("\n"));
            // Build warnings keep one per throw, each with its full path.
            var warnings = await RunIn(directory, "exceptions", input, "--format", "msbuild");
            Assert.Equal(4, warnings.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { Directory.Delete(directory, true); }
    }
}
