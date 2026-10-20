using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

/// <summary>Default: every explicit throw in source. --uncaught/--caught/--miscaught: the fate of each throw along the
/// call chains from controller actions (or --root).</summary>
internal sealed class ExceptionsCommand : ReportCommand
{
    private static readonly string[] Views = ["--uncaught", "--caught", "--miscaught"];
    private static readonly string[] TracedOptions = ["--root", "--budget", "--locations", "--declared-only"];

    public override ImmutableHashSet<string> Options { get; } = [.. TracedOptions, .. Views];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Given.Count(Views.Contains) > 1) throw new ArgumentException("Choose one of --uncaught, --caught or --miscaught.");
        switch (arguments.View)
        {
            case ExceptionView.Throws when arguments.Given.Overlaps(TracedOptions):
                throw new ArgumentException("Listing all throws takes no options; --root, --budget, --locations and --declared-only require --uncaught, --caught or --miscaught.");
            case ExceptionView.Uncaught: SummaryQuery(arguments).Validate(); break;
            case ExceptionView.Caught or ExceptionView.Miscaught: PathQuery(arguments).Validate(); break;
        }
    }

    // One knob: --budget bounds the work; depth is generous (cycles are detected separately) and the number of reported
    // witnesses scales with the budget, so no output limit is hit before the work limit.
    private const int Depth = 100;
    private static ExceptionOptions PathQuery(Arguments arguments) => new(Depth, Math.Max(1000, arguments.Budget / 100), arguments.Budget, !arguments.DeclaredOnly, DistinctOutcomes: true);

    // The same budget bounds both transfer work and retained facts.
    private static ExceptionSummaryOptions SummaryQuery(Arguments arguments) => new(arguments.Budget, arguments.Budget, !arguments.DeclaredOnly);

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
        foreach (var origin in throws) { token.ThrowIfCancellationRequested(); WriteThrow(context, origin); }
        Console.Error.WriteLine($"Throws: {throws.Length} (explicit: {throws.Count(x => x.Kind == ThrowKind.Explicit)}; rethrows: {throws.Count(x => x.Kind == ThrowKind.Rethrow)}). Implicit exceptions (null dereference, framework calls) are not listed.");
        return false;
    }

    private static bool Handled(ReportContext context, ImmutableArray<CallableId> roots, bool caught, CancellationToken token)
    {
        var (arguments, display) = (context.Arguments, context.Display);
        var result = ExceptionQuery.Analyze(context.Index, roots, PathQuery(arguments), token);
        var count = 0;
        if (caught)
            foreach (var item in ExceptionHandlingQuery.Caught(result)) { token.ThrowIfCancellationRequested(); WriteCaught(context, item); count++; }
        else
            foreach (var item in ExceptionHandlingQuery.Miscaught(result)) { token.ThrowIfCancellationRequested(); WriteMiscaught(context, item); count++; }
        var unknown = result.Results.Count(x => (x.SubsequentStatus ?? x.Status) == ExceptionStatus.Unknown);
        Console.Error.WriteLine($"Action roots: {roots.Length}; {(caught ? "caught" : "miscaught")}: {count}; witnesses: {result.Results.Length}; unknown: {unknown}; library methods called: {result.LibraryCalls.Length}; examined sites: {result.ExpandedSites}; depth limit: {result.DepthLimitReached}; output limit: {result.OutputLimitReached}; work limit: {result.WorkLimitReached}; global caveats: {result.GlobalGaps.Length}.");
        if (result.OutputLimitReached)
            Console.Error.WriteLine($"Stopped after {PathQuery(arguments).MaxResults} origin witnesses: the list above is incomplete. Raise --budget to see more.");
        if (result.UnknownLimitReached)
            Console.Error.WriteLine("Some uncertainty witnesses were dropped (limit reached); they do not affect the origins listed above.");
        var unfollowed = result.Results.Where(x => x.ReasonCode == ExceptionReason.DispatchBoundary && x.Reason.Contains("declared binding only", StringComparison.Ordinal))
            .Select(x => x.Evidence.FirstOrDefault() is { } e ? $"{e.Path}:{e.SpanStart}" : "").Distinct().Count();
        if (unfollowed > 0)
            Console.Error.WriteLine($"{unfollowed} interface/virtual call sites were not followed (--declared-only), so exceptions thrown behind them are not listed. Rerun without --declared-only to continue through their source implementations.");
        return !result.IsComplete;
    }

    private static bool Uncaught(ReportContext context, ImmutableArray<CallableId> roots, CancellationToken token)
    {
        var (index, arguments, output, display) = (context.Index, context.Arguments, context.Output, context.Display);
        var summaries = ExceptionSummaryQuery.Analyze(index, roots, SummaryQuery(arguments), token);
        var unfollowed = new HashSet<CallSiteId>();
        var library = new HashSet<CallableId>();
        foreach (var root in roots.OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var summary = summaries.Roots[root];
            var lines = new List<string>();
            foreach (var uncertainty in summary.Unknowns)
            {
                lines.Add($"  Unknown [{uncertainty.ReasonCode}]: {uncertainty.Message}" + (uncertainty switch
                {
                    { Site: { } site } => $" | {Display.Location(index.CallSite(site).Evidence)}",
                    { Evidence: { } evidence } => $" | {Display.Location(evidence)}",
                    _ => "",
                }));
                if (uncertainty is { ReasonCode: ExceptionReason.DispatchBoundary, Site: { } dispatchSite } && arguments.DeclaredOnly) unfollowed.Add(dispatchSite);
            }
            // The same site can be reached several ways; identical lines are shown once with a count.
            var grouped = lines.GroupBy(x => x).Select(x => (Line: x.Key, Count: x.Count())).ToArray();
            output.WriteLine($"Exception summary: {display.Name(root)}");
            output.WriteLine($"  Explicit escapes: {summary.Escapes.Length}; unknown effects: {grouped.Length}; local summary complete within model: {summary.IsComplete}.");
            foreach (var escape in summary.Escapes)
            {
                output.WriteLine($"  May escape: {escape.ExceptionType ?? "unknown type"} | origin: {escape.Origin.Value} | {escape.ReasonCode}{(escape.ViaCandidate ? " | via an implementation candidate (possible, not proven)" : "")}");
                var witness = summaries.GetWitness(root, escape);
                output.WriteLine($"    Path: {PathText(context, witness.Path, witness.Sites)}");
                if (index.TryGetThrowSite(escape.Origin, out var origin)) output.WriteLine($"    Origin evidence: {Display.Location(origin.Evidence)}");
                if (escape.SubsequentOrigin is { } subsequent && index.TryGetThrowSite(subsequent, out var replacement))
                    output.WriteLine($"    Subsequent origin: {subsequent.Value} | {Display.Location(replacement.Evidence)}");
                if (arguments.Locations) foreach (var site in witness.Sites) output.WriteLine($"    Call-site evidence: {Display.Location(index.CallSite(site).Evidence)}");
            }
            foreach (var (line, count) in grouped) output.WriteLine(count > 1 ? $"{line} (x{count})" : line);
            var libraryCalls = summaries.LibraryCallsFrom(root);
            library.UnionWith(libraryCalls);
            if (libraryCalls.Length > 0)
            {
                var names = libraryCalls.Select(display.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                output.WriteLine($"  Library calls not analysed: {names.Length} method{(names.Length == 1 ? "" : "s")} (e.g. {string.Join(", ", names.Take(3))}{(names.Length > 3 ? ", ..." : "")}); exceptions they may throw are not modelled.");
            }
            foreach (var boundary in context.Snapshot.BoundaryHandling.Where(x => x.Compilation == index.Callable(root).Compilation))
                output.WriteLine($"  Boundary registration evidence (runtime coverage unknown): {boundary.Registration} | {Display.Location(boundary.Evidence)}");
        }
        Console.Error.WriteLine($"Summary roots: {summaries.Roots.Count}; methods: {summaries.Methods.Count}; facts: {summaries.FactsAdded}; transfer work: {summaries.WorkItemsProcessed}; work limit: {summaries.WorkLimitReached}; fact limit: {summaries.FactLimitReached}; library methods called: {library.Count}; global caveats: {summaries.GlobalGaps.Length}.");
        if (unfollowed.Count > 0)
            Console.Error.WriteLine($"{unfollowed.Count} interface/virtual call sites were not followed (--declared-only), so exceptions thrown behind them are not listed. Rerun without --declared-only to continue through their source implementations.");
        return !summaries.IsComplete;
    }

    // The path from the action to the origin; steps through an implementation candidate are marked.
    private static string PathText(ReportContext context, ImmutableArray<CallableId> path, ImmutableArray<CallSiteId> sites)
    {
        var text = context.Display.Name(path[0]);
        for (var i = 1; i < path.Length; i++)
        {
            var candidate = i - 1 < sites.Length && context.Index.CallSite(sites[i - 1]).Dispatch != DispatchKind.Direct;
            text += (candidate ? " -[candidate]-> " : " --> ") + context.Display.Name(path[i]);
        }
        return text;
    }

    private static void WriteThrow(ReportContext context, ThrowSiteFact origin)
    {
        context.Output.WriteLine($"Throw: {Thrown(origin)}");
        WriteOrigin(context, "At", origin);
        context.Output.WriteLine($"  Inside try in the same method: {(origin.ProtectedRegions.IsEmpty ? "no" : "yes")}");
    }

    private static void WriteCaught(ReportContext context, CaughtException caught)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var origin = index.ThrowSite(caught.Origin);
        var handler = index.Catch(caught.Catch);
        output.WriteLine($"Caught: {Thrown(origin)}");
        WriteOrigin(context, "Thrown", origin);
        output.WriteLine($"  Caught: {display.Name(handler.Owner)} | catch ({handler.CaughtType?.DisplayName ?? "all exceptions"}){(handler.Filter is null ? "" : " when (...)")} | Project: {display.Project(handler.Owner)} | {Display.Location(handler.Evidence)}");
        output.WriteLine($"  Handler: {Outcome(handler.Outcome)}{caught.Certainty switch
        {
            CatchCertainty.TypeUncertain => " | match uncertain: the runtime type may not be caught",
            CatchCertainty.Filtered => " | filter decides at runtime",
            _ => "",
        }}");
        WriteExample(context, caught.Example);
    }

    private static void WriteMiscaught(ReportContext context, MiscaughtException miscaught)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var origin = index.ThrowSite(miscaught.Origin);
        var region = index.Region(miscaught.Missed.Region);
        var clauses = region.Catches.Select(x => index.Catch(x).CaughtType?.DisplayName ?? "all exceptions");
        output.WriteLine($"Miscaught: {miscaught.Missed.ExceptionType ?? Thrown(origin)}");
        WriteOrigin(context, "Thrown", origin);
        output.WriteLine($"  Passed through: try in {display.Name(region.Owner)} | Project: {display.Project(region.Owner)} | {Display.Location(region.ProtectedBlock)}");
        output.WriteLine($"  Catch clauses (none match): {string.Join(", ", clauses)}");
        output.WriteLine($"  Then: {string.Join("; ", miscaught.Outcomes.Select(Fate))}");
        WriteExample(context, miscaught.Example);
    }

    // Where an exception is raised: method, declaring type, project and source location.
    private static void WriteOrigin(ReportContext context, string label, ThrowSiteFact origin) =>
        context.Output.WriteLine($"  {label}: {context.Display.Name(origin.Owner)} | Type: {context.Display.TypeOf(origin.Owner)} | Project: {context.Display.Project(origin.Owner)} | {Display.Location(origin.Evidence)}");

    private static void WriteExample(ReportContext context, ExceptionWitness witness)
    {
        context.Output.WriteLine($"  Example path: {PathText(context, witness.Path, witness.Sites)}{(witness.ViaCandidate ? "   (via an implementation candidate: possible, not proven)" : "")}");
        if (context.Arguments.Locations) foreach (var evidence in witness.Evidence) context.Output.WriteLine($"    Evidence: {Display.Location(evidence)}");
    }

    private static string Thrown(ThrowSiteFact origin) => origin.Kind == ThrowKind.Rethrow ? "rethrow of the caught exception"
        : (origin.ExactExceptionType ?? origin.StaticExceptionType)?.DisplayName is { } type
            ? type + (origin.IsExactType || origin.ExactExceptionType is not null ? "" : " (static type; runtime type may differ)")
            : "unknown type";

    private static string Outcome(HandlerOutcome outcome) => outcome switch
    {
        HandlerOutcome.Intercepts => "handles it (recovery not proven)",
        HandlerOutcome.Rethrows => "rethrows it",
        HandlerOutcome.Replaces => "throws a replacement",
        _ => "unsupported handler flow; outcome unknown",
    };

    // What happens to a miscaught exception after the try it slipped through.
    private static string Fate(ExceptionStatus status) => status switch
    {
        ExceptionStatus.Intercepted => "caught further up",
        ExceptionStatus.RethrownOrReplaced => "rethrown or replaced further up",
        ExceptionStatus.MayEscape => "may escape the controller action",
        _ => "unknown",
    };
}
