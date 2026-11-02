using System.Diagnostics;
using SharpDrill.Core;

namespace SharpDrill.Cli;

/// <summary>--restore: runs <c>dotnet restore</c> on the input before the load. Opt-in, because restore writes obj/, may reach
/// package feeds and runs package tooling; without it the analysed code and its folders are never touched.</summary>
internal static class Restore
{
    internal static async Task RunAsync(Arguments arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in new[] { "restore", arguments.Input, $"-p:Configuration={arguments.Configuration}", "-nologo", "-v", "quiet" }) start.ArgumentList.Add(x);
        using var process = Process.Start(start) ?? throw new AnalysisInputException("Could not start dotnet restore.");
        // stdout is the report: everything restore prints goes to stderr.
        var copied = Task.WhenAll(Copy(process.StandardOutput), Copy(process.StandardError));
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        await copied;
        if (process.ExitCode != 0) throw new AnalysisInputException($"dotnet restore failed for {arguments.Input} (exit {process.ExitCode}); see its output above.");

        static async Task Copy(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) await Console.Error.WriteLineAsync(line); }
    }
}
