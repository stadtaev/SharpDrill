using System.Collections.Immutable;
using FlowKey = SharpDrill.Core.ExceptionSummaryResult.FlowKey;
using Link = SharpDrill.Core.ExceptionSummaryResult.Link;
using Stored = SharpDrill.Core.ExceptionSummaryResult.Stored;
using Step = SharpDrill.Core.ExceptionSummaryResult.Step;
using FactRef = SharpDrill.Core.ExceptionSummaryResult.FactRef;

namespace SharpDrill.Core;

public static class ExceptionSummaryQuery
{
    public static ExceptionSummaryResult Analyze(ProgramSnapshot snapshot, ImmutableArray<CallableId> roots,
        ExceptionSummaryOptions? options = null, CancellationToken token = default) =>
        Analyze(new ProgramIndex(snapshot), roots, options, token);

    public static ExceptionSummaryResult Analyze(ProgramIndex index, ImmutableArray<CallableId> roots,
        ExceptionSummaryOptions? options = null, CancellationToken token = default)
    {
        options ??= new(); options.Validate(); token.ThrowIfCancellationRequested();
        foreach (var root in roots) if (!index.ContainsCallable(root)) throw new ArgumentException($"Unknown root: {root.Value}");
        var solver = new Solver(index, options, token);
        solver.Reach(roots); solver.Seed(); solver.Propagate();
        return solver.Summarize(roots);
    }

    // One analysis run, in four phases: Reach finds what the roots can call, Seed puts each method's own effects
    // (explicit throws, third-party calls) and uncertainties on it, Propagate pushes them up through caller sites to a
    // fixed point, Summarize freezes the result. Facts are keyed per method, not per path: a method is where effects
    // from all its callees merge, so each distinct effect is pushed to each caller once.
    private sealed class Solver(ProgramIndex index, ExceptionSummaryOptions options, CancellationToken token)
    {
        private readonly CallGraph calls = index.GetCallGraph(token);
        private readonly HashSet<CallableId> reachable = [];
        private readonly Dictionary<CallableId, HashSet<CallableId>> callees = [];
        private readonly Dictionary<CallableId, HashSet<CallableId>> libraryByMethod = [];
        // Call sites that reach a method only as an implementation candidate of an interface/virtual call.
        private readonly Dictionary<CallableId, List<CallSiteFact>> candidateIncoming = [];

        private readonly Dictionary<CallableId, Dictionary<FlowKey, Stored>> facts = [];
        // Links found after a fact's first one; witnesses search all of them for the shortest path.
        private readonly Dictionary<(CallableId, FlowKey), List<Link>> alternatives = [];
        private readonly Dictionary<CallableId, HashSet<ExceptionUncertainty>> unknowns = [];
        private readonly Dictionary<CallSiteId, HashSet<ExceptionEscapeFact>> crossingFlows = [];
        private readonly Dictionary<CallSiteId, HashSet<ExceptionUncertainty>> crossingUnknowns = [];
        private readonly Dictionary<CallSiteId, HashSet<ThrowSiteId>> crossingIntercepts = [];
        private readonly SortedDictionary<ThrowSiteId, CallSiteId> thirdPartyCalls = new(Comparer<ThrowSiteId>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value)));
        private readonly Queue<(CallableId Method, FlowKey Key)> queue = new();
        private readonly Queue<(CallableId Method, ExceptionUncertainty Value)> unknownQueue = new();
        private readonly List<Step> steps = [];

        // MaxWork counts examined sites, origins, gaps and transfer crossings; MaxFacts counts retained facts and unknowns.
        private int work, count;
        private bool workLimit, factLimit;
        private bool Stopped => workLimit || factLimit;

        private bool Spend()
        {
            if (work >= options.MaxWork) { workLimit = true; return false; }
            work++; return true;
        }

        // Followed callees: direct source callees, and implementation candidates when requested. Library calls are
        // recorded (their exceptions are not modelled), never followed and never Unknown.
        private ImmutableArray<CallableId> Followed(CallTarget target) => target.Kind switch
        {
            CallTargetKind.Source => target.Callees,
            CallTargetKind.Dispatch when options.FollowCandidates => target.Callees,
            _ => [],
        };

        private static void AddTo<T>(Dictionary<CallableId, HashSet<T>> map, CallableId key, T value) =>
            (map.TryGetValue(key, out var set) ? set : map[key] = []).Add(value);

        public void Reach(ImmutableArray<CallableId> roots)
        {
            reachable.UnionWith(roots);
            var pending = new Stack<CallableId>(roots);
            while (!workLimit && pending.TryPop(out var method))
            {
                token.ThrowIfCancellationRequested();
                foreach (var site in index.Outgoing(method))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Spend()) break;
                    var target = calls.Resolve(site.Id);
                    if (target.Kind == CallTargetKind.Library) AddTo(libraryByMethod, method, target.Callees[0]);
                    var followed = Followed(target);
                    if (target.Kind == CallTargetKind.Dispatch)
                        foreach (var candidate in followed)
                            (candidateIncoming.TryGetValue(candidate, out var list) ? list : candidateIncoming[candidate] = []).Add(site);
                    foreach (var callee in followed) { AddTo(callees, method, callee); if (reachable.Add(callee)) pending.Push(callee); }
                }
            }
            foreach (var method in reachable)
            {
                facts[method] = []; unknowns[method] = [];
                foreach (var site in index.Outgoing(method))
                {
                    token.ThrowIfCancellationRequested();
                    crossingFlows.TryAdd(site.Id, []); crossingUnknowns.TryAdd(site.Id, []); crossingIntercepts.TryAdd(site.Id, []);
                }
            }
        }

        // In ordinal method order, so the queue (and with it the first recorded link of each fact) is deterministic.
        public void Seed()
        {
            var thirdParty = options.ThirdPartyCallOrigins ? index.Snapshot.ThirdPartyAssemblies.ToHashSet() : [];
            foreach (var method in reachable.OrderBy(x => x.Value, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (Stopped) break;
                var callable = index.Callable(method);
                // Compiler-provided members hold no explicit code: no origins, no uncertainty.
                if (callable.Body == BodyStatus.Synthesized) continue;
                if (callable.Body != BodyStatus.Source)
                {
                    if (Spend()) AddUnknown(method, new(ExceptionReason.MissingBody, null, null,
                        "Missing body: explicit origins unknown.", callable.Declarations.FirstOrDefault()));
                    continue;
                }
                foreach (var origin in index.ThrowSites(method).Where(x => x.Kind == ThrowKind.Explicit))
                {
                    token.ThrowIfCancellationRequested();
                    if (Stopped || !Spend()) break;
                    var transfer = ExceptionTransfer.Start(index, origin, token, collectEvidence: false);
                    FactRef? to = null;
                    if (transfer.Status == ExceptionStatus.Unknown)
                        AddUnknown(method, new(transfer.Reason, origin.Id, null, transfer.Message, origin.Evidence));
                    else if (transfer.Status is null && AddFlow(method, transfer.Flow, null)) to = new(method, Key(transfer.Flow));
                    steps.Add(new(null, null, method, transfer, to));
                }
                foreach (var gap in index.Gaps(method))
                {
                    token.ThrowIfCancellationRequested();
                    if (Stopped || !Spend()) break;
                    AddUnknown(method, new(ExceptionReason.ExtractionGap, null, null, gap.Message, gap.Evidence));
                }
                foreach (var site in index.Outgoing(method))
                {
                    token.ThrowIfCancellationRequested();
                    if (Stopped || !Spend()) break;
                    var target = calls.Resolve(site.Id);
                    switch (target.Kind)
                    {
                        // Strict mode: a third-party call is an origin of any type, which every catch takes.
                        case CallTargetKind.Library when thirdParty.Contains(index.Callable(target.Callees[0]).Compilation):
                            var origin = new ThrowSiteId("call|" + site.Id.Value); thirdPartyCalls[origin] = site.Id;
                            var transfer = ExceptionTransfer.StartAtCall(index, origin, site, token);
                            if (transfer.Status == ExceptionStatus.Unknown) AddUnknown(method, new(transfer.Reason, origin, site.Id, transfer.Message, site.Evidence));
                            else if (transfer.Status is null) AddFlow(method, transfer.Flow, null);
                            break;
                        // Followed candidates carry their own effects in; only a call with nowhere to go is uncertain.
                        case CallTargetKind.Dispatch when Followed(target).IsEmpty:
                            Boundary(method, new(ExceptionReason.DispatchBoundary, null, site.Id,
                                options.FollowCandidates ? $"{site.Dispatch} dispatch: no source implementation found; runtime origins unknown."
                                                         : $"{site.Dispatch} dispatch: runtime origins unknown.", site.Evidence));
                            break;
                        case CallTargetKind.Unresolved:
                            Boundary(method, new(ExceptionReason.UnresolvedTarget, null, site.Id, "Unresolved call target.", site.Evidence));
                            break;
                    }
                }
            }
        }

        private void Boundary(CallableId method, ExceptionUncertainty value)
        {
            crossingUnknowns[value.Site!.Value].Add(value); AddUnknown(method, value);
        }

        // Each fact crosses every incoming site once; ExceptionTransfer.Cross applies that site's try/catch/finally.
        public void Propagate()
        {
            while (!Stopped && (queue.Count > 0 || unknownQueue.Count > 0))
            {
                token.ThrowIfCancellationRequested();
                if (queue.TryDequeue(out var item))
                {
                    var flow = facts[item.Method][item.Key].Flow;
                    foreach (var (site, viaCandidate) in IncomingSites(item.Method))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Spend()) break;
                        // Handlers and missed regions start empty, so the result lists exactly this step's.
                        var input = flow with { ViaCandidate = flow.ViaCandidate || viaCandidate, Handlers = [], Possible = [], Missed = [] };
                        var crossed = ExceptionTransfer.Cross(index, input, site, item.Method, token, collectEvidence: false);
                        FactRef? to = null;
                        if (crossed.Status == ExceptionStatus.Intercepted)
                            crossingIntercepts[site.Id].Add(flow.Origin);
                        else if (crossed.Status == ExceptionStatus.Unknown)
                        {
                            var uncertainty = new ExceptionUncertainty(crossed.Reason, flow.Origin, site.Id, crossed.Message, site.Evidence);
                            crossingUnknowns[site.Id].Add(uncertainty);
                            AddUnknown(site.Caller, uncertainty);
                        }
                        // Ends in the async void method: the step records it; nothing reaches the caller.
                        else if (crossed.Status == ExceptionStatus.EscapesAsyncVoid) { }
                        else
                        {
                            if (crossed.Flow.Type is not null) crossingFlows[site.Id].Add(Escape(crossed.Flow));
                            else crossingUnknowns[site.Id].Add(new(ExceptionReason.UnboundExceptionType,
                                crossed.Flow.Origin, site.Id, "Exception type could not be bound.", site.Evidence));
                            if (AddFlow(site.Caller, crossed.Flow, new(item.Method, item.Key, site.Id))) to = new(site.Caller, Key(crossed.Flow));
                        }
                        // Third-party call origins answer --strict only; they are not catches or misses of a throw.
                        if (!flow.AnyType) steps.Add(new(new(item.Method, item.Key), site, site.Caller, crossed, to));
                        if (factLimit) break;
                    }
                }
                else if (unknownQueue.TryDequeue(out var unknownItem))
                {
                    foreach (var (site, _) in IncomingSites(unknownItem.Method))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Spend()) break;
                        crossingUnknowns[site.Id].Add(unknownItem.Value);
                        AddUnknown(site.Caller, unknownItem.Value);
                        if (factLimit) break;
                    }
                }
            }
        }

        // Direct callers, then (when candidates are followed) callers through an interface/virtual declaration.
        private IEnumerable<(CallSiteFact Site, bool Candidate)> IncomingSites(CallableId method)
        {
            foreach (var site in index.Incoming(method))
                if (site.Dispatch == DispatchKind.Direct && reachable.Contains(site.Caller)) yield return (site, false);
            if (candidateIncoming.TryGetValue(method, out var list))
                foreach (var site in list) yield return (site, true);
        }

        private static FlowKey Key(ExceptionFlow flow) => new(flow.Origin, flow.SubsequentOrigin,
            flow.StaticType, flow.ExactType, flow.Exact, flow.Changed, flow.ViaCandidate);

        private static ExceptionEscapeFact Escape(ExceptionFlow flow) => new(flow.Origin, flow.SubsequentOrigin,
            flow.Type?.DisplayName, flow.Exact, flow.Changed ? ExceptionReason.ChangedMayEscape : ExceptionReason.MayEscape, flow.ViaCandidate);

        // A known fact reached again only records the extra link (for shortest witnesses); it is not re-propagated.
        private bool AddFlow(CallableId method, ExceptionFlow flow, Link? link)
        {
            var key = Key(flow);
            if (facts[method].ContainsKey(key))
            {
                if (link is not null) (alternatives.TryGetValue((method, key), out var list) ? list : alternatives[(method, key)] = []).Add(link);
                return true;
            }
            if (count >= options.MaxFacts) { factLimit = true; return false; }
            facts[method].Add(key, new(flow, link)); count++; queue.Enqueue((method, key)); return true;
        }

        private bool AddUnknown(CallableId method, ExceptionUncertainty value)
        {
            if (unknowns[method].Contains(value)) return true;
            if (count >= options.MaxFacts) { factLimit = true; return false; }
            unknowns[method].Add(value); count++; unknownQueue.Enqueue((method, value)); return true;
        }

        public ExceptionSummaryResult Summarize(ImmutableArray<CallableId> roots)
        {
            var methods = reachable.ToImmutableDictionary(method => method, MethodSummary);
            var crossings = crossingFlows.Keys.ToImmutableDictionary(site => site, CrossingSummary);
            return new(methods, roots.Distinct().ToImmutableDictionary(x => x, x => methods[x]), crossings,
                facts.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableDictionary()), alternatives,
                workLimit, factLimit, work, count, [.. index.Snapshot.Gaps.Where(x => x.Owner is null)])
            {
                Callees = callees.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray()),
                LibraryByMethod = libraryByMethod.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray()),
                ThirdPartyCalls = thirdPartyCalls.ToImmutableDictionary(),
                Steps = [.. steps],
                StepsFrom = steps.Where(x => x.From is not null).ToLookup(x => x.From!.Value),
                Callers = reachable.ToImmutableDictionary(x => x, x => IncomingSites(x).ToImmutableArray()),
            };
        }

        private ExceptionMethodSummary MethodSummary(CallableId method)
        {
            var all = facts[method].Values.Select(x => x.Flow).Where(x => x.Type is not null).Select(Escape).Distinct().ToList();
            // A possibility through an implementation candidate is redundant when the same effect is proven directly.
            var proven = all.Where(x => !x.ViaCandidate).Select(x => (x.Origin, x.SubsequentOrigin, x.ExceptionType, x.IsExactType)).ToHashSet();
            var escapes = all.Where(x => !x.ViaCandidate || !proven.Contains((x.Origin, x.SubsequentOrigin, x.ExceptionType, x.IsExactType)))
                .OrderBy(x => x.Origin.Value, StringComparer.Ordinal)
                .ThenBy(x => x.SubsequentOrigin?.Value, StringComparer.Ordinal).ThenBy(x => x.ViaCandidate).ToImmutableArray();
            var local = unknowns[method].ToList();
            foreach (var flow in facts[method].Values.Select(x => x.Flow).Where(x => x.Type is null))
                local.Add(new(ExceptionReason.UnboundExceptionType, flow.Origin, null, "Exception type could not be bound."));
            if (Stopped)
                local.Add(new(factLimit ? ExceptionReason.FactLimit : ExceptionReason.WorkLimit, null, null,
                    factLimit ? "Summary fact budget exhausted; effects may be missing." : "Summary work budget exhausted; effects may be missing."));
            var sorted = local.Distinct().OrderBy(x => x.ReasonCode).ThenBy(x => x.Origin?.Value, StringComparer.Ordinal)
                .ThenBy(x => x.Site?.Value, StringComparer.Ordinal).ToImmutableArray();
            return new(method, escapes, sorted, sorted.Length == 0);
        }

        private ExceptionCrossingSummary CrossingSummary(CallSiteId site)
        {
            var uncertainties = crossingUnknowns[site].ToList();
            if (Stopped)
                uncertainties.Add(new(factLimit ? ExceptionReason.FactLimit : ExceptionReason.WorkLimit, null, site,
                    factLimit ? "Summary fact budget exhausted; crossing effects may be missing."
                              : "Summary work budget exhausted; crossing effects may be missing."));
            return new(site,
                [.. crossingFlows[site].OrderBy(x => x.Origin.Value, StringComparer.Ordinal)],
                [.. uncertainties.OrderBy(x => x.ReasonCode).ThenBy(x => x.Origin?.Value, StringComparer.Ordinal)],
                [.. crossingIntercepts[site].OrderBy(x => x.Value, StringComparer.Ordinal)], uncertainties.Count == 0);
        }
    }
}
