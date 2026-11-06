using System.Diagnostics;
using SharpDrill.Core;

namespace SharpDrill.Cli;

/// <summary>Runs <c>dotnet restore</c> on the input, kept short and quiet: stdout is the report, and what restore printed
/// matters only when it failed. It writes obj/ and may reach package feeds; only the CLI does this, never the loader.</summary>
internal static class Restore
{
    internal static async Task RunAsync(Arguments arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in new[] { "restore", arguments.Input, $"-p:Configuration={arguments.Configuration}", "-nologo", "-v", "quiet" }) start.ArgumentList.Add(x);
        using var process = Process.Start(start) ?? throw new AnalysisInputException("Could not start dotnet restore.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode == 0) return;
        await Console.Error.WriteAsync(await output + await errors);
        throw new AnalysisInputException($"dotnet restore failed for {arguments.Input} (exit {process.ExitCode}); see its output above.");
    }
}
