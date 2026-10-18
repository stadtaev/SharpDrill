using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

internal sealed record ReportContext(ProgramIndex Index, Arguments Arguments, TextWriter Output, Stream? OutputFile, Display Display)
{
    internal ProgramSnapshot Snapshot => Index.Snapshot;
}

/// <summary>Text-report commands: shared output target, extraction-gap trailer and exit code.</summary>
internal abstract class ReportCommand : ICliCommand
{
    public abstract ImmutableHashSet<string> Options { get; }

    public virtual void Validate(Arguments arguments) { }

    public async Task<int> RunAsync(ProgramIndex index, Arguments arguments, CancellationToken token)
    {
        using var file = arguments.Output is null ? null : new StreamWriter(new FileStream(arguments.Output, FileMode.CreateNew, FileAccess.Write));
        var output = (TextWriter?)file ?? Console.Out;
        var context = new ReportContext(index, arguments, output, file?.BaseStream, new Display(index));
        var incomplete = await ExecuteAsync(context, token) | !index.Snapshot.Gaps.IsEmpty;
        WriteGaps(index.Snapshot.Gaps);
        token.ThrowIfCancellationRequested();
        return incomplete ? 1 : 0;
    }

    /// <returns>True when the command's own results are incomplete within the model.</returns>
    protected abstract ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token);

    private static void WriteGaps(IReadOnlyList<AnalysisGap> gaps)
    {
        foreach (var gap in gaps.Take(20)) Console.Error.WriteLine($"Gap [{gap.Kind}]: {gap.Message}" + (gap.Evidence is null ? "" : $" ({Display.Location(gap.Evidence)})"));
        if (gaps.Count > 20) Console.Error.WriteLine($"Further gaps: {gaps.Count - 20}; inspect JSON for full evidence.");
        Console.Error.WriteLine($"Extraction gaps: {gaps.Count}. Coverage: explicit origins/direct calls; minimal APIs and inherited actions outside scope.");
    }
}
