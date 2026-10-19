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

    // Gaps are grouped by cause: a real solution has hundreds, usually a handful of kinds repeated many times.
    private static void WriteGaps(IReadOnlyList<AnalysisGap> gaps)
    {
        var groups = gaps.GroupBy(x => (x.Kind, x.Message))
            .Select(g => (g.Key.Kind, g.Key.Message, Count: g.Count(), First: g.FirstOrDefault(x => x.Evidence is not null)?.Evidence))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Message, StringComparer.Ordinal).ToArray();
        foreach (var group in groups.Take(10))
            Console.Error.WriteLine($"Gap [{group.Kind}]{(group.Count > 1 ? $" x{group.Count}" : "")}: {group.Message}"
                + (group.First is null ? "" : $" ({(group.Count > 1 ? "first at " : "")}{Display.Location(group.First)})"));
        if (groups.Length > 10) Console.Error.WriteLine($"Further gap kinds: {groups.Length - 10}; inspect --json lists every gap with its evidence.");
        Console.Error.WriteLine($"Extraction gaps: {gaps.Count} ({groups.Length} kind{(groups.Length == 1 ? "" : "s")}). Coverage: explicit origins/direct calls; minimal APIs and inherited actions outside scope.");
    }
}
