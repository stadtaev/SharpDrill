using System.Collections.Immutable;
using SharpDrill.Core;

namespace SharpDrill.Cli;

/// <summary>Default: every explicit throw in source. --uncaught/--caught/--miscaught: the fate of each throw along the
/// call chains from controller actions (or --root).</summary>
internal sealed partial class ExceptionsCommand : ReportCommand
{
    private static readonly string[] Views = ["--uncaught", "--caught", "--miscaught"];
    private static readonly string[] TracedOptions = ["--root", "--budget", "--locations", "--declared-only"];

    public override ImmutableHashSet<string> Options { get; } = [.. TracedOptions, .. Views, "--strict", "--verbose", "--format"];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Given.Count(Views.Contains) > 1) throw new ArgumentException("Choose one of --uncaught, --caught or --miscaught.");
        if (arguments.Verbose && !arguments.Strict) throw new ArgumentException("--verbose requires --strict.");
        if (arguments.MSBuild && arguments.View == ExceptionView.Caught) throw new ArgumentException("--format msbuild reports uncaught and miscaught exceptions; caught ones are not warnings.");
        if (arguments.MSBuild && arguments.Locations) throw new ArgumentException("--format msbuild prints one line per finding; --locations does not apply.");
        if (arguments.Strict && arguments.View != ExceptionView.Uncaught && !(arguments.MSBuild && arguments.View == ExceptionView.Throws)) throw new ArgumentException("--strict requires --uncaught.");
        if (arguments.View == ExceptionView.Throws && !arguments.MSBuild && arguments.Given.Overlaps(TracedOptions))
            throw new ArgumentException("Listing all throws takes no options; --root, --budget, --locations and --declared-only require --uncaught, --caught or --miscaught.");
        AnalysisOptions(arguments).Validate();
    }

    // Every view reads one propagation. The same budget bounds both transfer work and retained facts.
    private static ExceptionSummaryOptions AnalysisOptions(Arguments arguments) => new(arguments.Budget, arguments.Budget, !arguments.DeclaredOnly, arguments.Strict, arguments.Verbose);

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var roots = context.Arguments.MSBuild || context.Arguments.View != ExceptionView.Throws ? Roots.Select(context.Index, context.Arguments, token) : [];
        if (context.Arguments.View == ExceptionView.Throws && !context.Arguments.MSBuild) return ValueTask.FromResult(Throws(context, token));
        var summaries = ExceptionSummaryQuery.Analyze(context.Index, roots, AnalysisOptions(context.Arguments), token);
        string[] counts = context.Arguments.View switch
        {
            // Build warnings default to every finding worth one: uncaught and miscaught.
            ExceptionView.Throws => [Uncaught(context, summaries, roots, token), Handled(context, summaries, caught: false, token)],
            ExceptionView.Uncaught => [Uncaught(context, summaries, roots, token)],
            ExceptionView.Caught => [Handled(context, summaries, caught: true, token)],
            _ => [Handled(context, summaries, caught: false, token)],
        };
        // One summary for the run, however many views it printed, so a CI log has one line to read.
        // Without actions a zero count would read as a clean result, so that case says nothing was traced.
        Console.Error.WriteLine(roots.IsEmpty ? "No controller actions found." : string.Join("; ", counts) + ".");
        WriteNotFollowed(context, roots.SelectMany(x => summaries.Roots[x].Unknowns)
            .Where(x => x.ReasonCode is not (ExceptionReason.WorkLimit or ExceptionReason.FactLimit))
            .Select(x => (x.ReasonCode, x.Site is { } s ? context.Index.CallSite(s).Evidence : x.Evidence)));
        if (summaries.WorkLimitReached || summaries.FactLimitReached) WriteLimit(context.Arguments);
        if (suppressed > 0) Console.Error.WriteLine($"{Display.Plural(suppressed, "warning")} suppressed in source (#pragma warning disable or [SuppressMessage]).");
        return ValueTask.FromResult(!summaries.IsComplete);
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

    private static string Handled(ReportContext context, ExceptionSummaryResult summaries, bool caught, CancellationToken token)
    {
        var (arguments, display) = (context.Arguments, context.Display);
        var (count, handled, report) = (0, 0, new Report(context.Output));
        if (caught)
        {
            var items = ExceptionHandlingQuery.Caught(context.Index, summaries, token).Where(x => !Hidden(display, context.Index.ThrowSite(x.Origin), x.Example)).ToArray();
            report.Overview(Display.Plural(items.Length, "caught exception"), [.. items.Select(x => Thrown(context.Index.ThrowSite(x.Origin)))]);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                WriteCaught(context, report, item); count++;
            }
        }
        else
        {
            var handlers = new GlobalHandlers(context.Index);
            // Stable: findings keep their order within each section.
            var items = ExceptionHandlingQuery.Miscaught(context.Index, summaries, token).Where(x => !Hidden(display, context.Index.ThrowSite(x.Origin), x.Example))
                .Select(x => (Item: x, Handled: handlers.Covers(x.EscapesFrom, x.Outcomes.Contains(ExceptionStatus.MayEscape)))).OrderBy(x => x.Handled).ToArray();
            if (!arguments.MSBuild)
                report.Overview(Display.Plural(items.Length, "miscaught exception") + HandledCount(items.Count(x => x.Handled)),
                    [.. items.Select(x => x.Item.Missed.ExceptionType ?? Thrown(context.Index.ThrowSite(x.Item.Origin)))]);
            foreach (var (item, covered) in items)
            {
                token.ThrowIfCancellationRequested();
                if (covered) handled++;
                if (arguments.MSBuild) WarnMiscaught(context, item, handlers, covered); else WriteMiscaught(context, report, item, handlers, covered);
                count++;
            }
        }
        return $"{count} {(caught ? "caught" : "miscaught")}{HandledCount(handled)}";
    }

    // One block per exception that may leave an action: the throw (and the rethrow it passed through), the actions it
    // escapes from, and one example path. The same throw escaping many actions is listed once, and so are throws of one
    // type in one method (build warnings keep one per throw, since each points at its throw).
    private static string Uncaught(ReportContext context, ExceptionSummaryResult summaries, ImmutableArray<CallableId> roots, CancellationToken token)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var handlers = new GlobalHandlers(index);
        var ordered = roots.OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal).ToArray();
        // What leaves an async void action is a crash, listed with the others below.
        var escapes = ordered.Where(x => !index.Callable(x).IsAsyncVoid).SelectMany(root => summaries.Roots[root].Escapes.Select(escape => (Root: root, Escape: escape)))
            .Where(x => x.Escape.ExceptionType is not null && !summaries.ThirdPartyCalls.ContainsKey(x.Escape.Origin))
            .GroupBy(x => (Thrower: context.Arguments.MSBuild ? x.Escape.Origin.Value : index.ThrowSite(x.Escape.Origin).Owner.Value, x.Escape.SubsequentOrigin, x.Escape.ExceptionType, Handled: handlers.Covers([x.Root], true)))
            .Select(x => (x.Key, Origins: x.Select(e => index.ThrowSite(e.Escape.Origin)).Distinct().OrderBy(e => e.Evidence.Path, StringComparer.Ordinal).ThenBy(e => e.Evidence.SpanStart).ToArray(), Items: x.ToArray()))
            .OrderBy(x => x.Key.Handled)
            .ThenBy(x => x.Origins[0].Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Origins[0].Evidence.SpanStart)
            .ThenBy(x => x.Key.SubsequentOrigin?.Value, StringComparer.Ordinal).ToArray();
        var (handled, report) = (0, new Report(output));
        var unwrapped = context.Arguments.Strict ? UnwrappedCalls(index, summaries, ordered) : [];
        var crashes = ExceptionHandlingQuery.Crashing(index, summaries, token).Where(x => !Hidden(display, index.ThrowSite(x.Origin), x.Example)).ToArray();
        if (!context.Arguments.MSBuild)
            report.Overview(Display.Plural(escapes.Length, "uncaught exception") + HandledCount(escapes.Count(x => x.Key.Handled)) + CrashCount(crashes.Length)
                + (unwrapped.Length > 0 ? $"; {Display.Plural(unwrapped.Length, "third-party call")} with no catch on the way (--strict)" : ""),
                [.. crashes.Select(x => x.ExceptionType ?? "exception of unknown type"), .. escapes.Select(x => x.Key.ExceptionType!)], unwrapped.Length);
        foreach (var crash in crashes) { token.ThrowIfCancellationRequested(); WriteCrash(context, report, crash); }
        foreach (var group in escapes)
        {
            token.ThrowIfCancellationRequested();
            if (group.Key.Handled) handled++;
            var actions = group.Items.Select(x => x.Root).Distinct().ToArray();
            // A proven path makes the better example than one through an assumed implementation; then the shortest.
            var example = group.Items.OrderBy(x => x.Escape.ViaCandidate).ThenBy(x => summaries.GetWitness(x.Root, x.Escape).Path.Length).First();
            if (context.Arguments.MSBuild)
            {
                var rethrown = group.Key.SubsequentOrigin is { } at && index.TryGetThrowSite(at, out var again) ? $", rethrown in {display.Short(again.Owner)} ({FileLine(again.Evidence)})" : "";
                var to = group.Key.Handled ? $" to the global exception handler {handlers.Names(actions)}" : "";
                Warn(context, group.Key.Handled ? "SD0002" : "SD0001", group.Origins[0].Evidence,
                    $"{group.Key.ExceptionType} may escape {Actions(display, actions)}{to}{rethrown}; {Chain(context, summaries.GetWitness(example.Root, example.Escape).Path, example.Escape.ViaCandidate)}");
                continue;
            }
            var witness = summaries.GetWitness(example.Root, example.Escape);
            report.Begin(group.Key.ExceptionType!);
            WriteOrigin(context, report, "Thrown", group.Origins, witness.Path);
            if (group.Key.SubsequentOrigin is { } rethrow && index.TryGetThrowSite(rethrow, out var site)) WriteOrigin(context, report, "Rethrown", [site], []);
            // The path starts at the action, so a single action is already named there.
            if (actions is not [var only] || witness.Path.FirstOrDefault() != only) report.List("Escapes", actions.Select(display.Short));
            if (group.Key.Handled) report.Line("Handled", handlers.Describe(context, actions));
            WritePath(context, report, witness.Path, witness.Sites, []);
        }
        if (context.Arguments.Strict) WriteUnwrapped(context, report, summaries, unwrapped, token);
        // The claim is narrower when calls were left out, so it says so; a program without such assemblies has nothing to add.
        var leftOut = !context.Arguments.Verbose && index.Snapshot.ThirdPartyAssemblies.Any(ExceptionSummaryOptions.IsRoutineThirdParty)
            ? ", not counting logging and Microsoft.Extensions (--verbose includes them)" : "";
        return Display.Plural(escapes.Length, "uncaught exception") + HandledCount(handled) + CrashCount(crashes.Length)
            + (!context.Arguments.Strict ? "" : (unwrapped.Length == 0 ? "; every third-party call has a catch on the way"
                : $"; {Display.Plural(unwrapped.Length, "third-party call")} with no catch on the way") + leftOut);
    }

    // A path that reaches a fake or test class only through an assumed implementation is a guess about what DI picks.
    private static bool Hidden(Display display, ThrowSiteFact origin, ExceptionWitness witness) =>
        witness.ViaCandidate && (display.IsTestDouble(origin.Owner) || witness.Path.Any(display.IsTestDouble));

    private static string CrashCount(int crashes) => crashes == 0 ? "" : $"; {Display.Plural(crashes, "crash", "crashes")} from async void";

    // An exception leaving an async void method: no try around the call sees it, and global handlers do not either, so the
    // block names the method it leaves and the actions that reach it.
    private static void WriteCrash(ReportContext context, Report report, CrashingException crash)
    {
        var (index, display) = (context.Index, context.Display);
        var origin = index.ThrowSite(crash.Origin);
        var type = crash.ExceptionType ?? "exception of unknown type";
        var rethrow = crash.SubsequentOrigin is { } at && index.TryGetThrowSite(at, out var again) ? again : null;
        if (context.Arguments.MSBuild)
        {
            Warn(context, "SD0005", origin.Evidence, $"{type} escapes async void {display.Short(crash.Method)}, where no caller can catch it, and ends the process"
                + (rethrow is null ? "" : $"; rethrown in {display.Short(rethrow.Owner)} ({FileLine(rethrow.Evidence)})")
                + $"; reached from {Actions(display, crash.Roots)}; {Chain(context, crash.Example.Path, crash.ViaCandidate)}");
            return;
        }
        report.Begin(type);
        WriteOrigin(context, report, "Thrown", [origin], crash.Example.Path);
        if (rethrow is not null) WriteOrigin(context, report, "Rethrown", [rethrow], []);
        report.Line("Crashes", $"leaves async void {display.Short(crash.Method)}, where no caller can catch it; the process ends");
        if (crash.Roots is not [var only] || crash.Example.Path.FirstOrDefault() != only) report.List("Escapes", crash.Roots.Select(display.Short));
        WriteExample(context, report, crash.Example);
    }

    // --strict: one block per third-party call site that no catch meets on the way from an action. Global handlers do not
    // count: the rule asks for a catch around the call.
    private sealed record UnwrappedCall(CallSiteFact Site, (CallableId Root, ExceptionEscapeFact Escape)[] Items);

    private static UnwrappedCall[] UnwrappedCalls(ProgramIndex index, ExceptionSummaryResult summaries, CallableId[] roots) =>
        roots.SelectMany(root => summaries.Roots[root].Escapes.Where(x => summaries.ThirdPartyCalls.ContainsKey(x.Origin)).Select(escape => (Root: root, Escape: escape)))
            .GroupBy(x => x.Escape.Origin).Select(x => new UnwrappedCall(index.CallSite(summaries.ThirdPartyCalls[x.Key]), x.ToArray()))
            .OrderBy(x => x.Site.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Site.Evidence.SpanStart).ToArray();

    private static void WriteUnwrapped(ReportContext context, Report report, ExceptionSummaryResult summaries, UnwrappedCall[] calls, CancellationToken token)
    {
        var display = context.Display;
        if (calls.Length == 0) return;
        if (!context.Arguments.MSBuild) report.Section("Third-party calls with no catch on the way from the action (--strict):", []);
        foreach (var (site, items) in calls)
        {
            token.ThrowIfCancellationRequested();
            var actions = items.Select(x => x.Root).Distinct().ToArray();
            var example = items.OrderBy(x => x.Escape.ViaCandidate).First();
            var witness = summaries.GetWitness(example.Root, example.Escape);
            var target = site.BoundTarget is { } bound ? display.Name(bound) : site.TargetDisplay;
            if (context.Arguments.MSBuild)
            {
                Warn(context, "SD0004", site.Evidence, $"{target} is a third-party call with no catch on the way from {Actions(display, actions)}; {Chain(context, witness.Path, example.Escape.ViaCandidate)}");
                continue;
            }
            report.Begin(target);
            report.Line("Called", $"{display.Short(site.Caller)}  {display.Where(site.Evidence)}{(witness.Path is [_] ? "  (in the action)" : "")}");
            if (actions is not [var only] || witness.Path.FirstOrDefault() != only) report.List("Escapes", actions.Select(display.Short));
            WritePath(context, report, witness.Path, witness.Sites, []);
        }
    }

    // Build warnings are one line each, so they name three actions and count the rest; text reports list them all.
    private static string Actions(Display display, IReadOnlyCollection<CallableId> actions) =>
        string.Join(", ", actions.Take(3).Select(display.Short)) + (actions.Count > 3 ? $" and {actions.Count - 3} more" : "");

    private static string HandledCount(int handled) => handled == 0 ? "" : $" ({handled} handled by a global exception handler)";

    // Exceptions that escape the action still reach exception middleware or a global exception filter registered by the
    // host. Those findings are listed last, each saying which registration receives it, so every block reads on its own.
    private sealed class GlobalHandlers(ProgramIndex index)
    {
        private readonly Dictionary<CallableId, ImmutableArray<BoundaryHandlingFact>> byRoot = [];

        private ImmutableArray<BoundaryHandlingFact> Of(CallableId root) =>
            byRoot.TryGetValue(root, out var found) ? found : byRoot[root] = AspNetConventions.GlobalHandlers(index, root);

        // True when the exception may escape and every action it escapes from has a global handler.
        public bool Covers(IReadOnlyCollection<CallableId> roots, bool escapes) =>
            escapes && roots.Count > 0 && roots.All(x => !Of(x).IsEmpty);

        private IEnumerable<BoundaryHandlingFact> All(IEnumerable<CallableId> roots) => roots.SelectMany(x => Of(x)).Distinct()
            .OrderBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart);

        public string Names(IEnumerable<CallableId> roots) => string.Join(", ", All(roots).Select(x => x.Registration).Distinct());

        public string Describe(ReportContext context, IEnumerable<CallableId> roots) =>
            "by the global exception handler " + string.Join("; ", All(roots).Select(x => $"{x.Registration}  {context.Display.Where(x.Evidence)}"));
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
        ExceptionReason.IteratorDeferred => ("exception raised inside an iterator, which surfaces when it is enumerated", "exceptions raised inside iterators, which surface when they are enumerated"),
        ExceptionReason.UnobservedTask => ("task that is never awaited", "tasks that are never awaited"),
        ExceptionReason.AsyncUnsupported => ("async iterator or custom awaitable", "async iterators or custom awaitables"),
        ExceptionReason.FinallyUnsupported => ("finally block that may throw", "finally blocks that may throw"),
        ExceptionReason.MissingBody => ("method without a body", "methods without a body"),
        ExceptionReason.DispatchBoundary => ("interface or virtual call without a source implementation", "interface or virtual calls without a source implementation"),
        ExceptionReason.UnresolvedTarget => ("call that does not compile", "calls that do not compile"),
        ExceptionReason.UnboundExceptionType => ("exception whose type does not compile", "exceptions whose type does not compile"),
        ExceptionReason.ExtractionGap => ("construct SharpDrill does not analyse", "constructs SharpDrill does not analyse"),
        ExceptionReason.HandlerFlowUnsupported => ("catch block with unclear flow", "catch blocks with unclear flow"),
        _ => null,
    };

    private static void WriteLimit(Arguments arguments) =>
        Console.Error.WriteLine($"Stopped early: the analysis budget ran out, so the list above is incomplete. Rerun with --budget {arguments.Budget * 10}.");


    private static void WriteCaught(ReportContext context, Report report, CaughtException caught)
    {
        var (index, display) = (context.Index, context.Display);
        var origin = index.ThrowSite(caught.Origin);
        var handler = index.Catch(caught.Catch);
        report.Begin(Thrown(origin));
        WriteOrigin(context, report, "Thrown", [origin], caught.Example.Path);
        report.Line("Caught by", $"{display.Short(handler.Owner)}  {display.Where(handler.Evidence)}");
        report.More(Clause(handler));
        report.Line("Handler", $"{Outcome(handler.Outcome)}{(caught.Certainty == CatchCertainty.Possible ? handler.Filter is null ? " (only if the runtime type matches)" : " (only if the filter accepts it)" : "")}");
        WriteExample(context, report, caught.Example);
    }

    private static void WriteMiscaught(ReportContext context, Report report, MiscaughtException miscaught, GlobalHandlers handlers, bool handled)
    {
        var (index, display) = (context.Index, context.Display);
        var origin = index.ThrowSite(miscaught.Origin);
        var region = index.Region(miscaught.Missed.Region);
        report.Begin(miscaught.Missed.ExceptionType ?? Thrown(origin));
        WriteOrigin(context, report, "Thrown", [origin], miscaught.Example.Path);
        report.Line("Missed by", $"{display.Short(region.Owner)}  {display.Where(region.ProtectedBlock)}");
        report.More($"try {{ ... }} {string.Join(" ", region.Catches.Select(x => Clause(index.Catch(x))))}");
        report.Line("Then", string.Join("; ", miscaught.Outcomes.Select(x => Fate(context, miscaught, x, msbuild: false, null))));
        if (handled) report.Line("Handled", handlers.Describe(context, miscaught.EscapesFrom));
        WriteExample(context, report, miscaught.Example);
    }

    // The clause as written, so it reads like the code it points at.
    private static string Clause(CatchFact handler) =>
        (handler.CaughtType is { } type ? $"catch ({type.DisplayName})" : "catch") + (handler.Filter is null ? "" : " when (...)");

    // Throws of one method, one line per file. A one-step path means the throw is in the action itself; the path line is
    // then left out.
    private static void WriteOrigin(ReportContext context, Report report, string label, IReadOnlyList<ThrowSiteFact> origins, ImmutableArray<CallableId> path) =>
        report.Line(label, $"{context.Display.Short(origins[0].Owner)}  "
            + string.Join("; ", origins.GroupBy(x => x.Evidence.Path).Select(x => context.Display.Where(x.First().Evidence) + string.Concat(x.Skip(1).Select(e => $", {e.Evidence.StartLine}"))))
            + (path is [_] ? "  (in the action)" : ""));


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
    private static string Fate(ReportContext context, MiscaughtException miscaught, ExceptionStatus status, bool msbuild, string? handler) => status switch
    {
        ExceptionStatus.Intercepted => "caught further up",
        ExceptionStatus.RethrownOrReplaced => "rethrown further up",
        ExceptionStatus.EscapesAsyncVoid => "leaves an async void method, where no caller can catch it; the process ends",
        ExceptionStatus.MayEscape => "may escape " + (msbuild ? Actions(context.Display, miscaught.EscapesFrom) : string.Join(", ", miscaught.EscapesFrom.Select(context.Display.Short)))
            + (handler is null ? "" : $" to the global exception handler {handler}"),
        _ => "not determined" + (miscaught.Undetermined is { } undetermined && Cause(undetermined.ReasonCode) is { } cause
            ? $": {cause.One}" + (undetermined.Evidence.LastOrDefault() is { } at ? $" at {context.Display.Where(at)}" : "") : ""),
    };
}
