using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

/// <summary>Default: every explicit throw in source. --uncaught/--caught/--miscaught: the fate of each throw along the
/// call chains from controller actions (or --root).</summary>
internal sealed class ExceptionsCommand : ReportCommand
{
    private static readonly string[] Views = ["--uncaught", "--caught", "--miscaught"];
    private static readonly string[] TracedOptions = ["--root", "--budget", "--locations", "--declared-only"];

    public override ImmutableHashSet<string> Options { get; } = [.. TracedOptions, .. Views, "--strict"];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Given.Count(Views.Contains) > 1) throw new ArgumentException("Choose one of --uncaught, --caught or --miscaught.");
        if (arguments.Strict && arguments.View != ExceptionView.Uncaught) throw new ArgumentException("--strict requires --uncaught.");
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
    private static ExceptionSummaryOptions SummaryQuery(Arguments arguments) => new(arguments.Budget, arguments.Budget, !arguments.DeclaredOnly, arguments.Strict);

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

    // One line per explicit throw; rethrows are not new exceptions.
    private static bool Throws(ReportContext context, CancellationToken token)
    {
        var (index, display) = (context.Index, context.Display);
        var throws = context.Snapshot.ThrowSites.Where(x => x.Kind == ThrowKind.Explicit)
            .OrderBy(x => index.ProjectName(index.Callable(x.Owner).Compilation), StringComparer.Ordinal)
            .ThenBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart).ToArray();
        foreach (var origin in throws)
        {
            token.ThrowIfCancellationRequested();
            context.Output.WriteLine($"{Thrown(origin)}  {display.Short(origin.Owner)}  {display.Where(origin.Evidence)}");
        }
        Console.Error.WriteLine($"{Display.Plural(throws.Length, "throw")} in source.");
        return false;
    }

    private static bool Handled(ReportContext context, ImmutableArray<CallableId> roots, bool caught, CancellationToken token)
    {
        var (arguments, display) = (context.Arguments, context.Display);
        var result = ExceptionQuery.Analyze(context.Index, roots, PathQuery(arguments), token);
        var count = 0; var handled = 0;
        // A path that reaches a fake or test class only through an assumed implementation is a guess about what DI picks.
        bool Hidden(ThrowSiteFact origin, ExceptionWitness witness) =>
            witness.ViaCandidate && (display.IsTestDouble(origin.Owner) || witness.Path.Any(display.IsTestDouble));
        if (caught)
            foreach (var item in ExceptionHandlingQuery.Caught(result))
            {
                token.ThrowIfCancellationRequested();
                if (Hidden(context.Index.ThrowSite(item.Origin), item.Example)) continue;
                WriteCaught(context, item); count++;
            }
        else
        {
            var handlers = new GlobalHandlers(context.Index);
            // Stable: findings keep their order within each section.
            foreach (var item in ExceptionHandlingQuery.Miscaught(result).Where(x => !Hidden(context.Index.ThrowSite(x.Origin), x.Example))
                .OrderBy(x => handlers.Covers(x.EscapesFrom, x.Outcomes.Contains(ExceptionStatus.MayEscape))))
            {
                token.ThrowIfCancellationRequested();
                if (handlers.Covers(item.EscapesFrom, item.Outcomes.Contains(ExceptionStatus.MayEscape)) && handled++ == 0) handlers.WriteHeader(context, count > 0);
                WriteMiscaught(context, item); count++;
            }
        }
        Console.Error.WriteLine($"{Display.Plural(roots.Length, "action")} analysed: {count} {(caught ? "caught" : "miscaught")}{HandledCount(handled)}.");
        WriteNotFollowed(context, result.Results.Where(x => (x.SubsequentStatus ?? x.Status) == ExceptionStatus.Unknown)
            .Select(x => (x.ReasonCode, x.Evidence.LastOrDefault())));
        if (result.WorkLimitReached || result.OutputLimitReached || result.DepthLimitReached) WriteLimit(arguments);
        return !result.IsComplete;
    }

    // One block per exception that may leave an action: the throw (and the rethrow it passed through), the actions it
    // escapes from, and one example path. The same throw escaping many actions is listed once.
    private static bool Uncaught(ReportContext context, ImmutableArray<CallableId> roots, CancellationToken token)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var summaries = ExceptionSummaryQuery.Analyze(index, roots, SummaryQuery(context.Arguments), token);
        var handlers = new GlobalHandlers(index);
        var ordered = roots.OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal).ToArray();
        var escapes = ordered.SelectMany(root => summaries.Roots[root].Escapes.Select(escape => (Root: root, Escape: escape)))
            .Where(x => x.Escape.ExceptionType is not null && !summaries.ThirdPartyCalls.ContainsKey(x.Escape.Origin))
            .GroupBy(x => (x.Escape.Origin, x.Escape.SubsequentOrigin, x.Escape.ExceptionType, Handled: handlers.Covers([x.Root], true)))
            .OrderBy(x => x.Key.Handled)
            .ThenBy(x => index.ThrowSite(x.Key.Origin).Evidence.Path, StringComparer.Ordinal).ThenBy(x => index.ThrowSite(x.Key.Origin).Evidence.SpanStart)
            .ThenBy(x => x.Key.SubsequentOrigin?.Value, StringComparer.Ordinal).ToArray();
        var handled = 0;
        foreach (var group in escapes)
        {
            token.ThrowIfCancellationRequested();
            if (group.Key.Handled && handled++ == 0) handlers.WriteHeader(context, group != escapes[0]);
            var actions = group.Select(x => x.Root).Distinct().ToArray();
            // A proven path makes the better example than one through an assumed implementation.
            var example = group.OrderBy(x => x.Escape.ViaCandidate).First();
            output.WriteLine($"Uncaught: {group.Key.ExceptionType}");
            WriteOrigin(context, "Thrown", index.ThrowSite(group.Key.Origin));
            if (group.Key.SubsequentOrigin is { } rethrow && index.TryGetThrowSite(rethrow, out var site)) WriteOrigin(context, "Rethrown", site);
            output.WriteLine($"  Escapes:   {Actions(display, actions)}");
            var witness = summaries.GetWitness(example.Root, example.Escape);
            WritePath(context, witness.Path, witness.Sites, example.Escape.ViaCandidate, []);
        }
        var unwrapped = context.Arguments.Strict ? Unwrapped(context, summaries, ordered, separate: escapes.Length > 0, token) : 0;
        Console.Error.WriteLine($"{Display.Plural(roots.Length, "action")} analysed: {Display.Plural(escapes.Length, "uncaught exception")}{HandledCount(handled)}"
            + (context.Arguments.Strict ? $"; {Display.Plural(unwrapped, "third-party call")} with no catch on the way." : "."));
        WriteNotFollowed(context, roots.SelectMany(x => summaries.Roots[x].Unknowns)
            .Where(x => x.ReasonCode is not (ExceptionReason.WorkLimit or ExceptionReason.FactLimit))
            .Select(x => (x.ReasonCode, x.Site is { } s ? index.CallSite(s).Evidence : x.Evidence)));
        if (summaries.WorkLimitReached || summaries.FactLimitReached) WriteLimit(context.Arguments);
        return !summaries.IsComplete;
    }

    // --strict: one block per third-party call site that no catch meets on the way from an action. Global handlers do not
    // count: the rule asks for a catch around the call.
    private static int Unwrapped(ReportContext context, ExceptionSummaryResult summaries, CallableId[] roots, bool separate, CancellationToken token)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var calls = roots.SelectMany(root => summaries.Roots[root].Escapes.Where(x => summaries.ThirdPartyCalls.ContainsKey(x.Origin)).Select(escape => (Root: root, Escape: escape)))
            .GroupBy(x => x.Escape.Origin).Select(x => (Site: index.CallSite(summaries.ThirdPartyCalls[x.Key]), Items: x.ToArray()))
            .OrderBy(x => x.Site.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Site.Evidence.SpanStart).ToArray();
        if (calls.Length == 0) return 0;
        if (separate) output.WriteLine();
        output.WriteLine("Third-party calls with no catch on the way from the action (--strict):");
        foreach (var (site, items) in calls)
        {
            token.ThrowIfCancellationRequested();
            var actions = items.Select(x => x.Root).Distinct().ToArray();
            var example = items.OrderBy(x => x.Escape.ViaCandidate).First();
            output.WriteLine($"Unwrapped: {(site.BoundTarget is { } target ? display.Name(target) : site.TargetDisplay)}");
            output.WriteLine($"  Called:    {display.Short(site.Caller)}  {display.Where(site.Evidence)}");
            output.WriteLine($"  Escapes:   {Actions(display, actions)}");
            var witness = summaries.GetWitness(example.Root, example.Escape);
            WritePath(context, witness.Path, witness.Sites, example.Escape.ViaCandidate, []);
        }
        return calls.Length;
    }

    private static string Actions(Display display, CallableId[] actions) =>
        string.Join(", ", actions.Take(3).Select(display.Short)) + (actions.Length > 3 ? $" and {actions.Length - 3} more" : "");

    private static string HandledCount(int handled) => handled == 0 ? "" : $" ({handled} handled by a global exception handler)";

    // Exceptions that escape the action still reach exception middleware or a global exception filter registered by the
    // host; those findings are listed last, under one line naming the registrations.
    private sealed class GlobalHandlers(ProgramIndex index)
    {
        private readonly Dictionary<CallableId, ImmutableArray<BoundaryHandlingFact>> byRoot = [];
        private readonly SortedSet<BoundaryHandlingFact> used = new(Comparer<BoundaryHandlingFact>.Create((a, b) =>
            string.CompareOrdinal(a.Evidence.Path, b.Evidence.Path) is var c and not 0 ? c : a.Evidence.SpanStart.CompareTo(b.Evidence.SpanStart)));

        private ImmutableArray<BoundaryHandlingFact> Of(CallableId root) =>
            byRoot.TryGetValue(root, out var found) ? found : byRoot[root] = AspNetConventions.GlobalHandlers(index, root);

        // True when the exception may escape and every action it escapes from has a global handler.
        public bool Covers(IReadOnlyCollection<CallableId> roots, bool escapes)
        {
            if (!escapes || roots.Count == 0 || roots.Any(x => Of(x).IsEmpty)) return false;
            foreach (var root in roots) used.UnionWith(Of(root));
            return true;
        }

        public void WriteHeader(ReportContext context, bool separate)
        {
            if (separate) context.Output.WriteLine();
            context.Output.WriteLine($"Handled by a global exception handler: {string.Join(", ", used.Select(x => $"{x.Registration} ({context.Display.Where(x.Evidence)})"))}");
        }
    }

    // Where the analysis could not follow an exception, grouped by cause, one line in all.
    private static void WriteNotFollowed(ReportContext context, IEnumerable<(ExceptionReason Reason, SourceEvidence? Evidence)> unknowns)
    {
        // Under --declared-only an interface call stops by request, not for lack of an implementation.
        (string One, string Many)? Label(ExceptionReason reason) => reason == ExceptionReason.DispatchBoundary && context.Arguments.DeclaredOnly
            ? ("interface or virtual call not followed (--declared-only)", "interface or virtual calls not followed (--declared-only)") : Cause(reason);
        var groups = unknowns.Where(x => Label(x.Reason) is not null)
            .GroupBy(x => x.Reason)
            .Select(g => (Reason: g.Key, Places: g.Select(x => x.Evidence).DistinctBy(x => x is null ? "" : $"{x.Path}:{x.SpanStart}").ToArray()))
            .OrderByDescending(x => x.Places.Length).ThenBy(x => x.Reason).ToArray();
        if (groups.Length == 0) return;
        Console.Error.WriteLine("Not followed: " + string.Join("; ", groups.Select(g =>
            $"{Display.Plural(g.Places.Length, Label(g.Reason)!.Value.One, Label(g.Reason)!.Value.Many)}"
            + (g.Places.OrderBy(x => x?.Path, StringComparer.Ordinal).ThenBy(x => x?.SpanStart).FirstOrDefault() is { } first ? $" (e.g. {context.Display.Where(first)})" : ""))) + ".");
    }

    // Plain-words causes; limits are reported separately and the rest cannot occur after the handler rules above.
    private static (string One, string Many)? Cause(ExceptionReason reason) => reason switch
    {
        ExceptionReason.UnobservedTask => ("task that is never awaited", "tasks that are never awaited"),
        ExceptionReason.AsyncUnsupported => ("async void method", "async void methods"),
        ExceptionReason.FinallyUnsupported => ("finally block that may throw", "finally blocks that may throw"),
        ExceptionReason.MissingBody => ("method without a body", "methods without a body"),
        ExceptionReason.DispatchBoundary => ("interface or virtual call without a source implementation", "interface or virtual calls without a source implementation"),
        ExceptionReason.UnresolvedTarget => ("call that does not compile", "calls that do not compile"),
        ExceptionReason.Cycle => ("recursive call", "recursive calls"),
        ExceptionReason.UnboundExceptionType => ("exception whose type does not compile", "exceptions whose type does not compile"),
        ExceptionReason.ExtractionGap => ("construct SharpView does not analyse", "constructs SharpView does not analyse"),
        ExceptionReason.HandlerFlowUnsupported => ("catch block with unclear flow", "catch blocks with unclear flow"),
        _ => null,
    };

    private static void WriteLimit(Arguments arguments) =>
        Console.Error.WriteLine($"Stopped early: the analysis budget ran out, so the list above is incomplete. Rerun with --budget {arguments.Budget * 10}.");

    private static void WriteCaught(ReportContext context, CaughtException caught)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var origin = index.ThrowSite(caught.Origin);
        var handler = index.Catch(caught.Catch);
        output.WriteLine($"Caught: {Thrown(origin)}");
        WriteOrigin(context, "Thrown", origin);
        output.WriteLine($"  Caught by: catch ({handler.CaughtType?.DisplayName ?? "all exceptions"}){(handler.Filter is null ? "" : " when (...)")} in {display.Short(handler.Owner)}  {display.Where(handler.Evidence)}");
        output.WriteLine($"  Handler:   {Outcome(handler.Outcome)}{(caught.Certainty == CatchCertainty.Possible ? handler.Filter is null ? " (only if the runtime type matches)" : " (only if the filter accepts it)" : "")}");
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
        output.WriteLine($"  Missed by: try in {display.Short(region.Owner)} (catches only {string.Join(", ", clauses)})  {display.Where(region.ProtectedBlock)}");
        output.WriteLine($"  Then:      {string.Join("; ", miscaught.Outcomes.Select(x => Fate(context, x, miscaught.Undetermined)))}");
        WriteExample(context, miscaught.Example);
    }

    private static void WriteOrigin(ReportContext context, string label, ThrowSiteFact origin) =>
        context.Output.WriteLine($"  {label + ":",-10} {context.Display.Short(origin.Owner)}  {context.Display.Where(origin.Evidence)}");

    private static void WriteExample(ReportContext context, ExceptionWitness witness) =>
        WritePath(context, witness.Path, witness.Sites, witness.ViaCandidate, witness.Evidence);

    // One call per line, from the action down to the throw. A step through an interface/virtual call assumes which
    // implementation runs, so a path with such a step is possible, not proven.
    private static void WritePath(ReportContext context, ImmutableArray<CallableId> path, ImmutableArray<CallSiteId> sites, bool assumed, ImmutableArray<SourceEvidence> evidence)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        output.WriteLine(assumed ? "  Path (possible, not proven):" : "  Path:");
        for (var i = 0; i < path.Length; i++)
        {
            var candidate = i > 0 && i - 1 < sites.Length && index.CallSite(sites[i - 1]).Dispatch != DispatchKind.Direct;
            output.WriteLine($"    {(i == 0 ? "  " : "->")} {display.Short(path[i])}{(candidate ? "  (assumed implementation)" : "")}");
        }
        if (!context.Arguments.Locations) return;
        foreach (var item in evidence.IsEmpty ? sites.Select(x => index.CallSite(x).Evidence) : evidence) output.WriteLine($"    at {display.Where(item)}");
    }

    private static string Thrown(ThrowSiteFact origin) => (origin.ExactExceptionType ?? origin.StaticExceptionType)?.DisplayName is { } type
        ? type + (origin.IsExactType || origin.ExactExceptionType is not null ? "" : " (or a subclass)")
        : "exception of unknown type";

    private static string Outcome(HandlerOutcome outcome) => outcome switch
    {
        HandlerOutcome.Intercepts => "handles it",
        HandlerOutcome.Rethrows => "rethrows it",
        HandlerOutcome.MayRethrow => "handles it in some cases, rethrows it in others",
        HandlerOutcome.Replaces => "throws a different exception instead",
        _ => "outcome not determined",
    };

    // What happens to a miscaught exception after the try it slipped through.
    private static string Fate(ReportContext context, ExceptionStatus status, ExceptionWitness? undetermined) => status switch
    {
        ExceptionStatus.Intercepted => "caught further up",
        ExceptionStatus.RethrownOrReplaced => "rethrown further up",
        ExceptionStatus.MayEscape => "may escape the controller action",
        _ => "not determined" + (undetermined is not null && Cause(undetermined.ReasonCode) is { } cause
            ? $": {cause.One}" + (undetermined.Evidence.LastOrDefault() is { } at ? $" at {context.Display.Where(at)}" : "") : ""),
    };
}
