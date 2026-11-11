using System.Collections.Immutable;
using SharpDrill.Core;

namespace SharpDrill.Cli;

// --format msbuild: one warning line per finding, for build logs, VS Code and Visual Studio.
internal sealed partial class ExceptionsCommand
{
    // Warnings the source suppresses; one count per run (one process runs one command).
    private static int suppressed;

    // --format msbuild: MSBuild's canonical message format, which Visual Studio's Error List and VS Code's $msCompile
    // problem matcher both parse. The path stays absolute so the location resolves from any working directory.
    private static void Warn(ReportContext context, string code, SourceEvidence at, string message)
    {
        if (context.Snapshot.Suppressions.Any(x => x.Covers(code, at))) { suppressed++; return; }
        context.Output.WriteLine($"{at.Path}({at.StartLine},{at.StartColumn}): warning {code}: {message}");
    }

    // Other places in a warning: the file name is enough beside the method, and keeps the line readable in the Error List.
    private static string FileLine(SourceEvidence at) => $"{Path.GetFileName(at.Path)}:{at.StartLine}";

    // The try is where the fix goes, so the warning points at it.
    private static void WarnMiscaught(ReportContext context, MiscaughtException miscaught)
    {
        var origin = miscaught.Thrown;
        var clauses = miscaught.Clauses.Select(x => x.CaughtType?.DisplayName ?? "all exceptions");
        Warn(context, "SD0003", miscaught.MissedBy.ProtectedBlock, $"{miscaught.ExceptionType ?? Thrown(origin)} thrown in {context.Display.Short(origin.Owner)} ({FileLine(origin.Evidence)}) "
            + $"passes through this try, which catches only {string.Join(", ", clauses)}; then {string.Join("; ", miscaught.Then.Select(x => Fate(context, miscaught, x, msbuild: true)))}; "
            + Chain(context, miscaught.Example));
    }

    private static string Chain(ReportContext context, ExceptionPath path) =>
        $"path{(path.IsAssumed ? " (possible, not proven)" : "")}: {string.Join(" -> ", path.Steps.Select(x => context.Display.Short(x.Method.Id)))}";
}
