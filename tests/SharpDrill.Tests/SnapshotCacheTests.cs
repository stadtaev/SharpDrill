using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace SharpDrill.Tests;

internal static class TestEnvironment
{
    // The CLI runs as a child process and inherits this: test runs never touch the user's own cache.
    [ModuleInitializer]
    internal static void IsolateCache() => Environment.SetEnvironmentVariable("SHARPDRILL_CACHE_DIR", Path.Combine(Path.GetTempPath(), "sharpdrill-tests-" + Environment.ProcessId));
}

public sealed class SnapshotCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sharpdrill-cache-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(directory, "cache");
    private string App => Path.Combine(directory, "src");
    private string Input => Path.Combine(App, "App.csproj");
    private const string Controller = """
        using Microsoft.AspNetCore.Mvc;
        [ApiController, Route("x")] public class ItemsController : ControllerBase {
            [HttpGet] public int Get() => Helper.Load();
        }
        """;
    private const string Throwing = "public static class Helper { public static int Load() => throw new System.FormatException(); }";
    private const string Quiet = "public static class Helper { public static int Load() => 1; }";

    public SnapshotCacheTests()
    {
        Directory.CreateDirectory(App);
        File.WriteAllText(Input, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(App, "Controller.cs"), Controller);
        File.WriteAllText(Path.Combine(App, "Helper.cs"), Throwing);
    }
    public void Dispose() => Directory.Delete(directory, true);

    private async Task<(int Exit, string Out, string Error, long Ms)> Run(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = App };
        start.Environment["SHARPDRILL_CACHE_DIR"] = Cache;
        start.ArgumentList.Add(Path.Combine(ResearchFixture.Root, $"src/SharpDrill.Cli/bin/{configuration}/net10.0/SharpDrill.Cli.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout, await stderr, watch.ElapsedMilliseconds);
    }
    private Task<(int Exit, string Out, string Error, long Ms)> Uncaught(params string[] more) => Run(["exceptions", Input, "--uncaught", .. more]);
    private string Entry => Assert.Single(Directory.GetFiles(Cache));

    [Theory]
    [InlineData("exceptions", "--uncaught")]
    [InlineData("exceptions", "--caught")]
    [InlineData("exceptions", "--miscaught")]
    [InlineData("exceptions", "--format", "msbuild")]
    [InlineData("projects")]
    public async Task A_cached_result_prints_exactly_what_a_fresh_analysis_prints(params string[] command)
    {
        string[] args = [command[0], Input, .. command[1..]];
        var first = await Run(args);
        Assert.True(File.Exists(Entry));
        var written = File.GetLastWriteTimeUtc(Entry);
        var cached = await Run(args);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Entry));
        var fresh = await Run([.. args, "--no-cache"]);
        Assert.NotEqual(written, File.GetLastWriteTimeUtc(Entry));
        foreach (var other in new[] { cached, fresh }) Assert.Equal((first.Exit, first.Out, first.Error.Replace("Restoring App.csproj..." + Environment.NewLine, "")), (other.Exit, other.Out, other.Error));
    }

    [Fact]
    public async Task Different_commands_share_one_analysis()
    {
        await Uncaught();
        var written = File.GetLastWriteTimeUtc(Entry);
        Assert.Contains("App", (await Run("projects", Input)).Out);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Entry));
    }

    [Fact]
    public async Task Editing_a_source_file_is_seen()
    {
        Assert.Contains("System.FormatException", (await Uncaught()).Out);
        await File.WriteAllTextAsync(Path.Combine(App, "Helper.cs"), Quiet);
        Assert.StartsWith("0 uncaught", (await Uncaught()).Error);
    }

    [Fact]
    public async Task A_same_size_edit_with_the_same_timestamp_is_seen()
    {
        Assert.Contains("System.FormatException", (await Uncaught()).Out);
        var path = Path.Combine(App, "Helper.cs");
        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, Throwing.Replace("FormatException", "RankException  "));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Contains("System.RankException", (await Uncaught()).Out);
    }

    [Fact]
    public async Task A_new_source_file_is_seen()
    {
        await Uncaught();
        await File.WriteAllTextAsync(Path.Combine(App, "Deep.cs"), Quiet.Replace("Helper", "Other"));
        var written = File.GetLastWriteTimeUtc(Entry);
        await Uncaught();
        Assert.NotEqual(written, File.GetLastWriteTimeUtc(Entry));
    }

    [Fact]
    public async Task A_new_build_props_above_the_project_is_seen()
    {
        await Uncaught();
        var written = File.GetLastWriteTimeUtc(Entry);
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), "<Project><PropertyGroup><DefineConstants>$(DefineConstants);EXTRA</DefineConstants></PropertyGroup></Project>");
        await Uncaught();
        Assert.NotEqual(written, File.GetLastWriteTimeUtc(Entry));
    }

    [Fact]
    public async Task Deleted_restore_assets_restore_again_instead_of_using_the_cache()
    {
        await Uncaught();
        Directory.Delete(Path.Combine(App, "obj"), true);
        var result = await Uncaught();
        Assert.Contains("Restoring App.csproj...", result.Error);
        Assert.Contains("System.FormatException", result.Out);
    }

    [Fact]
    public async Task Code_that_does_not_compile_is_never_cached()
    {
        await File.WriteAllTextAsync(Path.Combine(App, "Broken.cs"), "public class Broken { Missing field; }");
        Assert.Contains("compile error", (await Uncaught()).Error);
        Assert.Empty(Directory.Exists(Cache) ? Directory.GetFiles(Cache) : []);
    }

    [Fact]
    public async Task A_corrupt_cache_file_is_a_miss()
    {
        var first = await Uncaught();
        await File.WriteAllBytesAsync(Entry, [1, 2, 3, 4, 5]);
        var again = await Uncaught();
        Assert.Equal((first.Exit, first.Out), (again.Exit, again.Out));
        Assert.DoesNotContain("bug", again.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_options_do_not_share_a_cache_entry()
    {
        await Uncaught();
        await Uncaught("--configuration", "Release");
        Assert.Equal(2, Directory.GetFiles(Cache).Length);
    }

    [Fact]
    public async Task No_restore_stops_and_both_restore_flags_are_refused()
    {
        var stopped = await Uncaught("--no-restore");
        Assert.Equal(2, stopped.Exit);
        Assert.Contains("not restored", stopped.Error);
        Assert.False(File.Exists(Path.Combine(App, "obj", "project.assets.json")));
        Assert.Contains("cannot be combined", (await Uncaught("--restore", "--no-restore")).Error);
    }
}
