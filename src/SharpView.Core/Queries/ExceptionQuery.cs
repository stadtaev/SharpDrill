using System.Collections.Immutable;
namespace SharpView.Core;

public enum ExceptionStatus { Intercepted, RethrownOrReplaced, MayEscape, Unknown }
// Machine-readable counterpart of ExceptionWitness.Reason: one code per distinct reason the analysis emits.
// After a rethrow/replacement the code explains the subsequent status (SubsequentStatus ?? Status).
public enum ExceptionReason
{
    Unspecified, InterceptedByHandler, MayEscape, ChangedMayEscape,
    MissingBody, UnresolvedTarget, DispatchBoundary, Cycle, DepthLimit, ExtractionGap,
    OriginInHandler, AsyncUnsupported, UnobservedTask, FinallyUnsupported, HandlerTypeUncertain,
    CatchFilter, HandlerFlowUnsupported, ReplacementUnavailable, UnboundExceptionType,
    WorkLimit, FactLimit
}
public sealed record ExceptionWitness(CallableId Action, ThrowSiteId? Origin, ThrowSiteId? SubsequentOrigin,
    string? ExceptionType, bool IsExactType, ImmutableArray<CallableId> Path,
    ExceptionStatus Status, string Reason, ImmutableArray<SourceEvidence> Evidence,
    ImmutableArray<CatchId> HandlerIds, ExceptionStatus? SubsequentStatus = null,
    string? StaticExceptionType = null, string? ExactExceptionType = null,
    ExceptionReason ReasonCode = ExceptionReason.Unspecified, DispatchKind? Dispatch = null)
{
    // try/catch blocks the exception passed through because no catch clause matched its type, innermost first.
    public ImmutableArray<MissedRegion> MissedRegions { get; init; } = [];
    // The call sites crossed from the action to the origin, one per step of Path (Path has one more element).
    public ImmutableArray<CallSiteId> Sites { get; init; } = [];
    // True when some step followed an implementation candidate of an interface/virtual call: a possibility, not a
    // proven runtime path.
    public bool ViaCandidate { get; init; }
}
// A protected region with catch clauses, none of which matches ExceptionType (the type at that point of propagation).
public sealed record MissedRegion(ExceptionRegionId Region, string? ExceptionType);
public sealed record ExceptionSearchResult(ImmutableArray<ExceptionWitness> Results, bool OutputLimitReached,
    bool WorkLimitReached, bool DepthLimitReached, int ExpandedSites)
{
    // Ownerless gaps are snapshot-level caveats, not evidence of a path from each action.
    public ImmutableArray<AnalysisGap> GlobalGaps { get; init; } = [];
    // Uncertainty witnesses beyond their own budget were dropped (they never crowd out origin witnesses).
    public bool UnknownLimitReached { get; init; }
    // Library methods (no source body) called along the examined paths. Their exceptions are not modelled.
    public ImmutableArray<CallableId> LibraryCalls { get; init; } = [];
    public bool IsComplete => GlobalGaps.IsEmpty && !OutputLimitReached && !UnknownLimitReached && !WorkLimitReached && !DepthLimitReached
        && Results.All(x => x.Status != ExceptionStatus.Unknown && x.SubsequentStatus != ExceptionStatus.Unknown);
}

// Bounds for detailed witness enumeration: call transitions, reported witnesses and examined call sites.
// MaxResults bounds origin witnesses and, separately, uncertainty witnesses. FollowCandidates continues through
// interface/virtual calls into their source implementation candidates (marked ViaCandidate).
public sealed record ExceptionOptions(int MaxDepth = 20, int MaxResults = 1000, int MaxExpansions = 100000, bool FollowCandidates = false)
{
    public void Validate()
    {
        if (MaxDepth < 0 || MaxResults < 1 || MaxExpansions < 1) throw new ArgumentException("Depth must be nonnegative; work and output limits must be positive.");
    }
}

public static class ExceptionQuery
{
    private readonly record struct Frame(CallableId Root, ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites, bool Via);

    public static ExceptionSearchResult Analyze(ProgramSnapshot snapshot, ImmutableArray<CallableId> actions,
        ExceptionOptions options, CancellationToken cancellationToken = default) => Analyze(new ProgramIndex(snapshot), actions, options, cancellationToken);
    public static ExceptionSearchResult Analyze(ProgramIndex program, ImmutableArray<CallableId> actions,
        ExceptionOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        foreach (var action in actions) if (!program.ContainsCallable(action)) throw new ArgumentException($"Unknown root: {action.Value}");
        var results = ImmutableArray.CreateBuilder<ExceptionWitness>();
        var calls = program.GetCallGraph(cancellationToken);
        var stack = new Stack<Frame>();
        foreach (var action in actions.Distinct().OrderByDescending(x => program.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenByDescending(x => x.Value, StringComparer.Ordinal))
            stack.Push(new(action, [action], [], false));
        var work = 0; var outputLimit = false; var workLimit = false; var depthLimit = false;
        var origins = 0; var uncertainties = 0; var unknownLimit = false;
        var seenUncertainty = new HashSet<(CallableId, ExceptionReason, string, string)>();
        var library = new HashSet<CallableId>();
        // Origin witnesses stop the search at the budget. Uncertainty witnesses are deduplicated per action and site
        // and have their own budget, so a flood of boundaries can never hide the real throws behind them.
        bool Add(ExceptionWitness witness)
        {
            if (witness.Origin is null)
            {
                var at = witness.Evidence.FirstOrDefault() is { } e ? $"{e.Path}:{e.SpanStart}" : "";
                if (!seenUncertainty.Add((witness.Action, witness.ReasonCode, at, witness.Path[^1].Value))) return true;
                if (uncertainties >= options.MaxResults) { unknownLimit = true; return true; }
                uncertainties++; results.Add(witness); return true;
            }
            if (origins >= options.MaxResults) { outputLimit = true; return false; }
            origins++; results.Add(witness); return true;
        }
        ExceptionWitness Unknown(Frame frame, ImmutableArray<CallableId> path, bool via, ExceptionReason code, string reason,
            SourceEvidence? evidence, DispatchKind? dispatch = null) =>
            new(frame.Root, null, null, null, false, path, ExceptionStatus.Unknown, reason,
                evidence is null ? [] : [evidence], [], ReasonCode: code, Dispatch: dispatch) { ViaCandidate = via };
        while (!outputLimit && stack.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = frame.Path[^1];
            var callable = program.Callable(current);
            // Compiler-provided members hold no explicit code: nothing to report, nothing to follow.
            if (callable.Body == BodyStatus.Synthesized) continue;
            if (callable.Body != BodyStatus.Source)
            {
                if (!Add(Unknown(frame, frame.Path, frame.Via, ExceptionReason.MissingBody, "Missing body: explicit origins unknown.", callable.Declarations.FirstOrDefault()))) break;
                continue;
            }
            foreach (var origin in program.ThrowSites(current).Where(x => x.Kind == ThrowKind.Explicit))
            {
                if (!Add(Unwind(frame.Root, frame.Path, frame.Sites, frame.Via, origin, program, cancellationToken))) break;
            }
            if (outputLimit) break;
            foreach (var gap in program.Gaps(current))
                if (!Add(Unknown(frame, frame.Path, frame.Via, ExceptionReason.ExtractionGap, gap.Message, gap.Evidence))) break;
            if (outputLimit) break;
            var outgoing = program.Outgoing(current);
            if (frame.Sites.Length >= options.MaxDepth && outgoing.Length > 0)
            {
                depthLimit = true;
                if (!Add(Unknown(frame, frame.Path, frame.Via, ExceptionReason.DepthLimit, "Search depth limit; further origins unknown.", callable.Declarations.FirstOrDefault()))) break;
                continue;
            }
            var descend = new List<Frame>();
            foreach (var site in outgoing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work >= options.MaxExpansions) { workLimit = true; break; }
                work++;
                CallableId? target = site.BoundTarget is { } bound && program.ContainsCallable(bound) ? bound : null;
                var path = site.BoundTarget is { } declared ? frame.Path.Add(declared) : frame.Path;
                if (site.Dispatch != DispatchKind.Direct)
                {
                    var candidates = calls.CandidatesOf(site.Id);
                    // A virtual/interface call to a library member that no source type overrides: a library call.
                    if (target is { } libraryTarget && program.Callable(libraryTarget).Body == BodyStatus.MetadataOnly && candidates.IsEmpty)
                    { library.Add(libraryTarget); continue; }
                    if (!options.FollowCandidates)
                    {
                        if (!Add(Unknown(frame, path, frame.Via, ExceptionReason.DispatchBoundary, $"{site.Dispatch} dispatch: declared binding only; runtime origins unknown.", site.Evidence, site.Dispatch))) break;
                    }
                    else if (candidates.IsEmpty)
                    {
                        if (!Add(Unknown(frame, path, frame.Via, ExceptionReason.DispatchBoundary, $"{site.Dispatch} dispatch: no source implementation found; runtime origins unknown.", site.Evidence, site.Dispatch))) break;
                    }
                    else
                        foreach (var candidate in candidates)
                        {
                            var next = frame.Path.Add(candidate);
                            if (frame.Path.Contains(candidate))
                            { if (!Add(Unknown(frame, next, true, ExceptionReason.Cycle, "Cycle: recursive propagation unknown.", site.Evidence))) break; }
                            else descend.Add(new(frame.Root, next, frame.Sites.Add(site), true));
                        }
                    if (outputLimit) break;
                    continue;
                }
                if (target is not { } callee)
                { if (!Add(Unknown(frame, path, frame.Via, ExceptionReason.UnresolvedTarget, "Unresolved call target.", site.Evidence))) break; }
                else if (program.Callable(callee).Body == BodyStatus.MetadataOnly) library.Add(callee);
                else if (!program.Callable(callee).HasBody)
                { if (!Add(Unknown(frame, path, frame.Via, ExceptionReason.MissingBody, "Missing body: explicit origins unknown.", site.Evidence))) break; }
                else if (frame.Path.Contains(callee))
                { if (!Add(Unknown(frame, path, frame.Via, ExceptionReason.Cycle, "Cycle: recursive propagation unknown.", site.Evidence))) break; }
                else descend.Add(new(frame.Root, path, frame.Sites.Add(site), frame.Via));
            }
            if (workLimit || outputLimit) break;
            for (var i = descend.Count - 1; i >= 0; i--) stack.Push(descend[i]);
        }
        return new(results.ToImmutable(), outputLimit, workLimit, depthLimit, work)
        {
            GlobalGaps = [.. program.Snapshot.Gaps.Where(x => x.Owner is null)],
            UnknownLimitReached = unknownLimit,
            LibraryCalls = [.. library.OrderBy(x => program.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)],
        };
    }

    private static ExceptionWitness Unwind(CallableId root, ImmutableArray<CallableId> path,
        ImmutableArray<CallSiteFact> sites, bool via, ThrowSiteFact origin, ProgramIndex program, CancellationToken token)
    {
        var current = ExceptionTransfer.Start(program, origin, token);
        for (var i = sites.Length - 1; current.Status is null && i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            current = ExceptionTransfer.Cross(program, current.Flow, sites[i], token);
        }
        var flow = current.Flow;
        var status = current.Status ?? (flow.Type is null ? ExceptionStatus.Unknown : ExceptionStatus.MayEscape);
        var reason = current.Status is not null ? current.Reason
            : flow.Type is null ? ExceptionReason.UnboundExceptionType
            : flow.Changed ? ExceptionReason.ChangedMayEscape : ExceptionReason.MayEscape;
        var message = current.Status is not null ? current.Message
            : flow.Type is null ? "Exception type could not be bound."
            : flow.Changed ? "Rethrown or replaced; subsequent exception may escape the controller action under the supported model."
            : "May escape the controller action under the supported analysis model.";
        return new(root, flow.Origin, flow.SubsequentOrigin, flow.Type?.DisplayName, flow.Exact, path,
            flow.Changed ? ExceptionStatus.RethrownOrReplaced : status, message,
            flow.Evidence.ToImmutableArray(), flow.Handlers.ToImmutableArray(),
            flow.Changed ? status : null, flow.StaticType?.DisplayName, flow.ExactType?.DisplayName, reason)
        { MissedRegions = [.. flow.Missed], Sites = [.. sites.Select(x => x.Id)], ViaCandidate = via };
    }
}
