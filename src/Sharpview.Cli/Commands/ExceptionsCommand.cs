using System.Collections.Immutable;
using Sharpview.Core;

namespace Sharpview.Cli;

/// <summary>Default: every explicit throw in source. --uncaught/--caught/--miscaught: the fate of each throw along the
/// call chains from controller actions (or --root).</summary>
internal sealed class ExceptionsCommand : ReportCommand
{
    private static readonly string[] Views = ["--uncaught", "--caught", "--miscaught"];

    public override ImmutableHashSet<string> Options { get; } = ["--root", "--max-depth", "--max-paths", "--budget", "--locations", .. Views];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Given.Count(Views.Contains) > 1) throw new ArgumentException("Choose one of --uncaught, --caught or --miscaught.");
        var traced = arguments.Given.Overlaps(["--root", "--max-depth", "--max-paths", "--budget", "--locations"]);
        switch (arguments.View)
        {
            case ExceptionView.Throws when traced:
                throw new ArgumentException("Listing all throws takes no options; --root and limits require --uncaught, --caught or --miscaught.");
            case ExceptionView.Uncaught when arguments.Given.Overlaps(["--max-depth", "--max-paths"]):
                throw new ArgumentException("--uncaught is bounded by --budget only; --max-depth and --max-paths apply to --caught and --miscaught.");
            case ExceptionView.Uncaught: SummaryQuery(arguments).Validate(); break;
            case ExceptionView.Caught or ExceptionView.Miscaught: PathQuery(arguments).Validate(); break;
        }
    }

    private static ExceptionOptions PathQuery(Arguments arguments) => new(arguments.MaxDepth, arguments.MaxPaths, arguments.Budget);

    // One budget bounds both transfer work and retained facts, so a single knob controls summary cost.
    private static ExceptionSummaryOptions SummaryQuery(Arguments arguments) => new(arguments.Budget, arguments.Budget);

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        if (context.Arguments.View == ExceptionView.Throws) return ValueTask.FromResult(Throws(context, token));
        var roots = Roots.Select(context.Index, context.Arguments, RootScope.Actions, token);
        return ValueTask.FromResult(context.Arguments.View switch
        {
            ExceptionView.Uncaught => Uncaught(context, roots, token),
            ExceptionView.Caught => Handled(context, roots, caught: true, token),
            _ => Handled(context, roots, caught: false, token),
        });
    }

    private static bool Throws(ReportContext context, CancellationToken token)
    {
        var index = context.Index;
        var throws = context.Snapshot.ThrowSites
            .OrderBy(x => index.ProjectName(index.Callable(x.Owner).Compilation), StringComparer.Ordinal)
            .ThenBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart).ToArray();
        foreach (var origin in throws) { token.ThrowIfCancellationRequested(); context.Report.Throw(origin); }
        Console.Error.WriteLine($"Throws: {throws.Length} (explicit: {throws.Count(x => x.Kind == ThrowKind.Explicit)}; rethrows: {throws.Count(x => x.Kind == ThrowKind.Rethrow)}). Implicit exceptions (null dereference, framework calls) are not listed.");
        return false;
    }

    private static bool Handled(ReportContext context, ImmutableArray<CallableId> roots, bool caught, CancellationToken token)
    {
        var result = ExceptionQuery.Analyze(context.Index, roots, PathQuery(context.Arguments), token);
        var count = 0;
        if (caught)
            foreach (var item in ExceptionHandlingQuery.Caught(result)) { token.ThrowIfCancellationRequested(); context.Report.Caught(item); count++; }
        else
            foreach (var item in ExceptionHandlingQuery.Miscaught(result)) { token.ThrowIfCancellationRequested(); context.Report.Miscaught(item); count++; }
        var unknown = result.Results.Count(x => (x.SubsequentStatus ?? x.Status) == ExceptionStatus.Unknown);
        Console.Error.WriteLine($"Action roots: {roots.Length}; {(caught ? "caught" : "miscaught")}: {count}; witnesses: {result.Results.Length}; unknown: {unknown}; examined sites: {result.ExpandedSites}; depth limit: {result.DepthLimitReached}; output limit: {result.OutputLimitReached}; work limit: {result.WorkLimitReached}; global caveats: {result.GlobalGaps.Length}.");
        return !result.IsComplete;
    }

    private static bool Uncaught(ReportContext context, ImmutableArray<CallableId> roots, CancellationToken token)
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
