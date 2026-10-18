using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SharpView.Tests;

public sealed class DiagramCliTests
{
    private static Task<(int Exit, string Out, string Error)> Run(string input, params string[] args) => RunIn(ResearchFixture.Root, input, args);

    private static async Task<(int Exit, string Out, string Error)> RunIn(string directory, string input, params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        start.ArgumentList.Add(Path.Combine(ResearchFixture.Root, $"src/SharpView.Cli/bin/{configuration}/net10.0/SharpView.Cli.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Picker_retries_invalid_input_saves_under_current_directory_and_prints_absolute_path()
    {
        var directory = Directory.CreateTempSubdirectory("sharpview-diagram-").FullName;
        var result = await RunIn(directory, "invalid\n0\n1\n", "diagram", ResearchFixture.Input);
        Assert.Equal(1, result.Exit); // fixture has intentional extraction gaps
        Assert.Contains("1. Research.Api.OrdersController.", result.Out);
        Assert.Contains("Choose a number", result.Out);
        var line = Assert.Single(result.Out.Split('\n'), x => x.StartsWith("Saved diagram: ", StringComparison.Ordinal));
        var path = line["Saved diagram: ".Length..].TrimEnd('\r');
        try
        {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal("diagrams_exports", new FileInfo(path).Directory!.Name);
            Assert.Equal(Path.Combine(directory, "diagrams_exports"), Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
            Assert.Contains("```mermaid", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Explicit_action_id_selects_without_prompt_and_preserves_dispatch_when_candidates_are_absent()
    {
        var snapshot = await Run("", "inspect", ResearchFixture.Input, "--json");
        using var json = JsonDocument.Parse(snapshot.Out);
        var id = json.RootElement.GetProperty("snapshot").GetProperty("callables").EnumerateArray()
            .Single(x => x.GetProperty("displaySignature").GetString() == "Research.Api.OrdersController.Interface(Research.Infrastructure.IRepository)")
            .GetProperty("id").GetString()!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md");
        try
        {
            var result = await Run("", "diagram", ResearchFixture.Input, "--root", id, "--follow-candidates", "--output", path);
            Assert.Equal(1, result.Exit); Assert.DoesNotContain("Choose an action", result.Out);
            Assert.Contains("Saved diagram: " + Path.GetFullPath(path), result.Out);
            var document = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(": possible candidate", document); Assert.Contains("declared dispatch", document);
            Assert.Contains("Implementation candidates followed: True", document);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("q\n")]
    public async Task Eof_or_quit_cancels_without_creating_output(string input)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md");
        var result = await Run(input, "diagram", ResearchFixture.Input, "--output", path);
        Assert.Equal(0, result.Exit);
        Assert.Contains("No diagram saved", result.Out);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Explicit_root_exports_project_groups_evidence_and_limits_without_prompt_and_refuses_overwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md");
        string[] args = ["diagram", ResearchFixture.Input, "--root", "Research.Api.OrdersController.Get()", "--max-nodes", "2", "--output", path];
        try
        {
            var result = await Run("", args);
            Assert.Equal(1, result.Exit);
            Assert.DoesNotContain("Choose an action", result.Out);
            Assert.Contains("Saved diagram: " + Path.GetFullPath(path), result.Out);
            var document = await File.ReadAllTextAsync(path);
            Assert.Contains("Research.Api", document); Assert.Contains("Research.Application", document);
            Assert.Contains("Truncated: Nodes", document);
            Assert.Contains("Call-site evidence", document);
            var existing = await Run("", args);
            Assert.Equal(2, existing.Exit);
            Assert.Contains("overwrite", existing.Error);
            Assert.Equal(document, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Uncaught_reports_known_escapes_alongside_unknowns_and_limits()
    {
        var result = await Run("", "exceptions", ResearchFixture.Input, "--uncaught", "--root", "Research.Api.OrdersController.Get()");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Exception summary: Research.Api.OrdersController.Get()", result.Out);
        Assert.Contains("May escape: Research.Infrastructure.StorageException", result.Out);
        Assert.Contains("Unknown [MissingBody]", result.Out);
        Assert.Contains("Path: Research.Api.OrdersController.Get() -->", result.Out);
        var limited = await Run("", "exceptions", ResearchFixture.Input, "--uncaught", "--root", "Research.Api.OrdersController.Get()", "--budget", "1");
        Assert.Equal(1, limited.Exit); Assert.Contains("work limit: True", limited.Error);
        var invalid = await Run("", "reach", "missing.slnx", "--uncaught");
        Assert.Equal(2, invalid.Exit); Assert.DoesNotContain("Analysis input not found", invalid.Error);
    }

    [Theory]
    [InlineData("--max-depth", "1")]
    [InlineData("--max-paths", "10")]
    [InlineData("--budget", "0")]
    public async Task Invalid_summary_options_fail_before_loading(string option, string value)
    {
        var result = await Run("", "exceptions", "missing.slnx", "--uncaught", option, value);
        Assert.Equal(2, result.Exit); Assert.DoesNotContain("Analysis input not found", result.Error);
    }

    [Theory]
    [InlineData("--max-nodes", "0")]
    [InlineData("--max-edges", "0")]
    [InlineData("--max-paths", "10")]
    [InlineData("--budget", "0")]
    [InlineData("--layer", "project:X=X")]
    public async Task Invalid_diagram_options_fail_before_loading(string option, string value)
    {
        var result = await Run("", "diagram", "missing.slnx", option, value);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("Analysis input not found", result.Error);
    }

    [Fact]
    public async Task Diagram_requires_controller_action_and_size_options_are_diagram_only()
    {
        var root = await Run("", "diagram", ResearchFixture.Input, "--root", "Research.Application.Service.Load()");
        Assert.Equal(2, root.Exit); Assert.Contains("recognised action", root.Error);
        var wrongCommand = await Run("", "reach", "missing.slnx", "--max-nodes", "20");
        Assert.Equal(2, wrongCommand.Exit); Assert.DoesNotContain("Analysis input not found", wrongCommand.Error);
    }
}
