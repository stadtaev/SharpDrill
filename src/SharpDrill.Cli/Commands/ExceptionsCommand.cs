using System.Collections.Immutable;
using SharpDrill.Core;

namespace SharpDrill.Cli;

/// <summary>Formats an <see cref="ExceptionReport"/>: uncaught (default), caught, miscaught or every throw. No analysis here.</summary>
internal sealed partial class ExceptionsCommand : ReportCommand
{
    private static readonly string[] TracedOptions = ["--from", "--budget", "--locations", "--declared-only", "--third-party"];

    public override ImmutableHashSet<string> Options { get; } = [.. TracedOptions, "--format"];

    public override ImmutableDictionary<string, ExceptionView> Views { get; } = new Dictionary<string, ExceptionView>
    {
        ["uncaught"] = ExceptionView.Uncaught, ["caught"] = ExceptionView.Caught, ["miscaught"] = ExceptionView.Miscaught, ["thrown"] = ExceptionView.Thrown,
    }.ToImmutableDictionary();

    public override void Validate(Arguments arguments)
    {
        if (arguments.MSBuild && arguments.View is ExceptionView.Caught or ExceptionView.Thrown) throw new ArgumentException("--format msbuild reports uncaught and miscaught exceptions; caught ones and plain throws are not warnings.");
        if (arguments.MSBuild && arguments.Locations) throw new ArgumentException("--format msbuild prints one line per finding; --locations does not apply.");
        if (arguments.ThirdParty && arguments.View is not (null or ExceptionView.Uncaught)) throw new ArgumentException("--third-party applies to the uncaught view.");
        if (arguments.View == ExceptionView.Thrown && arguments.Given.Overlaps(TracedOptions))
            throw new ArgumentException("thrown lists every throw in source and takes no options; --from, --third-party, --budget, --locations and --declared-only trace from entry points.");
        if (arguments.Budget <= 0) throw new ArgumentException("--budget must be positive.");
    }

    private static ExceptionOptions AnalysisOptions(Arguments arguments) =>
        new() { From = arguments.From, FollowImplementations = !arguments.DeclaredOnly, ThirdPartyCalls = arguments.ThirdParty, Budget = arguments.Budget };

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var report = context.Index.Exceptions(AnalysisOptions(context.Arguments), token);
        if (context.Arguments.View == ExceptionView.Thrown) return ValueTask.FromResult(Throws(context, report, token));
        string[] counts = context.Arguments.View switch
        {
            // Build warnings default to every finding worth one: uncaught and miscaught.
            null when context.Arguments.MSBuild => [Uncaught(context, report, token), Miscaught(context, report, token)],
            ExceptionView.Caught => [Caught(context, report, token)],
            ExceptionView.Miscaught => [Miscaught(context, report, token)],
            _ => [Uncaught(context, report, token)],
        };
        // One summary for the run, however many views it printed, so a CI log has one line to read.
        // Without entry points a zero count would read as a clean result, so that case says nothing was traced.
        Console.Error.WriteLine(report.EntryPoints.IsEmpty ? "No entry points found." : string.Join("; ", counts) + ".");
        WriteNotFollowed(context, report.NotFollowed);
        if (report.BudgetExhausted) WriteLimit(context.Arguments);
        if (suppressed > 0) Console.Error.WriteLine($"{Display.Plural(suppressed, "warning")} suppressed in source (#pragma warning disable or [SuppressMessage]).");
        return ValueTask.FromResult(!report.IsComplete);
    }

    // One line per explicit throw.
    private static bool Throws(ReportContext context, ExceptionReport report, CancellationToken token)
    {
        foreach (var origin in report.Thrown)
        {
            token.ThrowIfCancellationRequested();
            context.Output.WriteLine($"{Thrown(origin)}  {context.Display.Short(origin.Owner)}  {context.Display.Where(origin.Evidence)}");
        }
        Console.Error.WriteLine($"{Display.Plural(report.Thrown.Length, "throw")} in source.");
        return false;
    }

    private static string Caught(ReportContext context, ExceptionReport report, CancellationToken token)
    {
        var output = new Report(context.Output);
        output.Overview(Display.Plural(report.Caught.Length, "caught exception"), [.. report.Caught.Select(x => Thrown(x.Thrown))]);
        foreach (var item in report.Caught) { token.ThrowIfCancellationRequested(); WriteCaught(context, output, item); }
        return $"{report.Caught.Length} caught";
    }

    private static string Miscaught(ReportContext context, ExceptionReport report, CancellationToken token)
    {
        var (items, output) = (report.Miscaught, new Report(context.Output));
        var handled = items.Count(x => !x.GlobalHandlers.IsEmpty);
        if (!context.Arguments.MSBuild)
            output.Overview(Display.Plural(items.Length, "miscaught exception") + HandledCount(handled), [.. items.Select(x => x.ExceptionType ?? Thrown(x.Thrown))]);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (context.Arguments.MSBuild) WarnMiscaught(context, item); else WriteMiscaught(context, output, item);
        }
        return $"{items.Length} miscaught{HandledCount(handled)}";
    }

    // One block per exception that may leave an entry point: the throw (and the rethrow it passed through), the entry points
    // it escapes from, and one example path. Throws of one type in one method are listed together (build warnings keep one
    // per throw, since each points at its throw).
    private static string Uncaught(ReportContext context, ExceptionReport report, CancellationToken token)
    {
        var display = context.Display;
        // The report lists findings handled last, then by throw, so each group keeps the place of its first throw.
        var groups = report.Uncaught.GroupBy(x => (Thrower: context.Arguments.MSBuild ? x.Thrown.Id.Value : x.Thrown.Owner.Value, Rethrown: x.Rethrown?.Id, x.ExceptionType, Handled: !x.GlobalHandlers.IsEmpty))
            .Select(x => x.ToArray()).ToArray();
        var (crashes, unguarded, output) = (report.Crashes, report.UnguardedCalls, new Report(context.Output));
        if (!context.Arguments.MSBuild)
            output.Overview(Display.Plural(groups.Length, "uncaught exception") + HandledCount(groups.Count(x => !x[0].GlobalHandlers.IsEmpty)) + CrashCount(crashes.Length)
                + (unguarded.Length > 0 ? $"; {Display.Plural(unguarded.Length, "third-party call")} with no catch on the way" : ""),
                [.. crashes.Select(x => x.ExceptionType ?? "exception of unknown type"), .. groups.Select(x => x[0].ExceptionType)], unguarded.Length);
        foreach (var crash in crashes) { token.ThrowIfCancellationRequested(); WriteCrash(context, output, crash); }
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var first = group[0];
            var origins = group.Select(x => x.Thrown).Distinct().OrderBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart).ToArray();
            var entries = group.SelectMany(x => x.EscapesFrom).Distinct().OrderBy(x => x.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).Select(x => x.Id).ToArray();
            var handlers = group.SelectMany(x => x.GlobalHandlers).Distinct().OrderBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart).ToArray();
            // A proven path makes the better example than one through an assumed implementation; then the shortest.
            var example = group.Select(x => x.Example).OrderBy(x => x.IsAssumed).ThenBy(x => x.Steps.Length).First();
            if (context.Arguments.MSBuild)
            {
                var rethrown = first.Rethrown is { } again ? $", rethrown in {display.Short(again.Owner)} ({FileLine(again.Evidence)})" : "";
                var to = handlers.Length > 0 ? $" to the global exception handler {Names(handlers)}" : "";
                Warn(context, handlers.Length > 0 ? "SD0002" : "SD0001", origins[0].Evidence, $"{first.ExceptionType} may escape {EntryPoints(display, entries)}{to}{rethrown}; {Chain(context, example)}");
                continue;
            }
            output.Begin(first.ExceptionType);
            WriteOrigin(context, output, "Thrown", origins, example);
            if (first.Rethrown is { } rethrow) WriteOrigin(context, output, "Rethrown", [rethrow], null);
            // The path starts at the entry point, so a single one is already named there.
            if (entries is not [var only] || example.EntryPoint.Id != only) output.List("Escapes", entries.Select(display.Short));
            if (handlers.Length > 0) output.Line("Handled", Describe(context, handlers));
            WritePath(context, output, example);
        }
        WriteUnguarded(context, output, unguarded, token);
        return Display.Plural(groups.Length, "uncaught exception") + HandledCount(groups.Count(x => !x[0].GlobalHandlers.IsEmpty)) + CrashCount(crashes.Length)
            + (!context.Arguments.ThirdParty ? "" : (unguarded.Length == 0 ? "; every third-party call has a catch on the way" : $"; {Display.Plural(unguarded.Length, "third-party call")} with no catch on the way")
                + (report.LeavesOutRoutineCalls ? ", not counting logging and Microsoft.Extensions" : ""));
    }

    private static string CrashCount(int crashes) => crashes == 0 ? "" : $"; {Display.Plural(crashes, "crash", "crashes")} from async void";

    // An exception leaving an async void method: no try around the call sees it, and global handlers do not either, so the
    // block names the method it leaves and the entry points that reach it.
    private static void WriteCrash(ReportContext context, Report report, CrashingException crash)
    {
        var display = context.Display;
        var type = crash.ExceptionType ?? "exception of unknown type";
        var entries = crash.EscapesFrom.Select(x => x.Id).ToArray();
        if (context.Arguments.MSBuild)
        {
            Warn(context, "SD0005", crash.Thrown.Evidence, $"{type} escapes async void {display.Short(crash.AsyncVoidMethod.Id)}, where no caller can catch it, and ends the process"
                + (crash.Rethrown is not { } rethrow ? "" : $"; rethrown in {display.Short(rethrow.Owner)} ({FileLine(rethrow.Evidence)})")
                + $"; reached from {EntryPoints(display, entries)}; {Chain(context, crash.Example)}");
            return;
        }
        report.Begin(type);
        WriteOrigin(context, report, "Thrown", [crash.Thrown], crash.Example);
        if (crash.Rethrown is { } again) WriteOrigin(context, report, "Rethrown", [again], null);
        report.Line("Crashes", $"leaves async void {display.Short(crash.AsyncVoidMethod.Id)}, where no caller can catch it; the process ends");
        if (entries is not [var only] || crash.Example.EntryPoint.Id != only) report.List("Escapes", entries.Select(display.Short));
        WritePath(context, report, crash.Example);
    }

    // --third-party: one block per third-party call site that no catch meets on the way from an entry point.
    private static void WriteUnguarded(ReportContext context, Report report, ImmutableArray<UnguardedCall> calls, CancellationToken token)
    {
        var display = context.Display;
        if (calls.Length == 0) return;
        if (!context.Arguments.MSBuild) report.Section("Third-party calls with no catch on the way from the entry point (--third-party):", []);
        foreach (var call in calls)
        {
            token.ThrowIfCancellationRequested();
            var entries = call.EscapesFrom.Select(x => x.Id).ToArray();
            var target = call.Call.BoundTarget is { } bound ? display.Name(bound) : call.Call.TargetDisplay;
            if (context.Arguments.MSBuild)
            {
                Warn(context, "SD0004", call.Call.Evidence, $"{target} is a third-party call with no catch on the way from {EntryPoints(display, entries)}; {Chain(context, call.Example)}");
                continue;
            }
            report.Begin(target);
            report.Line("Called", $"{display.Short(call.Call.Caller)}  {display.Where(call.Call.Evidence)}{(call.Example.Steps is [_] ? "  (in the entry point)" : "")}");
            if (entries is not [var only] || call.Example.EntryPoint.Id != only) report.List("Escapes", entries.Select(display.Short));
            WritePath(context, report, call.Example);
        }
    }

    // Build warnings are one line each, so they name three entry points and count the rest; text reports list them all.
    private static string EntryPoints(Display display, IReadOnlyCollection<CallableId> entries) =>
        string.Join(", ", entries.Take(3).Select(display.Short)) + (entries.Count > 3 ? $" and {entries.Count - 3} more" : "");

    private static string HandledCount(int handled) => handled == 0 ? "" : $" ({handled} handled by a global exception handler)";

    private static string Names(IEnumerable<BoundaryHandlingFact> handlers) => string.Join(", ", handlers.Select(x => x.Registration).Distinct());

    private static string Describe(ReportContext context, IEnumerable<BoundaryHandlingFact> handlers) =>
        "by the global exception handler " + string.Join("; ", handlers.Select(x => $"{x.Registration}  {context.Display.Where(x.Evidence)}"));

    // Where the analysis could not follow an exception, grouped by cause, one line in all.
    private static void WriteNotFollowed(ReportContext context, ImmutableArray<NotFollowed> groups)
    {
        var lines = groups.Where(x => Label(context, x.Reason) is not null).Select(g =>
            $"{Display.Plural(g.Places.Length, Label(context, g.Reason)!.Value.One, Label(context, g.Reason)!.Value.Many)} (e.g. {context.Display.Where(g.Places[0])})").ToArray();
        if (lines.Length > 0) Console.Error.WriteLine("Not followed: " + string.Join("; ", lines) + ".");
    }

    // Under --declared-only an interface call stops by request, not for lack of an implementation.
    private static (string One, string Many)? Label(ReportContext context, ExceptionReason reason) => reason == ExceptionReason.DispatchBoundary && context.Arguments.DeclaredOnly
        ? ("interface or virtual call not followed (--declared-only)", "interface or virtual calls not followed (--declared-only)") : Cause(reason);

    // Plain-words causes of what the analysis could not follow.
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
        var handler = caught.Catch;
        report.Begin(Thrown(caught.Thrown));
        WriteOrigin(context, report, "Thrown", [caught.Thrown], caught.Example);
        report.Line("Caught by", $"{context.Display.Short(handler.Owner)}  {context.Display.Where(handler.Evidence)}");
        report.More(Clause(handler));
        report.Line("Handler", $"{Outcome(handler.Outcome)}{(caught.IsCertain ? "" : handler.Filter is null ? " (only if the runtime type matches)" : " (only if the filter accepts it)")}");
        WritePath(context, report, caught.Example);
    }

    private static void WriteMiscaught(ReportContext context, Report report, MiscaughtException miscaught)
    {
        var region = miscaught.MissedBy;
        report.Begin(miscaught.ExceptionType ?? Thrown(miscaught.Thrown));
        WriteOrigin(context, report, "Thrown", [miscaught.Thrown], miscaught.Example);
        report.Line("Missed by", $"{context.Display.Short(region.Owner)}  {context.Display.Where(region.ProtectedBlock)}");
        report.More($"try {{ ... }} {string.Join(" ", miscaught.Clauses.Select(Clause))}");
        report.Line("Then", string.Join("; ", miscaught.Then.Select(x => Fate(context, miscaught, x, msbuild: false))));
        if (!miscaught.GlobalHandlers.IsEmpty) report.Line("Handled", Describe(context, miscaught.GlobalHandlers));
        WritePath(context, report, miscaught.Example);
    }

    // The clause as written, so it reads like the code it points at.
    private static string Clause(CatchFact handler) =>
        (handler.CaughtType is { } type ? $"catch ({type.DisplayName})" : "catch") + (handler.Filter is null ? "" : " when (...)");

    // Throws of one method, one line per file. A one-step path means the throw is in the entry point itself; the path line is
    // then left out.
    private static void WriteOrigin(ReportContext context, Report report, string label, IReadOnlyList<ThrowSiteFact> origins, ExceptionPath? path) =>
        report.Line(label, $"{context.Display.Short(origins[0].Owner)}  "
            + string.Join("; ", origins.GroupBy(x => x.Evidence.Path).Select(x => context.Display.Where(x.First().Evidence) + string.Concat(x.Skip(1).Select(e => $", {e.Evidence.StartLine}"))))
            + (path?.Steps is [_] ? "  (in the entry point)" : ""));

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
    private static string Fate(ReportContext context, MiscaughtException miscaught, ExceptionStatus status, bool msbuild) => status switch
    {
        ExceptionStatus.Intercepted => "caught further up",
        ExceptionStatus.RethrownOrReplaced => "rethrown further up",
        ExceptionStatus.EscapesAsyncVoid => "leaves an async void method, where no caller can catch it; the process ends",
        ExceptionStatus.MayEscape => "may escape " + (msbuild ? EntryPoints(context.Display, [.. miscaught.EscapesFrom.Select(x => x.Id)]) : string.Join(", ", miscaught.EscapesFrom.Select(x => context.Display.Short(x.Id))))
            + (msbuild && !miscaught.GlobalHandlers.IsEmpty ? $" to the global exception handler {Names(miscaught.GlobalHandlers)}" : ""),
        _ => "not determined" + (miscaught.Undetermined is { } undetermined && Cause(undetermined.Reason) is { } cause
            ? $": {cause.One}" + (undetermined.Places is [var at, ..] ? $" at {context.Display.Where(at)}" : "") : ""),
    };
}
