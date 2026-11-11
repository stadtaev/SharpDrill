using System.Collections.Immutable;

namespace SharpDrill.Core;

/// <summary>What to trace. The defaults answer the usual question: every entry point, interface calls followed.</summary>
public sealed record ExceptionOptions
{
    /// <summary>One entry point to start from, by ID, signature or a unique part of one (<c>OrdersController.Get</c>); null: all.</summary>
    public string? From { get; init; }
    /// <summary>Follow interface and virtual calls into their implementations in source. Each such step is a possibility,
    /// never a claim about what runs (DI registrations are not read).</summary>
    public bool FollowImplementations { get; init; } = true;
    /// <summary>Also find calls into third-party assemblies (NuGet packages, referenced DLLs) that no catch meets on the way
    /// from an entry point. Logging and Microsoft.Extensions (except Caching) are left out.</summary>
    public bool ThirdPartyCalls { get; init; }
    /// <summary>Upper bound on analysis work; <see cref="ExceptionReport.BudgetExhausted"/> says when it ran out.</summary>
    public int Budget { get; init; } = 100_000;
}

/// <summary>One step of an example path. Call is the call that led here (null for the entry point). Implementations counts
/// the candidates source has for an interface or virtual call.</summary>
public sealed record PathStep(CallableFact Method, CallSiteFact? Call = null, int Implementations = 0)
{
    /// <summary>Reached through an interface or virtual call: an implementation found in source, assumed to be the one that runs.</summary>
    public bool IsAssumed => Call is { Dispatch: not DispatchKind.Direct };
}

/// <summary>One call chain from an entry point (Steps[0]) down to the throw. IsAssumed: some step follows an implementation
/// candidate, so the path is possible, not proven. Locations: the source evidence along it.</summary>
public sealed record ExceptionPath(ImmutableArray<PathStep> Steps, ImmutableArray<SourceEvidence> Locations, bool IsAssumed)
{
    public CallableFact EntryPoint => Steps[0].Method;
}

/// <summary>An exception that may leave entry points with no catch on the way. Rethrown: the catch it passed through on the
/// way. GlobalHandlers: exception middleware or MVC filters that receive it, when they cover every entry point listed.</summary>
public sealed record UncaughtException(ThrowSiteFact Thrown, ThrowSiteFact? Rethrown, string ExceptionType,
    ImmutableArray<CallableFact> EscapesFrom, ImmutableArray<BoundaryHandlingFact> GlobalHandlers, ExceptionPath Example);

/// <summary>An exception leaving an async void method: no caller can catch it, so the process ends.</summary>
public sealed record CrashingException(ThrowSiteFact Thrown, ThrowSiteFact? Rethrown, string? ExceptionType,
    CallableFact AsyncVoidMethod, ImmutableArray<CallableFact> EscapesFrom, ExceptionPath Example);

/// <summary>A throw reaching a matching catch. Not certain: a filter or the runtime type decides.</summary>
public sealed record CaughtException(ThrowSiteFact Thrown, CatchFact Catch, bool IsCertain, ExceptionPath Example);

/// <summary>A throw passing through a try whose catch clauses (Clauses) cannot match it. Then: what happens to it
/// afterwards over every path, EscapesFrom the entry points it may leave, Undetermined where the analysis lost it.</summary>
public sealed record MiscaughtException(ThrowSiteFact Thrown, string? ExceptionType, ExceptionRegionFact MissedBy,
    ImmutableArray<CatchFact> Clauses, ImmutableArray<ExceptionStatus> Then, ImmutableArray<CallableFact> EscapesFrom,
    ImmutableArray<BoundaryHandlingFact> GlobalHandlers, ExceptionPath Example)
{
    public NotFollowed? Undetermined { get; init; }
}

/// <summary>A third-party call that no catch meets on the way from the entry points (only with ThirdPartyCalls).</summary>
public sealed record UnguardedCall(CallSiteFact Call, ImmutableArray<CallableFact> EscapesFrom, ExceptionPath Example);

/// <summary>Places the analysis could not follow an exception past, for one reason: open questions, not findings.</summary>
public sealed record NotFollowed(ExceptionReason Reason, ImmutableArray<SourceEvidence> Places);

public static class ProgramExceptions
{
    /// <summary>Exceptions from the program's entry points (or the one Options.From names). Each view is computed on first read.</summary>
    public static ExceptionReport Exceptions(this ProgramIndex program, ExceptionOptions? options = null, CancellationToken token = default)
    {
        options ??= new();
        if (options.Budget <= 0) throw new ArgumentException("The budget must be positive.");
        return new(program, options, options.From is { } from ? [program.EntryPoint(from, token)] : program.EntryPoints(token), token);
    }
}

public sealed class ExceptionReport
{
    private readonly ProgramIndex program;
    private readonly CancellationToken token;
    private readonly Lazy<ExceptionSummaryResult> summary;
    private readonly Dictionary<CallableId, ImmutableArray<BoundaryHandlingFact>> handlers = [];

    internal ExceptionReport(ProgramIndex program, ExceptionOptions options, ImmutableArray<EntryPoint> entryPoints, CancellationToken token)
    {
        (this.program, this.token, Options, EntryPoints) = (program, token, options, entryPoints);
        summary = new(() => ExceptionSummaryQuery.Analyze(program, [.. entryPoints.Select(x => x.Method.Id)],
            new(options.Budget, options.Budget, options.FollowImplementations, options.ThirdPartyCalls), token));
        thrown = new(() => [.. program.Snapshot.ThrowSites.Where(x => x.Kind == ThrowKind.Explicit)
            .OrderBy(x => program.ProjectName(program.Callable(x.Owner).Compilation), StringComparer.Ordinal)
            .ThenBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart)]);
        uncaught = new(FindUncaught); crashes = new(FindCrashes); caught = new(FindCaught); miscaught = new(FindMiscaught);
        unguarded = new(FindUnguarded); notFollowed = new(FindNotFollowed);
    }

    public ExceptionOptions Options { get; }
    public ImmutableArray<EntryPoint> EntryPoints { get; }

    /// <summary>Every explicit throw in source (rethrows are not new exceptions), by project, file and position; independent of entry points.</summary>
    public ImmutableArray<ThrowSiteFact> Thrown => thrown.Value;
    /// <summary>Exceptions that may leave an entry point; those a global handler receives come last.</summary>
    public ImmutableArray<UncaughtException> Uncaught => uncaught.Value;
    public ImmutableArray<CrashingException> Crashes => crashes.Value;
    public ImmutableArray<CaughtException> Caught => caught.Value;
    /// <summary>Throws that slip past a try that does not match them; those a global handler receives come last.</summary>
    public ImmutableArray<MiscaughtException> Miscaught => miscaught.Value;
    /// <summary>Empty unless Options.ThirdPartyCalls.</summary>
    public ImmutableArray<UnguardedCall> UnguardedCalls => unguarded.Value;
    /// <summary>Where the analysis stopped, grouped by reason, most places first.</summary>
    public ImmutableArray<NotFollowed> NotFollowed => notFollowed.Value;
    /// <summary>The budget ran out: every list may be incomplete. Raise Options.Budget.</summary>
    public bool BudgetExhausted => Summary.WorkLimitReached || Summary.FactLimitReached;
    /// <summary>Nothing on the traced paths was left undecided and the budget held.</summary>
    public bool IsComplete => Summary.IsComplete;
    /// <summary>ThirdPartyCalls is on and the program calls logging or Microsoft.Extensions, which it leaves out.</summary>
    public bool LeavesOutRoutineCalls => Options.ThirdPartyCalls && program.Snapshot.ThirdPartyAssemblies.Any(ExceptionSummaryOptions.IsRoutineThirdParty);

    private readonly Lazy<ImmutableArray<ThrowSiteFact>> thrown;
    private readonly Lazy<ImmutableArray<UncaughtException>> uncaught;
    private readonly Lazy<ImmutableArray<CrashingException>> crashes;
    private readonly Lazy<ImmutableArray<CaughtException>> caught;
    private readonly Lazy<ImmutableArray<MiscaughtException>> miscaught;
    private readonly Lazy<ImmutableArray<UnguardedCall>> unguarded;
    private readonly Lazy<ImmutableArray<NotFollowed>> notFollowed;
    private ExceptionSummaryResult Summary => summary.Value;

    // One finding per throw, rethrow and type, split by whether a global handler receives it from every entry point listed.
    // What leaves an async void entry point is a crash, listed there.
    private ImmutableArray<UncaughtException> FindUncaught() =>
    [
        .. EntryPoints.Where(x => !x.Method.IsAsyncVoid).SelectMany(root => Summary.Roots[root.Method.Id].Escapes.Select(escape => (Root: root.Method, Escape: escape)))
            .Where(x => x.Escape.ExceptionType is not null && !Summary.ThirdPartyCalls.ContainsKey(x.Escape.Origin))
            .GroupBy(x => (x.Escape.Origin, x.Escape.SubsequentOrigin, x.Escape.ExceptionType, Covered: !Handlers(x.Root.Id).IsEmpty))
            .Select(group =>
            {
                token.ThrowIfCancellationRequested();
                // A proven path makes the better example than one through an assumed implementation; then the shortest.
                var example = group.OrderBy(x => x.Escape.ViaCandidate).ThenBy(x => Summary.GetWitness(x.Root.Id, x.Escape).Path.Length).First();
                var witness = Summary.GetWitness(example.Root.Id, example.Escape);
                var roots = group.Select(x => x.Root).Distinct().ToImmutableArray();
                return (group.Key, Finding: new UncaughtException(program.ThrowSite(group.Key.Origin), Rethrow(group.Key.SubsequentOrigin), group.Key.ExceptionType!,
                    roots, group.Key.Covered ? GlobalHandlers(roots) : [], Path(witness.Path, witness.Sites, [], example.Escape.ViaCandidate)));
            })
            .OrderBy(x => x.Key.Covered).ThenBy(x => x.Finding.Thrown.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Finding.Thrown.Evidence.SpanStart)
            .ThenBy(x => x.Key.SubsequentOrigin?.Value, StringComparer.Ordinal).Select(x => x.Finding),
    ];

    private ImmutableArray<CrashingException> FindCrashes() =>
    [
        .. ExceptionHandlingQuery.Crashing(program, Summary, token).Where(x => !Hidden(x.Origin, x.Example))
            .Select(x => new CrashingException(program.ThrowSite(x.Origin), Rethrow(x.SubsequentOrigin), x.ExceptionType, program.Callable(x.Method),
                [.. x.Roots.Select(program.Callable)], Path(x.Example))),
    ];

    private ImmutableArray<CaughtException> FindCaught() =>
    [
        .. ExceptionHandlingQuery.Caught(program, Summary, token).Where(x => !Hidden(x.Origin, x.Example))
            .Select(x => new CaughtException(program.ThrowSite(x.Origin), program.Catch(x.Catch), x.Certainty == CatchCertainty.Definite, Path(x.Example))),
    ];

    // Stable: findings keep the query's order within each half.
    private ImmutableArray<MiscaughtException> FindMiscaught() =>
    [
        .. ExceptionHandlingQuery.Miscaught(program, Summary, token).Where(x => !Hidden(x.Origin, x.Example)).Select(x =>
        {
            var region = program.Region(x.Missed.Region);
            var escapes = x.EscapesFrom.Select(program.Callable).ToImmutableArray();
            var covered = x.Outcomes.Contains(ExceptionStatus.MayEscape) && escapes.Length > 0 && escapes.All(e => !Handlers(e.Id).IsEmpty);
            return new MiscaughtException(program.ThrowSite(x.Origin), x.Missed.ExceptionType, region, [.. region.Catches.Select(program.Catch)], x.Outcomes,
                escapes, covered ? GlobalHandlers(escapes) : [], Path(x.Example))
            {
                Undetermined = x.Undetermined is { } u ? new NotFollowed(u.ReasonCode, u.Evidence.IsEmpty ? [] : [u.Evidence[^1]]) : null,
            };
        }).OrderBy(x => !x.GlobalHandlers.IsEmpty),
    ];

    // One finding per call site. A catch anywhere on the way counts; global handlers do not, since the rule asks for a catch around the call.
    private ImmutableArray<UnguardedCall> FindUnguarded() =>
    [
        .. EntryPoints.SelectMany(root => Summary.Roots[root.Method.Id].Escapes.Where(x => Summary.ThirdPartyCalls.ContainsKey(x.Origin)).Select(escape => (Root: root.Method, Escape: escape)))
            .GroupBy(x => x.Escape.Origin).Select(group =>
            {
                var example = group.OrderBy(x => x.Escape.ViaCandidate).First();
                var witness = Summary.GetWitness(example.Root.Id, example.Escape);
                return new UnguardedCall(program.CallSite(Summary.ThirdPartyCalls[group.Key]), [.. group.Select(x => x.Root).Distinct()],
                    Path(witness.Path, witness.Sites, [], example.Escape.ViaCandidate));
            })
            .OrderBy(x => x.Call.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Call.Evidence.SpanStart),
    ];

    // Reasons that are outcomes or limits rather than places the analysis stopped are left out; limits are BudgetExhausted.
    private static readonly ImmutableHashSet<ExceptionReason> NotPlaces = [ExceptionReason.Unspecified, ExceptionReason.InterceptedByHandler, ExceptionReason.MayEscape,
        ExceptionReason.ChangedMayEscape, ExceptionReason.AsyncVoidEscape, ExceptionReason.WorkLimit, ExceptionReason.FactLimit];

    private ImmutableArray<NotFollowed> FindNotFollowed() =>
    [
        .. EntryPoints.SelectMany(x => Summary.Roots[x.Method.Id].Unknowns).Where(x => !NotPlaces.Contains(x.ReasonCode))
            .Select(x => (x.ReasonCode, Evidence: x.Site is { } s ? program.CallSite(s).Evidence : x.Evidence)).Where(x => x.Evidence is not null)
            .GroupBy(x => x.ReasonCode)
            .Select(g => new NotFollowed(g.Key, [.. g.Select(x => x.Evidence!).DistinctBy(x => (x.Path, x.SpanStart)).OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.SpanStart)]))
            .OrderByDescending(x => x.Places.Length).ThenBy(x => x.Reason),
    ];

    // Exception middleware and MVC filters reach controller actions only.
    private ImmutableArray<BoundaryHandlingFact> Handlers(CallableId root) => handlers.TryGetValue(root, out var found) ? found
        : handlers[root] = EntryPoints.Any(x => x.Method.Id == root && x.Kind == EntryPointKind.ControllerAction) ? AspNetConventions.GlobalHandlers(program, root) : [];

    private ImmutableArray<BoundaryHandlingFact> GlobalHandlers(IEnumerable<CallableFact> roots) =>
        [.. roots.SelectMany(x => Handlers(x.Id)).Distinct().OrderBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart)];

    private ThrowSiteFact? Rethrow(ThrowSiteId? at) => at is { } id && program.TryGetThrowSite(id, out var site) ? site : null;

    // A path that reaches a fake or test class only through an assumed implementation is a guess about what DI picks.
    private bool Hidden(ThrowSiteId origin, ExceptionWitness witness) =>
        witness.ViaCandidate && (IsTestDouble(program.ThrowSite(origin).Owner) || witness.Path.Any(IsTestDouble));

    private static readonly string[] TestMarkers = ["Fake", "Mock", "Stub", "InMemory", "Dummy", ".Tests", "UnitTests", "IntegrationTests"];

    // Fakes, mocks, stubs, in-memory doubles and test projects, judged by type and project names.
    private bool IsTestDouble(CallableId id)
    {
        var haystack = (program.DeclaringType(id)?.DisplayName ?? "") + "|" + program.ProjectName(program.Callable(id).Compilation);
        return TestMarkers.Any(x => haystack.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private ExceptionPath Path(ExceptionWitness witness) => Path(witness.Path, witness.Sites, witness.Evidence, witness.ViaCandidate);

    private ExceptionPath Path(ImmutableArray<CallableId> path, ImmutableArray<CallSiteId> sites, ImmutableArray<SourceEvidence> evidence, bool assumed)
    {
        var calls = sites.Select(program.CallSite).ToArray();
        var steps = path.Select((x, i) => i == 0 || i - 1 >= calls.Length ? new PathStep(program.Callable(x))
            : new PathStep(program.Callable(x), calls[i - 1], program.CallGraph.Resolve(sites[i - 1]) is { Kind: CallTargetKind.Dispatch } target ? target.Callees.Length : 0));
        return new([.. steps], evidence.IsEmpty ? [.. calls.Select(x => x.Evidence)] : evidence, assumed);
    }
}
