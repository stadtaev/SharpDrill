using System.Collections.Immutable;
namespace Sharpview.Core;

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
}
// A protected region with catch clauses, none of which matches ExceptionType (the type at that point of propagation).
public sealed record MissedRegion(ExceptionRegionId Region, string? ExceptionType);
public sealed record ExceptionSearchResult(ImmutableArray<ExceptionWitness> Results, bool OutputLimitReached,
    bool WorkLimitReached, bool DepthLimitReached, int ExpandedSites)
{
    // Ownerless gaps are snapshot-level caveats, not evidence of a path from each action.
    public ImmutableArray<AnalysisGap> GlobalGaps { get; init; } = [];
    public bool IsComplete => GlobalGaps.IsEmpty && !OutputLimitReached && !WorkLimitReached && !DepthLimitReached
        && Results.All(x => x.Status != ExceptionStatus.Unknown && x.SubsequentStatus != ExceptionStatus.Unknown);
}

// Bounds for detailed witness enumeration: call transitions, reported witnesses and examined call sites.
public sealed record ExceptionOptions(int MaxDepth = 20, int MaxResults = 1000, int MaxExpansions = 100000)
{
    public void Validate()
    {
        if (MaxDepth < 0 || MaxResults < 1 || MaxExpansions < 1) throw new ArgumentException("Depth must be nonnegative; work and output limits must be positive.");
    }
}

public static class ExceptionQuery
{
    public static ExceptionSearchResult Analyze(ProgramSnapshot snapshot, ImmutableArray<CallableId> actions,
        ExceptionOptions options, CancellationToken cancellationToken = default) => Analyze(new ProgramIndex(snapshot), actions, options, cancellationToken);
    public static ExceptionSearchResult Analyze(ProgramIndex program, ImmutableArray<CallableId> actions,
        ExceptionOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        foreach (var action in actions) if (!program.ContainsCallable(action)) throw new ArgumentException($"Unknown root: {action.Value}");
        var results = ImmutableArray.CreateBuilder<ExceptionWitness>();
        var stack = new Stack<(CallableId Root, ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites)>();
        foreach (var action in actions.Distinct().OrderByDescending(x => program.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenByDescending(x => x.Value, StringComparer.Ordinal)) stack.Push((action, [action], []));
        var work = 0; var outputLimit = false; var workLimit = false; var depthLimit = false;
        bool Add(ExceptionWitness witness)
        {
            if (results.Count >= options.MaxResults) { outputLimit = true; return false; }
            results.Add(witness); return true;
        }
        ExceptionWitness Unknown(CallableId root, ImmutableArray<CallableId> path, ExceptionReason code, string reason, SourceEvidence? evidence, DispatchKind? dispatch = null) =>
            new(root, null, null, null, false, path, ExceptionStatus.Unknown, reason,
                evidence is null ? [] : [evidence], [], ReasonCode: code, Dispatch: dispatch);
        while (!outputLimit && stack.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = frame.Path[^1];
            if (program.Callable(current).Body != BodyStatus.Source)
            {
                if (!Add(Unknown(frame.Root, frame.Path, ExceptionReason.MissingBody, "Missing body: explicit origins unknown.", program.Callable(current).Declarations.FirstOrDefault()))) break;
                continue;
            }
            foreach (var origin in program.ThrowSites(current).Where(x => x.Kind == ThrowKind.Explicit))
            {
                if (!Add(Unwind(frame.Root, frame.Path, frame.Sites, origin, program, cancellationToken))) break;
            }
            if (outputLimit) break;
            foreach (var gap in program.Gaps(current))
                if (!Add(Unknown(frame.Root, frame.Path, ExceptionReason.ExtractionGap, gap.Message, gap.Evidence))) break;
            if (outputLimit) break;
            var outgoing = program.Outgoing(current);
            if (frame.Sites.Length >= options.MaxDepth && outgoing.Length > 0)
            {
                depthLimit = true;
                if (!Add(Unknown(frame.Root, frame.Path, ExceptionReason.DepthLimit, "Search depth limit; further origins unknown.", program.Callable(current).Declarations.FirstOrDefault()))) break;
                continue;
            }
            var descend = new List<(CallableId Root, ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites)>();
            foreach (var site in outgoing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work >= options.MaxExpansions) { workLimit = true; break; }
                work++;
                var path = site.BoundTarget is { } target ? frame.Path.Add(target) : frame.Path;
                (ExceptionReason Code, string Text)? reason = site.Dispatch != DispatchKind.Direct ? (ExceptionReason.DispatchBoundary, $"{site.Dispatch} dispatch: declared binding only; runtime origins unknown.")
                    : site.BoundTarget is null || !program.ContainsCallable(site.BoundTarget.Value) ? (ExceptionReason.UnresolvedTarget, "Unresolved call target.")
                    : program.Callable(site.BoundTarget.Value).Body != BodyStatus.Source ? (ExceptionReason.MissingBody, "Missing body: explicit origins unknown.")
                    : frame.Path.Contains(site.BoundTarget.Value) ? (ExceptionReason.Cycle, "Cycle: recursive propagation unknown.") : null;
                if (reason is { } r)
                {
                    if (!Add(Unknown(frame.Root, path, r.Code, r.Text, site.Evidence, r.Code == ExceptionReason.DispatchBoundary ? site.Dispatch : null))) break;
                }
                else descend.Add((frame.Root, path, frame.Sites.Add(site)));
            }
            if (workLimit || outputLimit) break;
            for (var i = descend.Count - 1; i >= 0; i--) stack.Push(descend[i]);
        }
        return new(results.ToImmutable(), outputLimit, workLimit, depthLimit, work)
        { GlobalGaps = [.. program.Snapshot.Gaps.Where(x => x.Owner is null)] };
    }

    private static ExceptionWitness Unwind(CallableId root, ImmutableArray<CallableId> path,
        ImmutableArray<CallSiteFact> sites, ThrowSiteFact origin, ProgramIndex program, CancellationToken token)
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
        { MissedRegions = [.. flow.Missed] };
    }
}
