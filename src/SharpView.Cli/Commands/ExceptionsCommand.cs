using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

/// <summary>Default: every explicit throw in source. --uncaught/--caught/--miscaught: the fate of each throw along the
/// call chains from controller actions (or --root).</summary>
internal sealed class ExceptionsCommand : ReportCommand
{
    private static readonly string[] Views = ["--uncaught", "--caught", "--miscaught"];
    private static readonly string[] TracedOptions = ["--root", "--budget", "--locations", "--declared-only"];

    public override ImmutableHashSet<string> Options { get; } = [.. TracedOptions, .. Views, "--strict", "--format"];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Given.Count(Views.Contains) > 1) throw new ArgumentException("Choose one of --uncaught, --caught or --miscaught.");
        if (arguments.MSBuild && arguments.View == ExceptionView.Caught) throw new ArgumentException("--format msbuild reports uncaught and miscaught exceptions; caught ones are not warnings.");
        if (arguments.MSBuild && arguments.Locations) throw new ArgumentException("--format msbuild prints one line per finding; --locations does not apply.");
        if (arguments.Strict && arguments.View != ExceptionView.Uncaught && !(arguments.MSBuild && arguments.View == ExceptionView.Throws)) throw new ArgumentException("--strict requires --uncaught.");
        if (arguments.MSBuild && arguments.View == ExceptionView.Throws) { SummaryQuery(arguments).Validate(); PathQuery(arguments).Validate(); return; }
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
        var roots = context.Arguments.MSBuild || context.Arguments.View != ExceptionView.Throws ? Roots.Select(context.Index, context.Arguments, RootScope.Actions, token) : [];
        if (context.Arguments.View == ExceptionView.Throws && !context.Arguments.MSBuild) return ValueTask.FromResult(Throws(context, token));
        var incomplete = context.Arguments.View switch
        {
            // Build warnings default to every finding worth one: uncaught and miscaught.
            ExceptionView.Throws => Uncaught(context, roots, token) | Handled(context, roots, caught: false, token),
            ExceptionView.Uncaught => Uncaught(context, roots, token),
            ExceptionView.Caught => Handled(context, roots, caught: true, token),
            _ => Handled(context, roots, caught: false, token),
        };
        if (suppressed > 0) Console.Error.WriteLine($"{Display.Plural(suppressed, "warning")} suppressed in source (#pragma warning disable or [SuppressMessage]).");
        return ValueTask.FromResult(incomplete);
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
        var (count, handled, report) = (0, 0, new Report(context.Output));
        // A path that reaches a fake or test class only through an assumed implementation is a guess about what DI picks.
        bool Hidden(ThrowSiteFact origin, ExceptionWitness witness) =>
            witness.ViaCandidate && (display.IsTestDouble(origin.Owner) || witness.Path.Any(display.IsTestDouble));
        if (caught)
        {
            var items = ExceptionHandlingQuery.Caught(result).Where(x => !Hidden(context.Index.ThrowSite(x.Origin), x.Example)).ToArray();
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
            var items = ExceptionHandlingQuery.Miscaught(result).Where(x => !Hidden(context.Index.ThrowSite(x.Origin), x.Example))
                .Select(x => (Item: x, Handled: handlers.Covers(x.EscapesFrom, x.Outcomes.Contains(ExceptionStatus.MayEscape)))).OrderBy(x => x.Handled).ToArray();
            if (!arguments.MSBuild)
                report.Overview(Display.Plural(items.Length, "miscaught exception") + HandledCount(items.Count(x => x.Handled)),
                    [.. items.Select(x => x.Item.Missed.ExceptionType ?? Thrown(context.Index.ThrowSite(x.Item.Origin)))]);
            foreach (var (item, covered) in items)
            {
                token.ThrowIfCancellationRequested();
                if (covered && handled++ == 0 && !arguments.MSBuild) handlers.WriteHeader(context, report);
                if (arguments.MSBuild) WarnMiscaught(context, item); else WriteMiscaught(context, report, item);
                count++;
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
        var (handled, report) = (0, new Report(output));
        var unwrapped = context.Arguments.Strict ? UnwrappedCalls(index, summaries, ordered) : [];
        if (!context.Arguments.MSBuild)
            report.Overview(Display.Plural(escapes.Length, "uncaught exception") + HandledCount(escapes.Count(x => x.Key.Handled))
                + (unwrapped.Length > 0 ? $"; {Display.Plural(unwrapped.Length, "third-party call")} with no catch on the way (--strict)" : ""),
                [.. escapes.Select(x => x.Key.ExceptionType!)], unwrapped.Length);
        foreach (var group in escapes)
        {
            token.ThrowIfCancellationRequested();
            if (group.Key.Handled && handled++ == 0 && !context.Arguments.MSBuild) handlers.WriteHeader(context, report);
            var actions = group.Select(x => x.Root).Distinct().ToArray();
            // A proven path makes the better example than one through an assumed implementation.
            var example = group.OrderBy(x => x.Escape.ViaCandidate).First();
            if (context.Arguments.MSBuild)
            {
                var rethrown = group.Key.SubsequentOrigin is { } at && index.TryGetThrowSite(at, out var again) ? $", rethrown in {display.Short(again.Owner)} ({FileLine(again.Evidence)})" : "";
                var to = group.Key.Handled ? $" to the global exception handler {handlers.Names(actions)}" : "";
                Warn(context, group.Key.Handled ? "SV0002" : "SV0001", index.ThrowSite(group.Key.Origin).Evidence,
                    $"{group.Key.ExceptionType} may escape {Actions(display, actions)}{to}{rethrown}; {Chain(context, summaries.GetWitness(example.Root, example.Escape).Path, example.Escape.ViaCandidate)}");
                continue;
            }
            var witness = summaries.GetWitness(example.Root, example.Escape);
            report.Begin(group.Key.ExceptionType!);
            WriteOrigin(context, report, "Thrown", index.ThrowSite(group.Key.Origin), witness.Path);
            if (group.Key.SubsequentOrigin is { } rethrow && index.TryGetThrowSite(rethrow, out var site)) WriteOrigin(context, report, "Rethrown", site, []);
            // The path starts at the action, so a single action is already named there.
            if (actions is not [var only] || witness.Path.FirstOrDefault() != only) report.Line("Escapes", Actions(display, actions));
            WritePath(context, report, witness.Path, witness.Sites, example.Escape.ViaCandidate, []);
        }
        if (context.Arguments.Strict) WriteUnwrapped(context, report, summaries, unwrapped, token);
        Console.Error.WriteLine($"{Display.Plural(roots.Length, "action")} analysed: {Display.Plural(escapes.Length, "uncaught exception")}{HandledCount(handled)}"
            + (context.Arguments.Strict ? $"; {Display.Plural(unwrapped.Length, "third-party call")} with no catch on the way." : "."));
        WriteNotFollowed(context, roots.SelectMany(x => summaries.Roots[x].Unknowns)
            .Where(x => x.ReasonCode is not (ExceptionReason.WorkLimit or ExceptionReason.FactLimit))
            .Select(x => (x.ReasonCode, x.Site is { } s ? index.CallSite(s).Evidence : x.Evidence)));
        if (summaries.WorkLimitReached || summaries.FactLimitReached) WriteLimit(context.Arguments);
        return !summaries.IsComplete;
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
                Warn(context, "SV0004", site.Evidence, $"{target} is a third-party call with no catch on the way from {Actions(display, actions)}; {Chain(context, witness.Path, example.Escape.ViaCandidate)}");
                continue;
            }
            report.Begin(target);
            report.Line("Called", $"{display.Short(site.Caller)}  {display.Where(site.Evidence)}{(witness.Path is [_] ? "  (in the action)" : "")}");
            if (actions is not [var only] || witness.Path.FirstOrDefault() != only) report.Line("Escapes", Actions(display, actions));
            WritePath(context, report, witness.Path, witness.Sites, example.Escape.ViaCandidate, []);
        }
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

        public string Names(IEnumerable<CallableId> roots) => string.Join(", ", roots.SelectMany(x => Of(x)).Select(x => x.Registration).Distinct().Order(StringComparer.Ordinal));

        public void WriteHeader(ReportContext context, Report report) =>
            report.Section("Handled by a global exception handler:", used.Select(x => $"{x.Registration}  {context.Display.Where(x.Evidence)}"));
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
        var (index, display) = (context.Index, context.Display);
        var origin = index.ThrowSite(miscaught.Origin);
        var region = index.Region(miscaught.Missed.Region);
        var clauses = region.Catches.Select(x => index.Catch(x).CaughtType?.DisplayName ?? "all exceptions");
        Warn(context, "SV0003", region.ProtectedBlock, $"{miscaught.Missed.ExceptionType ?? Thrown(origin)} thrown in {display.Short(origin.Owner)} ({FileLine(origin.Evidence)}) "
            + $"passes through this try, which catches only {string.Join(", ", clauses)}; then {string.Join("; ", miscaught.Outcomes.Select(x => Fate(context, x, miscaught.Undetermined)))}; "
            + Chain(context, miscaught.Example.Path, miscaught.ViaCandidate));
    }

    private static string Chain(ReportContext context, ImmutableArray<CallableId> path, bool assumed) =>
        $"path{(assumed ? " (possible, not proven)" : "")}: {string.Join(" -> ", path.Select(context.Display.Short))}";

    private static void WriteCaught(ReportContext context, Report report, CaughtException caught)
    {
        var (index, display) = (context.Index, context.Display);
        var origin = index.ThrowSite(caught.Origin);
        var handler = index.Catch(caught.Catch);
        report.Begin(Thrown(origin));
        WriteOrigin(context, report, "Thrown", origin, caught.Example.Path);
        report.Line("Caught by", $"{display.Short(handler.Owner)}  {display.Where(handler.Evidence)}");
        report.More(Clause(handler));
        report.Line("Handler", $"{Outcome(handler.Outcome)}{(caught.Certainty == CatchCertainty.Possible ? handler.Filter is null ? " (only if the runtime type matches)" : " (only if the filter accepts it)" : "")}");
        WriteExample(context, report, caught.Example);
    }

    private static void WriteMiscaught(ReportContext context, Report report, MiscaughtException miscaught)
    {
        var (index, display) = (context.Index, context.Display);
        var origin = index.ThrowSite(miscaught.Origin);
        var region = index.Region(miscaught.Missed.Region);
        report.Begin(miscaught.Missed.ExceptionType ?? Thrown(origin));
        WriteOrigin(context, report, "Thrown", origin, miscaught.Example.Path);
        report.Line("Missed by", $"{display.Short(region.Owner)}  {display.Where(region.ProtectedBlock)}");
        report.More($"try {{ ... }} {string.Join(" ", region.Catches.Select(x => Clause(index.Catch(x))))}");
        report.Line("Then", string.Join("; ", miscaught.Outcomes.Select(x => Fate(context, x, miscaught.Undetermined))));
        WriteExample(context, report, miscaught.Example);
    }

    // The clause as written, so it reads like the code it points at.
    private static string Clause(CatchFact handler) =>
        (handler.CaughtType is { } type ? $"catch ({type.DisplayName})" : "catch") + (handler.Filter is null ? "" : " when (...)");

    // A one-step path means the throw is in the action itself; the path line is then left out.
    private static void WriteOrigin(ReportContext context, Report report, string label, ThrowSiteFact origin, ImmutableArray<CallableId> path) =>
        report.Line(label, $"{context.Display.Short(origin.Owner)}  {context.Display.Where(origin.Evidence)}{(path is [_] ? "  (in the action)" : "")}");

    private static void WriteExample(ReportContext context, Report report, ExceptionWitness witness) =>
        WritePath(context, report, witness.Path, witness.Sites, witness.ViaCandidate, witness.Evidence);

    // From the action down to the throw, on one line when it fits. A step through an interface/virtual call (~>) assumes
    // which implementation runs, so a path with such a step is possible, not proven.
    private static void WritePath(ReportContext context, Report report, ImmutableArray<CallableId> path, ImmutableArray<CallSiteId> sites, bool assumed, ImmutableArray<SourceEvidence> evidence)
    {
        var (index, display) = (context.Index, context.Display);
        var steps = path.Select((x, i) => (Arrow: i == 0 ? "" : i - 1 < sites.Length && index.CallSite(sites[i - 1]).Dispatch != DispatchKind.Direct ? "~> " : "-> ", Name: display.Short(x))).ToArray();
        if (steps.Length > 1)
        {
            var inline = string.Join(" ", steps.Select(x => x.Arrow + x.Name));
            if (report.Fits(inline)) report.Line("Path", inline);
            else { report.Line("Path", steps[0].Name); foreach (var (arrow, name) in steps[1..]) report.Step(arrow, name); }
            if (assumed) report.More("(possible, not proven: ~> assumes which implementation runs)");
        }
        if (!context.Arguments.Locations) return;
        var places = (evidence.IsEmpty ? sites.Select(x => index.CallSite(x).Evidence) : evidence).Select(display.Where).ToArray();
        for (var i = 0; i < places.Length; i++) if (i == 0) report.Line("Locations", places[i]); else report.More(places[i]);
    }

    // Numbered findings separated by blank lines, after an overview by exception type; labels share one column so the
    // values line up.
    private sealed class Report(TextWriter output)
    {
        private const int Column = 15, Width = 120;
        private int number;
        private bool written;

        private void Separate() { if (written) output.WriteLine(); written = true; }

        // Left out for a single finding, where it would only repeat it.
        public void Overview(string headline, string[] types, int others = 0)
        {
            if (types.Length + others < 2) return;
            Separate();
            output.WriteLine($"Overview: {headline}");
            var byType = types.GroupBy(x => x).Select(x => (Type: x.Key, Count: x.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray();
            var width = byType.Select(x => x.Count.ToString().Length).DefaultIfEmpty().Max();
            foreach (var (type, count) in byType) output.WriteLine($"  {count.ToString().PadLeft(width)}  {type}");
        }

        public void Section(string title, IEnumerable<string> items)
        {
            Separate();
            output.WriteLine(title);
            foreach (var item in items) output.WriteLine($"  - {item}");
        }

        public void Begin(string title) { Separate(); output.WriteLine($"{++number}. {title}"); }
        public void Line(string label, string text) => output.WriteLine($"   {label + ":",-11} {text}");
        public void More(string text) => output.WriteLine(new string(' ', Column) + text);
        public void Step(string arrow, string text) => output.WriteLine(new string(' ', Column - arrow.Length) + arrow + text);
        public bool Fits(string text) => Column + text.Length <= Width;
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
