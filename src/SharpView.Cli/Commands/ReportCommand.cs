using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

internal sealed record ReportContext(ProgramIndex Index, Arguments Arguments, TextWriter Output, Stream? OutputFile, Display Display)
{
    internal ProgramSnapshot Snapshot => Index.Snapshot;
}

/// <summary>Text-report commands: shared output target, coverage trailer and exit code.</summary>
internal abstract class ReportCommand : ICliCommand
{
    public abstract ImmutableHashSet<string> Options { get; }

    public virtual void Validate(Arguments arguments) { }

    public async Task<int> RunAsync(ProgramIndex index, Arguments arguments, CancellationToken token)
    {
        using var file = arguments.Output is null ? null : new StreamWriter(new FileStream(arguments.Output, FileMode.CreateNew, FileAccess.Write));
        var output = (TextWriter?)file ?? Console.Out;
        var context = new ReportContext(index, arguments, output, file?.BaseStream, new Display(index, arguments.Input));
        var incomplete = await ExecuteAsync(context, token);
        token.ThrowIfCancellationRequested();
        return WriteCoverage(index.Snapshot) | incomplete ? 1 : 0;
    }

    /// <returns>True when the command's own results are incomplete within the model.</returns>
    protected abstract ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token);

    // Only what changes how far the results can be trusted: projects that did not load, code that does not compile,
    // endpoints outside the analysis, and SharpView's own failures. Limits on a reported path show up with the result.
    private static bool WriteCoverage(ProgramSnapshot snapshot)
    {
        var load = snapshot.Gaps.Where(x => x.Kind == GapKind.ProjectLoad).ToArray();
        foreach (var gap in load) Console.Error.WriteLine($"Not loaded: {gap.Message}");
        var errors = snapshot.Gaps.Count(x => x.Kind == GapKind.Compilation);
        if (errors > 0) Console.Error.WriteLine($"The code has {Display.Plural(errors, "compile error")}; results around them may be incomplete. Build the solution first.");
        var minimal = snapshot.Gaps.Count(x => x.Message.StartsWith("Minimal API endpoint", StringComparison.Ordinal));
        if (minimal > 0) Console.Error.WriteLine($"Not analysed: {Display.Plural(minimal, "minimal API endpoint")} (only controller actions are).");
        var bugs = snapshot.Gaps.Where(x => x.Kind == GapKind.Internal).ToArray();
        if (bugs.Length > 0)
            BugReport.Write($"SharpView could not handle {Display.Plural(bugs.Length, "place")} in your code; results there are missing.",
                bugs.Take(10).Select(x => $"{x.Message} {(x.Evidence is { } e ? $"({Path.GetFileName(e.Path)}:{e.StartLine}:{e.StartColumn})" : "")}"));
        return load.Length > 0 || errors > 0 || minimal > 0 || bugs.Length > 0;
    }
}

/// <summary>How SharpView owns up to its own bugs: say so plainly and ask for the output in an issue.</summary>
internal static class BugReport
{
    internal const string Issues = "https://github.com/stadtaev/sharpview/issues";

    internal static void Write(string what, IEnumerable<string> details)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Sorry, we hit a bug on our side. {what}");
        Console.Error.WriteLine($"We are enthusiasts and fix these quickly when we hear about them: please copy-paste the lines below into an issue at {Issues}");
        Console.Error.WriteLine($"  sharpview {EntryPoint.Version}, .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        foreach (var line in details) Console.Error.WriteLine("  " + line);
    }
}
