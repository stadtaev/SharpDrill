using System.Collections.Immutable;
using Sharpview.Core;

namespace Sharpview.Cli;

/// <summary>Default: bottom-up summaries (one witness per escaping origin). --paths: every propagation path and its handlers.</summary>
internal sealed class ExceptionsCommand : ReportCommand
{
    public override ImmutableHashSet<string> Options { get; } = ["--root", "--paths", "--max-depth", "--max-paths", "--budget", "--locations"];

    public override void Validate(Arguments arguments)
    {
        if (!arguments.Paths && arguments.Given.Overlaps(["--max-depth", "--max-paths"]))
            throw new ArgumentException("--max-depth and --max-paths require exceptions --paths; summaries are bounded by --budget only.");
        if (arguments.Paths) PathQuery(arguments).Validate(); else SummaryQuery(arguments).Validate();
    }

    private static ExceptionOptions PathQuery(Arguments arguments) => new(arguments.MaxDepth, arguments.MaxPaths, arguments.Budget);

    // One budget bounds both transfer work and retained facts, so a single knob controls summary cost.
    private static ExceptionSummaryOptions SummaryQuery(Arguments arguments) => new(arguments.Budget, arguments.Budget);

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var roots = Roots.Select(context.Index, context.Arguments, RootScope.Actions, token);
        return ValueTask.FromResult(context.Arguments.Paths ? Detailed(context, roots, token) : Summary(context, roots, token));
    }

    private static bool Detailed(ReportContext context, ImmutableArray<CallableId> roots, CancellationToken token)
    {
        context.Report.Landscape(token);
        var exceptions = ExceptionQuery.Analyze(context.Index, roots, PathQuery(context.Arguments), token);
        foreach (var witness in exceptions.Results) { token.ThrowIfCancellationRequested(); context.Report.Witness(witness); }
        var unknown = exceptions.Results.Count(x => x.Status == ExceptionStatus.Unknown || x.SubsequentStatus == ExceptionStatus.Unknown);
        Console.Error.WriteLine($"Action roots: {roots.Length}; witnesses: {exceptions.Results.Length}; unknown: {unknown}; examined sites: {exceptions.ExpandedSites}; depth limit: {exceptions.DepthLimitReached}; output limit: {exceptions.OutputLimitReached}; work limit: {exceptions.WorkLimitReached}; global caveats: {exceptions.GlobalGaps.Length}.");
        return !exceptions.IsComplete;
    }

    private static bool Summary(ReportContext context, ImmutableArray<CallableId> roots, CancellationToken token)
    {
        var (index, arguments, output, report) = (context.Index, context.Arguments, context.Output, context.Report);
        var summaries = ExceptionSummaryQuery.Analyze(index, roots, SummaryQuery(arguments), token);
        foreach (var root in roots.OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var summary = summaries.Roots[root];
            output.WriteLine($"Exception summary: {report.Name(root)}");
            output.WriteLine($"  Explicit escapes: {summary.Escapes.Length}; unknown effects: {summary.Unknowns.Length}; local summary complete within model: {summary.IsComplete}.");
            foreach (var escape in summary.Escapes)
            {
                output.WriteLine($"  May escape: {escape.ExceptionType ?? "unknown type"} | origin: {escape.Origin.Value} | {escape.ReasonCode}");
                var witness = summaries.GetWitness(root, escape);
                output.WriteLine($"    Path: {string.Join(" --> ", witness.Path.Select(report.Name))}");
                if (index.TryGetThrowSite(escape.Origin, out var origin)) output.WriteLine($"    Origin evidence: {TextReport.Location(origin.Evidence)}");
                if (escape.SubsequentOrigin is { } subsequent && index.TryGetThrowSite(subsequent, out var replacement))
                    output.WriteLine($"    Subsequent origin: {subsequent.Value} | {TextReport.Location(replacement.Evidence)}");
                if (arguments.Locations) foreach (var site in witness.Sites) output.WriteLine($"    Call-site evidence: {TextReport.Location(index.CallSite(site).Evidence)}");
            }
            foreach (var uncertainty in summary.Unknowns)
                output.WriteLine($"  Unknown [{uncertainty.ReasonCode}]: {uncertainty.Message}" + (uncertainty switch
                {
                    { Site: { } site } => $" | {TextReport.Location(index.CallSite(site).Evidence)}",
                    { Evidence: { } evidence } => $" | {TextReport.Location(evidence)}",
                    _ => "",
                }));
            foreach (var boundary in context.Snapshot.BoundaryHandling.Where(x => x.Compilation == index.Callable(root).Compilation))
                output.WriteLine($"  Boundary registration evidence (runtime coverage unknown): {boundary.Registration} | {TextReport.Location(boundary.Evidence)}");
        }
        Console.Error.WriteLine($"Summary roots: {summaries.Roots.Count}; methods: {summaries.Methods.Count}; facts: {summaries.FactsAdded}; transfer work: {summaries.WorkItemsProcessed}; work limit: {summaries.WorkLimitReached}; fact limit: {summaries.FactLimitReached}; global caveats: {summaries.GlobalGaps.Length}.");
        return !summaries.IsComplete;
    }
}
