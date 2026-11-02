using System.Collections.Immutable;

namespace SharpDrill.Core;

// Definite: the catch type matches and no filter applies. Possible: a filter or the runtime type decides.
public enum CatchCertainty { Definite, Possible }

/// <summary>An origin reaching a matching catch clause; Example is one witness path from a root.</summary>
public sealed record CaughtException(ThrowSiteId Origin, CatchId Catch, CatchCertainty Certainty, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
}

/// <summary>An origin passing through a try/catch whose clauses do not match it; Outcomes are its final statuses
/// over every witness path (it may still be intercepted further up). Undetermined is a witness whose outcome is Unknown.
/// EscapesFrom are the roots of the witnesses where it may escape.</summary>
public sealed record MiscaughtException(ThrowSiteId Origin, MissedRegion Missed, ImmutableArray<ExceptionStatus> Outcomes, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
    public ExceptionWitness? Undetermined { get; init; }
    public ImmutableArray<CallableId> EscapesFrom { get; init; } = [];
}

/// <summary>An exception that leaves an async void method (Method) reached from roots: no caller can catch it, so it ends
/// the process. Roots are every root whose calls reach Method (Method itself when it is an async void root).</summary>
public sealed record CrashingException(ThrowSiteId Origin, ThrowSiteId? SubsequentOrigin, string? ExceptionType, CallableId Method,
    ImmutableArray<CallableId> Roots, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
}

/// <summary>Catches and misses read from the same propagation as --uncaught (ExceptionSummaryQuery), deduplicated across
/// roots and paths. Each transfer step records the handlers it matched and the try regions it passed; a miss's outcomes
/// are the fates of the fact it became, which depend only on that fact, not on the path that produced it. The example
/// is the shortest proven path from a root, else the shortest through an implementation candidate, re-walked with
/// evidence.</summary>
public static class ExceptionHandlingQuery
{
    private static bool Better(ExceptionWitness candidate, ExceptionWitness current) =>
        candidate.ViaCandidate != current.ViaCandidate ? !candidate.ViaCandidate : candidate.Path.Length < current.Path.Length;

    public static ImmutableArray<CaughtException> Caught(ProgramIndex index, ExceptionSummaryResult result, CancellationToken token = default)
    {
        var witnesses = new Witnesses(index, result, token);
        var found = new Dictionary<(ThrowSiteId, CatchId), CaughtException>();
        foreach (var step in result.Steps)
            foreach (var handler in step.Result.Flow.Handlers)
            {
                token.ThrowIfCancellationRequested();
                var certainty = step.Result.Flow.Possible.Contains(handler) ? CatchCertainty.Possible : CatchCertainty.Definite;
                if (witnesses.Of(step) is not { } example) continue;
                var key = (step.Result.Flow.Origin, handler);
                if (!found.TryGetValue(key, out var existing) || Better(example, existing.Example)) found[key] = new(key.Origin, handler, certainty, example);
            }
        return [.. found.Values.OrderBy(x => x.Example, witnesses.Order).ThenBy(x => index.Catch(x.Catch).Evidence.SpanStart)];
    }

    public static ImmutableArray<MiscaughtException> Miscaught(ProgramIndex index, ExceptionSummaryResult result, CancellationToken token = default)
    {
        var witnesses = new Witnesses(index, result, token);
        var fates = new Dictionary<ExceptionSummaryResult.FactRef, Fate>();
        var found = new Dictionary<(ThrowSiteId, ExceptionRegionId), (MissedRegion Missed, ExceptionWitness Example, Fate Fate)>();
        foreach (var step in result.Steps)
            foreach (var missed in step.Result.Flow.Missed)
            {
                token.ThrowIfCancellationRequested();
                if (witnesses.Of(step) is not { } example) continue;
                var fate = step.Result.Status switch
                {
                    ExceptionStatus.Intercepted => new Fate([ExceptionStatus.Intercepted], [], null),
                    ExceptionStatus.Unknown => new Fate([ExceptionStatus.Unknown], [], step),
                    ExceptionStatus.EscapesAsyncVoid => new Fate([ExceptionStatus.EscapesAsyncVoid], [], null),
                    _ => step.To is { } to ? fates.TryGetValue(to, out var known) ? known : fates[to] = FateOf(index, result, to) : new Fate([], [], null),
                };
                var key = (step.Result.Flow.Origin, missed.Region);
                if (!found.TryGetValue(key, out var existing)) found[key] = (missed, example, fate);
                else found[key] = (existing.Missed, Better(example, existing.Example) ? example : existing.Example, existing.Fate.With(fate));
            }
        return [.. found.Values.OrderBy(x => x.Example, witnesses.Order).ThenBy(x => index.Region(x.Missed.Region).ProtectedBlock.SpanStart)
            .Select(x => new MiscaughtException(x.Example.Origin!.Value, x.Missed, [.. x.Fate.Outcomes.Order()], x.Example)
            {
                Undetermined = x.Fate.Undetermined is { } unknown ? witnesses.Of(unknown) : null,
                EscapesFrom = [.. x.Fate.Roots.Order(StringComparer.Ordinal).Select(r => new CallableId(r))],
            })];
    }

    public static ImmutableArray<CrashingException> Crashing(ProgramIndex index, ExceptionSummaryResult result, CancellationToken token = default)
    {
        var witnesses = new Witnesses(index, result, token);
        var found = new Dictionary<(ThrowSiteId, ThrowSiteId?, string?, CallableId), ExceptionWitness>();
        void Add(ExceptionFlow flow, CallableId method, ExceptionWitness? example)
        {
            if (example is null) return;
            var key = (flow.Origin, flow.SubsequentOrigin, flow.Type?.DisplayName, method);
            if (!found.TryGetValue(key, out var existing) || Better(example, existing)) found[key] = example;
        }
        foreach (var step in result.Steps.Where(x => x.Result.Status == ExceptionStatus.EscapesAsyncVoid))
        {
            token.ThrowIfCancellationRequested();
            Add(step.Result.Flow, step.From!.Value.Method, witnesses.Of(step));
        }
        // An async void action has no caller in source: what leaves it is a root fact, not a crossing.
        foreach (var root in result.Roots.Keys.Where(x => index.Callable(x).IsAsyncVoid))
            foreach (var key in result.KeysAt(root).Where(x => !result.ThirdPartyCalls.ContainsKey(x.Origin)))
            {
                token.ThrowIfCancellationRequested();
                var path = result.Shortest(root, [key]);
                Add(result.Flow(new(root, key)), root, ExceptionTransfer.Witness(index, root, path.Path, [.. path.Sites.Select(index.CallSite)], key.ViaCandidate, index.ThrowSite(key.Origin), token));
            }
        return [.. found.OrderBy(x => x.Value, witnesses.Order).ThenBy(x => index.ThrowSite(x.Key.Item1).Evidence.SpanStart)
            .Select(x => new CrashingException(x.Key.Item1, x.Key.Item2, x.Key.Item3, x.Key.Item4, witnesses.RootsAbove(x.Key.Item4), x.Value))];
    }

    /// <summary>Where one throw ends: one example per handler that stops it, per place it becomes Unknown and per root it
    /// may escape, in the order of their actions.</summary>
    public static ImmutableArray<ExceptionWitness> Fates(ProgramIndex index, ExceptionSummaryResult result, ThrowSiteId origin, CancellationToken token = default)
    {
        var witnesses = new Witnesses(index, result, token);
        var stopped = result.Steps.Where(x => x.Result.Flow.Origin == origin && x.Result.Status is ExceptionStatus.Intercepted or ExceptionStatus.Unknown or ExceptionStatus.EscapesAsyncVoid)
            .Select(witnesses.Of).OfType<ExceptionWitness>();
        // Every fact of the origin at a root, typed or not (an unbound type ends Unknown there); a proven path first.
        var escaped = result.Roots.Keys.Select(root => (Root: root, Keys: result.KeysAt(root).Where(x => x.Origin == origin).OrderBy(x => x.ViaCandidate).ToArray()))
            .Where(x => x.Keys.Length > 0).Select(x =>
            {
                var path = result.Shortest(x.Root, x.Keys.Where(k => k.ViaCandidate == x.Keys[0].ViaCandidate));
                return ExceptionTransfer.Witness(index, x.Root, path.Path, [.. path.Sites.Select(index.CallSite)], x.Keys[0].ViaCandidate, index.ThrowSite(origin), token);
            });
        return [.. stopped.Concat(escaped).Order(witnesses.Order)];
    }

    // Final outcomes of an exception from some point on, the roots it may escape, and a step where it became Unknown.
    private sealed record Fate(ImmutableHashSet<ExceptionStatus> Outcomes, ImmutableHashSet<string> Roots, ExceptionSummaryResult.Step? Undetermined)
    {
        public Fate With(Fate other) => new(Outcomes.Union(other.Outcomes), Roots.Union(other.Roots), Undetermined ?? other.Undetermined);
    }

    // Every step reachable upward from the fact: a root it reaches lets it escape (Unknown when its type is unbound).
    private static Fate FateOf(ProgramIndex index, ExceptionSummaryResult result, ExceptionSummaryResult.FactRef start)
    {
        var (outcomes, roots) = (ImmutableHashSet.CreateBuilder<ExceptionStatus>(), ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal));
        ExceptionSummaryResult.Step? undetermined = null;
        var seen = new HashSet<ExceptionSummaryResult.FactRef> { start };
        var pending = new Queue<ExceptionSummaryResult.FactRef>([start]);
        while (pending.TryDequeue(out var fact))
        {
            if (result.Roots.ContainsKey(fact.Method))
            {
                if (index.Callable(fact.Method).IsAsyncVoid) outcomes.Add(ExceptionStatus.EscapesAsyncVoid);
                else outcomes.Add(result.Flow(fact).Type is null ? ExceptionStatus.Unknown : ExceptionStatus.MayEscape);
                if (result.Flow(fact).Type is not null && !index.Callable(fact.Method).IsAsyncVoid) roots.Add(fact.Method.Value);
            }
            foreach (var step in result.StepsFrom[fact])
                switch (step.Result.Status)
                {
                    case ExceptionStatus.Intercepted: outcomes.Add(ExceptionStatus.Intercepted); break;
                    case ExceptionStatus.Unknown: outcomes.Add(ExceptionStatus.Unknown); undetermined ??= step; break;
                    case ExceptionStatus.EscapesAsyncVoid: outcomes.Add(ExceptionStatus.EscapesAsyncVoid); break;
                    default: if (step.To is { } to && seen.Add(to)) pending.Enqueue(to); break;
                }
        }
        return new(outcomes.ToImmutable(), roots.ToImmutable(), undetermined);
    }

    // Example paths for steps: the shortest path from a root down to the step's method, then the provenance of the fact
    // that crossed it down to the throw. Proven callers are tried before implementation candidates.
    private sealed class Witnesses(ProgramIndex index, ExceptionSummaryResult result, CancellationToken token)
    {
        private readonly Dictionary<ExceptionSummaryResult.Step, ExceptionWitness?> cache = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CallableId, (ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites, bool Via)?> above = [];

        // Findings in the order of their example's action (by display, as the actions are listed), then by path.
        public IComparer<ExceptionWitness> Order { get; } = Comparer<ExceptionWitness>.Create((a, b) =>
            string.CompareOrdinal(index.Callable(a.Action).DisplaySignature, index.Callable(b.Action).DisplaySignature) is var c and not 0 ? c
            : string.CompareOrdinal(a.Action.Value, b.Action.Value) is var d and not 0 ? d
            : string.CompareOrdinal(string.Join("|", a.Path.Select(x => x.Value)), string.Join("|", b.Path.Select(x => x.Value))));

        public ExceptionWitness? Of(ExceptionSummaryResult.Step step)
        {
            if (cache.TryGetValue(step, out var known)) return known;
            ExceptionWitness? witness = null;
            if (Above(step.Owner) is { } up)
            {
                var (path, sites, via) = (up.Path, up.Sites, up.Via || step.Result.Flow.ViaCandidate);
                if (step.From is { } from)
                {
                    var down = result.Shortest(from.Method, [from.Key]);
                    path = path.AddRange(down.Path); sites = sites.Add(step.Site!).AddRange(down.Sites.Select(index.CallSite));
                }
                witness = ExceptionTransfer.Witness(index, path[0], path, sites, via, index.ThrowSite(step.Result.Flow.Origin), token);
            }
            return cache[step] = witness;
        }

        // Every root whose calls reach the method, through proven calls or implementation candidates, ordinal by ID.
        public ImmutableArray<CallableId> RootsAbove(CallableId method)
        {
            var seen = new HashSet<CallableId> { method }; var pending = new Queue<CallableId>([method]);
            while (pending.TryDequeue(out var at))
                foreach (var (site, _) in result.Callers.GetValueOrDefault(at, [])) if (seen.Add(site.Caller)) pending.Enqueue(site.Caller);
            return [.. seen.Where(result.Roots.ContainsKey).OrderBy(x => x.Value, StringComparer.Ordinal)];
        }

        private (ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites, bool Via)? Above(CallableId method) =>
            above.TryGetValue(method, out var known) ? known : above[method] = Search(method, false) ?? Search(method, true);

        // Breadth-first from the method up through its callers; at the first level holding a root, the root first by display.
        private (ImmutableArray<CallableId> Path, ImmutableArray<CallSiteFact> Sites, bool Via)? Search(CallableId method, bool candidates)
        {
            var below = new Dictionary<CallableId, (CallableId Callee, CallSiteFact Site, bool Candidate)?> { [method] = null };
            var level = new List<CallableId> { method };
            while (level.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var root = level.Where(result.Roots.ContainsKey).OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal)
                    .ThenBy(x => x.Value, StringComparer.Ordinal).Cast<CallableId?>().FirstOrDefault();
                if (root is { } found)
                {
                    var (path, sites, via) = (ImmutableArray.CreateBuilder<CallableId>(), ImmutableArray.CreateBuilder<CallSiteFact>(), false);
                    for (var at = found; ; )
                    {
                        path.Add(at);
                        if (below[at] is not { } step) break;
                        sites.Add(step.Site); via |= step.Candidate; at = step.Callee;
                    }
                    return (path.ToImmutable(), sites.ToImmutable(), via);
                }
                var next = new List<CallableId>();
                foreach (var callee in level)
                    foreach (var (site, candidate) in result.Callers.GetValueOrDefault(callee, []))
                        if ((candidates || !candidate) && below.TryAdd(site.Caller, (callee, site, candidate))) next.Add(site.Caller);
                level = next;
            }
            return null;
        }
    }
}
