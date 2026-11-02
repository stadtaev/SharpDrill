using System.Collections.Immutable;
namespace SharpView.Core;

// EscapesAsyncVoid: it leaves an async void method, where no caller can catch it and the process ends.
public enum ExceptionStatus { Intercepted, RethrownOrReplaced, MayEscape, Unknown, EscapesAsyncVoid }
// Machine-readable counterpart of ExceptionWitness.Reason and ExceptionUncertainty: one code per distinct reason the
// analysis emits. After a rethrow/replacement the code explains the subsequent status (SubsequentStatus ?? Status).
public enum ExceptionReason
{
    Unspecified, InterceptedByHandler, MayEscape, ChangedMayEscape,
    MissingBody, UnresolvedTarget, DispatchBoundary, ExtractionGap,
    AsyncUnsupported, UnobservedTask, FinallyUnsupported, HandlerFlowUnsupported, UnboundExceptionType,
    WorkLimit, FactLimit, AsyncVoidEscape
}

// One origin's story along one example path from an action (Path[0]) down to the throw, re-walked with evidence:
// the handlers it met, the try regions it passed and where it ended.
public sealed record ExceptionWitness(CallableId Action, ThrowSiteId? Origin, ThrowSiteId? SubsequentOrigin,
    string? ExceptionType, bool IsExactType, ImmutableArray<CallableId> Path,
    ExceptionStatus Status, string Reason, ImmutableArray<SourceEvidence> Evidence,
    ImmutableArray<CatchId> HandlerIds, ExceptionStatus? SubsequentStatus = null,
    string? StaticExceptionType = null, string? ExactExceptionType = null,
    ExceptionReason ReasonCode = ExceptionReason.Unspecified)
{
    // try/catch blocks the exception passed through because no catch clause matched its type, innermost first.
    public ImmutableArray<MissedRegion> MissedRegions { get; init; } = [];
    // The call sites crossed from the action to the origin, one per step of Path (Path has one more element).
    public ImmutableArray<CallSiteId> Sites { get; init; } = [];
    // True when some step followed an implementation candidate of an interface/virtual call: a possibility, not a
    // proven runtime path.
    public bool ViaCandidate { get; init; }
    // Handlers in HandlerIds that may or may not take the exception (filter or undecided type match); it was followed past them.
    public ImmutableArray<CatchId> PossibleHandlers { get; init; } = [];
}

// A protected region with catch clauses, none of which matches ExceptionType (the type at that point of propagation).
public sealed record MissedRegion(ExceptionRegionId Region, string? ExceptionType);
