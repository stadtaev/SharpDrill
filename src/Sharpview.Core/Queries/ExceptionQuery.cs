using System.Collections.Immutable;
namespace Sharpview.Core;

public enum ExceptionStatus { Intercepted, RethrownOrReplaced, MayEscape, Unknown }
public sealed record ExceptionWitness(CallableId Action, string? Origin, string? SubsequentOrigin,
    string? ExceptionType, bool IsExactType, ImmutableArray<CallableId> Path,
    ExceptionStatus Status, string Reason, ImmutableArray<SourceEvidence> Evidence,
    ImmutableArray<string> HandlerIds, ExceptionStatus? SubsequentStatus = null,
    string? StaticExceptionType = null, string? ExactExceptionType = null);
public sealed record ExceptionSearchResult(ImmutableArray<ExceptionWitness> Results, bool OutputLimitReached,
    bool WorkLimitReached, bool DepthLimitReached, int ExpandedSites);

public static class ExceptionQuery
{
    public static ExceptionSearchResult Analyze(ProgramSnapshot snapshot, ImmutableArray<CallableId> actions,
        ChainOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var methods = snapshot.Callables.ToDictionary(x => x.Id);
        foreach (var action in actions) if (!methods.ContainsKey(action)) throw new ArgumentException($"Unknown root: {action.Value}");
        var calls = snapshot.CallSites.GroupBy(x => x.Caller).ToDictionary(g => g.Key, g => g.OrderBy(x => x.TargetDisplay, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToArray());
        var origins = snapshot.ThrowSites.Where(x => x.Kind == ThrowKind.Explicit).GroupBy(x => x.Owner).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
        var regions = snapshot.ExceptionRegions.ToDictionary(x => x.Id);
        var catches = snapshot.Catches.ToDictionary(x => x.Id);
        var throws = snapshot.ThrowSites.ToDictionary(x => x.Id);
        var types = snapshot.Types.ToDictionary(x => x.Id);
        var gaps = snapshot.Gaps.Where(x => x.Owner is not null).GroupBy(x => x.Owner!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        var results = ImmutableArray.CreateBuilder<ExceptionWitness>();
        var stack = new Stack<(CallableId Root, ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites)>();
        foreach (var action in actions.Distinct().OrderByDescending(x => methods[x].DisplaySignature, StringComparer.Ordinal).ThenByDescending(x => x.Value, StringComparer.Ordinal)) stack.Push((action, [action], []));
        var work = 0; var outputLimit = false; var workLimit = false; var depthLimit = false;
        bool Add(ExceptionWitness witness)
        {
            if (results.Count >= options.MaxChains) { outputLimit = true; return false; }
            results.Add(witness); return true;
        }
        ExceptionWitness Unknown(CallableId root, ImmutableArray<CallableId> path, string reason, SourceEvidence? evidence) =>
            new(root, null, null, null, false, path, ExceptionStatus.Unknown, reason,
                evidence is null ? [] : [evidence], []);
        while (stack.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = frame.Path[^1];
            if (methods[current].Body != BodyStatus.Source)
            {
                if (!Add(Unknown(frame.Root, frame.Path, "Missing body: explicit origins unknown.", methods[current].Declarations.FirstOrDefault()))) break;
                continue;
            }
            foreach (var origin in origins.GetValueOrDefault(current, []))
            {
                if (!Add(Unwind(frame.Root, frame.Path, frame.Sites, origin, methods, regions, catches, throws, types, cancellationToken))) break;
            }
            if (outputLimit) break;
            foreach (var gap in gaps.GetValueOrDefault(current, []))
                if (!Add(Unknown(frame.Root, frame.Path, gap.Message, gap.Evidence))) break;
            if (outputLimit) break;
            var outgoing = calls.GetValueOrDefault(current, []);
            if (frame.Sites.Length >= options.MaxDepth && outgoing.Length > 0)
            {
                depthLimit = true;
                if (!Add(Unknown(frame.Root, frame.Path, "Search depth limit; further origins unknown.", methods[current].Declarations.FirstOrDefault()))) break;
                continue;
            }
            var descend = new List<(CallableId Root, ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites)>();
            foreach (var site in outgoing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work >= options.MaxExpansions) { workLimit = true; break; }
                work++;
                var path = site.BoundTarget is { } target ? frame.Path.Add(target) : frame.Path;
                string? reason = site.Dispatch != DispatchKind.Direct ? $"{site.Dispatch} dispatch: declared binding only; runtime origins unknown."
                    : site.BoundTarget is null || !methods.TryGetValue(site.BoundTarget.Value, out _) ? "Unresolved call target."
                    : methods[site.BoundTarget.Value].Body != BodyStatus.Source ? "Missing body: explicit origins unknown."
                    : frame.Path.Contains(site.BoundTarget.Value) ? "Cycle: recursive propagation unknown." : null;
                if (reason is not null)
                {
                    if (!Add(Unknown(frame.Root, path, reason, site.Evidence))) break;
                }
                else descend.Add((frame.Root, path, frame.Sites.Add(site)));
            }
            if (workLimit || outputLimit) break;
            for (var i = descend.Count - 1; i >= 0; i--) stack.Push(descend[i]);
        }
        return new(results.ToImmutable(), outputLimit, workLimit, depthLimit, work);
    }

    private static ExceptionWitness Unwind(CallableId root, ImmutableArray<CallableId> path,
        ImmutableArray<CallSiteFact> sites, ThrowSiteFact origin, Dictionary<CallableId, CallableFact> methods,
        Dictionary<string, ExceptionRegionFact> regions, Dictionary<string, CatchFact> catches,
        Dictionary<string, ThrowSiteFact> throws, Dictionary<TypeId, TypeFact> types, CancellationToken token)
    {
        var staticType = origin.StaticExceptionType;
        var exactType = origin.ExactExceptionType ?? (origin.IsExactType ? origin.StaticExceptionType : null);
        var type = exactType ?? staticType; var exact = origin.IsExactType || origin.ExactExceptionType is not null; string? subsequent = null;
        var evidence = ImmutableArray.CreateBuilder<SourceEvidence>(); evidence.Add(origin.Evidence);
        var handlers = ImmutableArray.CreateBuilder<string>();
        bool changed = false;
        ExceptionWitness Finish(ExceptionStatus status, string reason) => new(root, origin.Id, subsequent, type?.DisplayName, exact, path,
            changed ? ExceptionStatus.RethrownOrReplaced : status, reason, evidence.ToImmutable(), handlers.ToImmutable(), changed ? status : null, staticType?.DisplayName, exactType?.DisplayName);
        if (origin.CatchId is not null)
            return Finish(ExceptionStatus.Unknown, "Origin is inside a handler; reaching it depends on the caught exception and handler control flow.");
        if (methods[origin.Owner].IsAsync && !methods[origin.Owner].ReturnsTask)
            return Finish(ExceptionStatus.Unknown, "Async void or custom awaitable propagation is outside the supported model.");
        ExceptionWitness? Apply(ImmutableArray<string> protectedRegions)
        {
            foreach (var regionId in protectedRegions)
            {
                token.ThrowIfCancellationRequested();
                var region = regions[regionId];
                if (region.HasFinally) return Finish(ExceptionStatus.Unknown, "Finally may change propagation; its execution is outside the supported model.");
                foreach (var catchId in region.Catches)
                {
                    var handler = catches[catchId];
                    var match = Matches(types, type, exact, handler.CaughtType, token);
                    if (match == false) continue;
                    evidence.Add(handler.Evidence); handlers.Add(handler.Id);
                    if (match is null) return Finish(ExceptionStatus.Unknown, "Static exception type does not determine runtime handler matching.");
                    if (handler.Filter is not null) return Finish(ExceptionStatus.Unknown, "Conditional catch filter may accept, reject or throw.");
                    switch (handler.Outcome)
                    {
                        case HandlerOutcome.Intercepts:
                            return Finish(ExceptionStatus.Intercepted, "Intercepted by a matching handler; successful recovery is not proven.");
                        case HandlerOutcome.Unknown:
                            return Finish(ExceptionStatus.Unknown, "Matching handler has unsupported control flow or calls; propagation/recovery unknown.");
                        case HandlerOutcome.Rethrows:
                            changed = true;
                            if (handler.SubsequentThrow is { } rethrow) { subsequent = rethrow; evidence.Add(throws[rethrow].Evidence); }
                            break;
                        case HandlerOutcome.Replaces:
                            if (handler.SubsequentThrow is null) return Finish(ExceptionStatus.Unknown, "Replacement origin unavailable.");
                            var replacement = throws[handler.SubsequentThrow];
                            subsequent = replacement.Id;
                            staticType = replacement.StaticExceptionType;
                            exactType = replacement.ExactExceptionType ?? (replacement.IsExactType ? replacement.StaticExceptionType : null);
                            type = exactType ?? staticType; exact = replacement.IsExactType || replacement.ExactExceptionType is not null;
                            evidence.Add(replacement.Evidence); changed = true;
                            break;
                    }
                    // A selected catch is outside the same try's protected block: move to the next outer region.
                    break;
                }
            }
            return null;
        }
        var local = Apply(origin.ProtectedRegions);
        if (local is not null) return local;
        for (var i = sites.Length - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var site = sites[i]; evidence.Add(site.Evidence);
            if (methods[path[i]].IsAsync && !methods[path[i]].ReturnsTask)
                return Finish(ExceptionStatus.Unknown, "Async void or custom awaitable propagation is outside the supported model.");
            if ((methods[path[i + 1]].IsAsync || site.IsAwaited) && !methods[path[i + 1]].ReturnsTask)
                return Finish(ExceptionStatus.Unknown, "Async void or custom awaitable propagation is outside the supported model.");
            if (methods[path[i + 1]].ReturnsTask && !site.IsAwaited)
                return Finish(ExceptionStatus.Unknown, "Task-producing call is not directly awaited; exception observation/propagation unsupported.");
            var intercepted = Apply(site.ProtectedRegions);
            if (intercepted is not null) return intercepted;
        }
        if (type is null) return Finish(ExceptionStatus.Unknown, "Exception type could not be bound.");
        return Finish(ExceptionStatus.MayEscape, changed
            ? "Rethrown or replaced; subsequent exception may escape the controller action under the supported model."
            : "May escape the controller action under the supported analysis model.");
    }

    // A base-typed thrown expression may hold a subtype: an incompatible narrow catch is then uncertain.
    private static bool? Matches(Dictionary<TypeId, TypeFact> types, TypeUse? thrown, bool exact, TypeUse? caught, CancellationToken token)
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
                if (t.Declaration is not null && t.Declaration == target.Declaration)
                    return t.DisplayName == target.DisplayName
                        && t.Components.Select(x => x.DisplayName).SequenceEqual(target.Components.Select(x => x.DisplayName), StringComparer.Ordinal)
                        ? true : null;
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
