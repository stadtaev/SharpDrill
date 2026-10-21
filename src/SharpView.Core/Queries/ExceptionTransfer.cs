using System.Collections.Immutable;

namespace SharpView.Core;

// Shared transfer semantics for detailed witnesses and fixed-point summaries.
internal sealed record ExceptionFlow(ThrowSiteId Origin, ThrowSiteId? SubsequentOrigin,
    TypeUse? StaticType, TypeUse? ExactType, bool Exact, bool Changed,
    ImmutableList<SourceEvidence> Evidence, ImmutableList<CatchId> Handlers)
{
    public TypeUse? Type => ExactType ?? StaticType;
    // The flow crossed an implementation-candidate step of an interface/virtual call (a possibility, not a proof).
    public bool ViaCandidate { get; init; }
    // Protected regions crossed whose catch clauses definitely do not match; collected with evidence only.
    public ImmutableList<MissedRegion> Missed { get; init; } = ImmutableList<MissedRegion>.Empty;
    // Handlers that may or may not take the exception (a filter, or a static type that does not decide the match); the
    // exception is followed past them. Collected with evidence only.
    public ImmutableList<CatchId> Possible { get; init; } = ImmutableList<CatchId>.Empty;
}

internal sealed record ExceptionTransferResult(ExceptionFlow Flow, ExceptionStatus? Status = null,
    ExceptionReason Reason = ExceptionReason.Unspecified, string Message = "");

internal static class ExceptionTransfer
{
    internal static ExceptionTransferResult Start(ProgramIndex program, ThrowSiteFact origin, CancellationToken token,
        bool collectEvidence = true)
    {
        var exact = origin.ExactExceptionType ?? (origin.IsExactType ? origin.StaticExceptionType : null);
        var flow = new ExceptionFlow(origin.Id, null, origin.StaticExceptionType, exact,
            origin.IsExactType || origin.ExactExceptionType is not null, false,
            collectEvidence ? ImmutableList<SourceEvidence>.Empty.Add(origin.Evidence) : ImmutableList<SourceEvidence>.Empty,
            ImmutableList<CatchId>.Empty);
        if (program.Callable(origin.Owner).IsAsync && !program.Callable(origin.Owner).ReturnsTask)
            return Unknown(flow, ExceptionReason.AsyncUnsupported, "Async void or custom awaitable propagation is outside the supported model.");
        return ApplyRegions(program, flow, origin.ProtectedRegions, token, collectEvidence);
    }

    internal static ExceptionTransferResult Cross(ProgramIndex program, ExceptionFlow flow, CallSiteFact site, CancellationToken token,
        bool collectEvidence = true)
    {
        if (collectEvidence) flow = flow with { Evidence = flow.Evidence.Add(site.Evidence) };
        var caller = program.Callable(site.Caller);
        if (caller.IsAsync && !caller.ReturnsTask)
            return Unknown(flow, ExceptionReason.AsyncUnsupported, "Async void or custom awaitable propagation is outside the supported model.");
        if (site.BoundTarget is { } target && program.ContainsCallable(target))
        {
            var callee = program.Callable(target);
            if ((callee.IsAsync || site.IsAwaited) && !callee.ReturnsTask)
                return Unknown(flow, ExceptionReason.AsyncUnsupported, "Async void or custom awaitable propagation is outside the supported model.");
            if (callee.ReturnsTask && !site.IsAwaited)
                return Unknown(flow, ExceptionReason.UnobservedTask, "Task-producing call is not directly awaited; exception observation/propagation unsupported.");
        }
        return ApplyRegions(program, flow, site.ProtectedRegions, token, collectEvidence);
    }

    internal static ExceptionTransferResult ApplyRegions(ProgramIndex program, ExceptionFlow flow,
        ImmutableArray<ExceptionRegionId> protectedRegions, CancellationToken token, bool collectEvidence = true)
    {
        foreach (var regionId in protectedRegions)
        {
            token.ThrowIfCancellationRequested();
            var region = program.Region(regionId);
            if (region.FinallyMayThrow)
                return Unknown(flow, ExceptionReason.FinallyUnsupported, "Finally may throw and replace the propagating exception.");
            var matched = false;
            foreach (var catchId in region.Catches)
            {
                var handler = program.Catch(catchId);
                var match = Matches(program.Types, flow.Type, flow.Exact, handler.CaughtType, token);
                if (match == false) continue;
                matched = true;
                if (collectEvidence)
                    flow = flow with { Evidence = flow.Evidence.Add(handler.Evidence), Handlers = flow.Handlers.Add(handler.Id) };
                // A filter or an undecided type match may take the exception or let it pass: follow the case where it passes.
                if (match is null || handler.Filter is not null)
                {
                    if (collectEvidence) flow = flow with { Possible = flow.Possible.Add(handler.Id) };
                    continue;
                }
                switch (handler.Outcome)
                {
                    case HandlerOutcome.Intercepts:
                        return new(flow, ExceptionStatus.Intercepted, ExceptionReason.InterceptedByHandler,
                            "Intercepted by a matching handler; successful recovery is not proven.");
                    case HandlerOutcome.Unknown:
                        return Unknown(flow, ExceptionReason.HandlerFlowUnsupported,
                            "Matching handler has unsupported control flow or calls; propagation/recovery unknown.");
                    // A handler that rethrows on some paths only may still let the exception continue.
                    case HandlerOutcome.Rethrows or HandlerOutcome.MayRethrow:
                        flow = flow with { Changed = true };
                        if (handler.SubsequentThrow is { } rethrow)
                            flow = flow with { SubsequentOrigin = rethrow,
                                Evidence = collectEvidence ? flow.Evidence.Add(program.ThrowSite(rethrow).Evidence) : flow.Evidence };
                        break;
                    // The original exception ends here; the replacement throw is an origin of its own and propagates from the handler.
                    case HandlerOutcome.Replaces:
                        return new(flow, ExceptionStatus.Intercepted, ExceptionReason.InterceptedByHandler, "Replaced by a handler; the replacement is reported as its own origin.");
                }
                break;
            }
            if (!matched && !region.Catches.IsEmpty && collectEvidence)
                flow = flow with { Missed = flow.Missed.Add(new(region.Id, flow.Type?.DisplayName)) };
        }
        return new(flow);
    }

    private static ExceptionTransferResult Unknown(ExceptionFlow flow, ExceptionReason reason, string message) =>
        new(flow, ExceptionStatus.Unknown, reason, message);

    // A base-typed thrown expression may hold a subtype: an incompatible narrow catch is then uncertain.
    private static bool? Matches(IReadOnlyDictionary<TypeId, TypeFact> types, TypeUse? thrown, bool exact, TypeUse? caught, CancellationToken token)
    {
        if (caught is null) return true;
        if (thrown is null || thrown.Declaration is null || caught.Declaration is null) return null;
        bool? IsA(TypeUse value, TypeUse target)
        {
            var incomplete = false;
            var pending = new Stack<TypeUse>(); pending.Push(value); var seen = new HashSet<TypeId>();
            while (pending.TryPop(out var t))
            {
                token.ThrowIfCancellationRequested();
                // Exception types are classes, so generic variance never applies: other type arguments never match.
                if (t.Declaration is not null && t.Declaration == target.Declaration)
                    return t.DisplayName == target.DisplayName
                        && t.Components.Select(x => x.DisplayName).SequenceEqual(target.Components.Select(x => x.DisplayName), StringComparer.Ordinal)
                        ? true : false;
                if (t.Declaration is not { } id) { incomplete = true; continue; }
                if (!seen.Add(id)) continue;
                var fact = types.GetValueOrDefault(id);
                if (fact is null) incomplete = true;
                else foreach (var b in fact.BaseTypes) pending.Push(b);
            }
            return incomplete ? null : false;
        }
        var assignable = IsA(thrown, caught);
        if (assignable != false) return assignable;
        if (!exact && IsA(caught, thrown) != false) return null;
        return false;
    }
}
