using System.Collections.Immutable;
using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class LayersCommand : ReportCommand
{
    public override ImmutableHashSet<string> Options { get; } = ["--layer", "--allow"];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Layers.IsEmpty) throw new ArgumentException("layers requires at least one --layer mapping.");
    }

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var (report, arguments) = (context.Report, context.Arguments);
        var graph = LayerGraph.Build(context.Index, report.Layers, token);
        var findings = LayerRules.AllowedDependencies(graph, report.Layers.Model);
        report.LayerOverview(graph, findings);
        foreach (var name in arguments.Allowed.SelectMany(x => new[] { x.From, x.To }).Distinct().Where(x => !report.Layers.Model.Layers.Contains(x)))
            Console.Error.WriteLine($"Warning: --allow names layer '{name}', which no --layer mapping defines.");
        var violations = findings.Count(x => x.Verdict == Verdict.Violation);
        var undecided = findings.Count(x => x.Verdict == Verdict.Unknown);
        Console.Error.WriteLine($"Layers: {string.Join(", ", report.Layers.Model.Layers)}; edges: {graph.Edges.Length}; external references: {graph.ExternalReferences}; unresolved calls: {graph.UnresolvedCalls}; rule evaluated: {!arguments.Allowed.IsEmpty}; violations: {violations}; unknown: {undecided}.");
        return ValueTask.FromResult(violations > 0 || undecided > 0);
    }
}
