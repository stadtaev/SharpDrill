using System.Collections.Immutable;
using SharpView.Graph;

namespace SharpView.Core;

// FollowCandidates continues through interface/virtual calls into source implementation candidates; effects that
// pass through one are marked ViaCandidate. ThirdPartyCallOrigins also makes every call into a third-party assembly an
// origin (of any type, which every catch takes); its escapes are the calls with no catch on the way from the root.
public sealed record ExceptionSummaryOptions(int MaxWork = 1_000_000, int MaxFacts = 100_000, bool FollowCandidates = false, bool ThirdPartyCallOrigins = false)
{
    public void Validate()
    {
        if (MaxWork <= 0 || MaxFacts <= 0) throw new ArgumentException("Summary budgets must be positive.");
    }
}

public sealed record ExceptionEscapeFact(ThrowSiteId Origin, ThrowSiteId? SubsequentOrigin,
    string? ExceptionType, bool IsExactType, ExceptionReason ReasonCode, bool ViaCandidate = false);
public sealed record ExceptionUncertainty(ExceptionReason ReasonCode, ThrowSiteId? Origin,
    CallSiteId? Site, string Message, SourceEvidence? Evidence = null);
public sealed record ExceptionMethodSummary(CallableId Method, ImmutableArray<ExceptionEscapeFact> Escapes,
    ImmutableArray<ExceptionUncertainty> Unknowns, bool IsComplete);
public sealed record ExceptionCrossingSummary(CallSiteId Site, ImmutableArray<ExceptionEscapeFact> Escapes,
    ImmutableArray<ExceptionUncertainty> Unknowns, ImmutableArray<ThrowSiteId> InterceptedOrigins, bool IsComplete);
public sealed record ExceptionSummaryWitness(ThrowSiteId Origin, ImmutableArray<CallableId> Path,
    ImmutableArray<CallSiteId> Sites);

public sealed class ExceptionSummaryResult
{
    internal readonly record struct FlowKey(ThrowSiteId Origin, ThrowSiteId? SubsequentOrigin,
        TypeUse? StaticType, TypeUse? ExactType, bool Exact, bool Changed, bool ViaCandidate);
    internal sealed record Link(CallableId Callee, FlowKey Key, CallSiteId Site);
    internal sealed record Stored(ExceptionFlow Flow, Link? Next);
    private readonly ImmutableDictionary<CallableId, ImmutableDictionary<FlowKey, Stored>> facts;
    // Links found after a fact's first one; witnesses search all of them for the shortest path.
    private readonly IReadOnlyDictionary<(CallableId, FlowKey), List<Link>> alternatives;

    internal ExceptionSummaryResult(ImmutableDictionary<CallableId, ExceptionMethodSummary> methods,
        ImmutableDictionary<CallableId, ExceptionMethodSummary> roots,
        ImmutableDictionary<CallSiteId, ExceptionCrossingSummary> crossings,
        ImmutableDictionary<CallableId, ImmutableDictionary<FlowKey, Stored>> facts,
        IReadOnlyDictionary<(CallableId, FlowKey), List<Link>> alternatives,
        bool workLimitReached, bool factLimitReached, int workItemsProcessed, int factsAdded,
        ImmutableArray<AnalysisGap> globalGaps)
    {
        Methods = methods; Roots = roots; Crossings = crossings; this.facts = facts; this.alternatives = alternatives;
        WorkLimitReached = workLimitReached; FactLimitReached = factLimitReached;
        WorkItemsProcessed = workItemsProcessed; FactsAdded = factsAdded; GlobalGaps = globalGaps;
    }

    public ImmutableDictionary<CallableId, ExceptionMethodSummary> Methods { get; }
    public ImmutableDictionary<CallableId, ExceptionMethodSummary> Roots { get; }
    public ImmutableDictionary<CallSiteId, ExceptionCrossingSummary> Crossings { get; }
    public ImmutableArray<AnalysisGap> GlobalGaps { get; }
    // Followed callees and library callees per method; library methods have no source body, so their exceptions are
    // not modelled and they never make a summary Unknown.
    internal ImmutableDictionary<CallableId, ImmutableArray<CallableId>> Callees { get; init; } = ImmutableDictionary<CallableId, ImmutableArray<CallableId>>.Empty;
    internal ImmutableDictionary<CallableId, ImmutableArray<CallableId>> LibraryByMethod { get; init; } = ImmutableDictionary<CallableId, ImmutableArray<CallableId>>.Empty;
    // Origins standing for third-party call sites (ThirdPartyCallOrigins); they have no throw site.
    public ImmutableDictionary<ThrowSiteId, CallSiteId> ThirdPartyCalls { get; init; } = ImmutableDictionary<ThrowSiteId, CallSiteId>.Empty;

    /// <summary>Library methods called anywhere beneath a root (direct calls, and candidates when followed).</summary>
    public ImmutableArray<CallableId> LibraryCallsFrom(CallableId root)
    {
        var seen = new HashSet<CallableId> { root }; var pending = new Stack<CallableId>([root]);
        var library = new SortedSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var method))
        {
            foreach (var call in LibraryByMethod.GetValueOrDefault(method, [])) library.Add(call.Value);
            foreach (var callee in Callees.GetValueOrDefault(method, [])) if (seen.Add(callee)) pending.Push(callee);
        }
        return [.. library.Select(x => new CallableId(x))];
    }

    // Ownerless gaps (top-level statements, minimal APIs) belong to no root; the CLI reports them on their own.
    public bool IsComplete => !WorkLimitReached && !FactLimitReached && Roots.Values.All(x => x.IsComplete);
    public bool WorkLimitReached { get; }
    public bool FactLimitReached { get; }
    public int WorkItemsProcessed { get; }
    public int FactsAdded { get; }

    // The shortest path among the root's facts for this origin; a proven path is preferred to one through a candidate.
    public ExceptionSummaryWitness GetWitness(CallableId root, ThrowSiteId origin)
    {
        if (!Roots.TryGetValue(root, out var summary) || !summary.Escapes.Any(x => x.Origin == origin))
            throw new ArgumentException("No escaping fact for this root and origin.");
        var entries = facts[root].Where(x => x.Key.Origin == origin && x.Value.Flow.Type is not null)
            .OrderBy(x => x.Value.Flow.ViaCandidate)
            .ThenBy(x => x.Value.Flow.SubsequentOrigin?.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Value.Flow.Type?.DisplayName, StringComparer.Ordinal).ToArray();
        return Shortest(root, entries.Where(x => x.Value.Flow.ViaCandidate == entries[0].Value.Flow.ViaCandidate).Select(x => x.Key));
    }

    public ExceptionSummaryWitness GetWitness(CallableId root, ExceptionEscapeFact escape)
    {
        if (!Roots.TryGetValue(root, out var summary) || !summary.Escapes.Contains(escape))
            throw new ArgumentException("No matching escaping fact for this root.");
        var entries = facts[root].Where(x => x.Key.Origin == escape.Origin && x.Value.Flow.SubsequentOrigin == escape.SubsequentOrigin
            && x.Value.Flow.Type?.DisplayName == escape.ExceptionType && x.Value.Flow.Exact == escape.IsExactType
            && (x.Value.Flow.Changed ? ExceptionReason.ChangedMayEscape : ExceptionReason.MayEscape) == escape.ReasonCode
            && x.Value.Flow.ViaCandidate == escape.ViaCandidate)
            .OrderBy(x => x.Key.StaticType?.DisplayName, StringComparer.Ordinal)
            .ThenBy(x => x.Key.ExactType?.DisplayName, StringComparer.Ordinal).Select(x => x.Key);
        return Shortest(root, entries);
    }

    // Breadth-first over every provenance link from the root's facts down to a seed (a fact without a link: the throw's
    // own method). Ties go to the earliest recorded link, so the result is deterministic.
    private ExceptionSummaryWitness Shortest(CallableId root, IEnumerable<FlowKey> starts)
    {
        var parents = new Dictionary<(CallableId Method, FlowKey Key), ((CallableId Method, FlowKey Key) From, CallSiteId Site)?>();
        var pending = new Queue<(CallableId Method, FlowKey Key)>();
        foreach (var key in starts) if (parents.TryAdd((root, key), null)) pending.Enqueue((root, key));
        while (pending.TryDequeue(out var state))
        {
            var first = facts[state.Method][state.Key].Next;
            if (first is null)
            {
                var path = new List<CallableId>(); var sites = new List<CallSiteId>();
                for (var at = state; ; )
                {
                    path.Add(at.Method);
                    if (parents[at] is not { } parent) break;
                    sites.Add(parent.Site); at = parent.From;
                }
                path.Reverse(); sites.Reverse();
                return new(state.Key.Origin, [.. path], [.. sites]);
            }
            foreach (var link in alternatives.TryGetValue(state, out var more) ? [first, .. more] : (IEnumerable<Link>)[first])
                if (parents.TryAdd((link.Callee, link.Key), (state, link.Site))) pending.Enqueue((link.Callee, link.Key));
        }
        throw new InvalidOperationException("Summary provenance does not reach an origin.");
    }
}

public static class ExceptionSummaryQuery
{
    private sealed record DirectEdge(CallableId Source, CallableId Target) : IEdge<CallableId>;
    public static ExceptionSummaryResult Analyze(ProgramSnapshot snapshot, ImmutableArray<CallableId> roots,
        ExceptionSummaryOptions? options = null, CancellationToken token = default) =>
        Analyze(new ProgramIndex(snapshot), roots, options, token);

    public static ExceptionSummaryResult Analyze(ProgramIndex index, ImmutableArray<CallableId> roots,
        ExceptionSummaryOptions? options = null, CancellationToken token = default)
    {
        options ??= new(); options.Validate(); token.ThrowIfCancellationRequested();
        foreach (var root in roots) if (!index.ContainsCallable(root)) throw new ArgumentException($"Unknown root: {root.Value}");
        var count = 0; var work = 0; var factLimit = false; var workLimit = false;
        bool Spend()
        {
            if (work >= options.MaxWork) { workLimit = true; return false; }
            work++; return true;
        }
        var calls = index.GetCallGraph(token);
        // What each call site leads to: followed callees (direct source callees, and implementation candidates when
        // requested) or a library call. Library methods have no source body, so their exceptions are not modelled:
        // they are recorded, never part of the summary graph, and never make a summary Unknown.
        var siteTargets = new Dictionary<CallSiteId, ImmutableArray<CallableId>>();
        var librarySites = new Dictionary<CallSiteId, CallableId>();
        var libraryByMethod = new Dictionary<CallableId, HashSet<CallableId>>();
        var callees = new Dictionary<CallableId, HashSet<CallableId>>();
        var candidateIncoming = new Dictionary<CallableId, List<CallSiteFact>>();
        void AddTo<T>(Dictionary<CallableId, HashSet<T>> map, CallableId key, T value) =>
            (map.TryGetValue(key, out var set) ? set : map[key] = []).Add(value);
        var reachable = new HashSet<CallableId>(roots);
        var pending = new Stack<CallableId>(roots);
        while (!workLimit && pending.TryPop(out var method))
        {
            token.ThrowIfCancellationRequested();
            foreach (var site in index.Outgoing(method))
            {
                token.ThrowIfCancellationRequested();
                if (!Spend()) break;
                var targets = ImmutableArray<CallableId>.Empty;
                if (site.Dispatch == DispatchKind.Direct)
                {
                    if (site.BoundTarget is { } target && index.ContainsCallable(target))
                    {
                        if (index.Callable(target).Body == BodyStatus.MetadataOnly) { librarySites.Add(site.Id, target); AddTo(libraryByMethod, method, target); }
                        else targets = [target];
                    }
                }
                else
                {
                    var candidates = calls.CandidatesOf(site.Id);
                    if (site.BoundTarget is { } declared && index.ContainsCallable(declared)
                        && index.Callable(declared).Body == BodyStatus.MetadataOnly && candidates.IsEmpty)
                    { librarySites.Add(site.Id, declared); AddTo(libraryByMethod, method, declared); }
                    else if (options.FollowCandidates)
                    {
                        targets = candidates;
                        foreach (var candidate in candidates)
                            (candidateIncoming.TryGetValue(candidate, out var list) ? list : candidateIncoming[candidate] = []).Add(site);
                    }
                }
                siteTargets[site.Id] = targets;
                foreach (var target in targets) { AddTo(callees, method, target); if (reachable.Add(target)) pending.Push(target); }
            }
        }
        // Graph/SCC preparation is O(V+E) baseline work and cancellable; MaxWork counts
        // examined source facts/sites and transfer crossings after this preparation.
        // Tarjan returns callee components before their callers. Iterate each component's
        // worklist to a fixed point, then propagate its final facts to later components.
        var graphBuilder = DiGraph<CallableId, DirectEdge>.CreateBuilder();
        foreach (var method in reachable) graphBuilder.AddNode(method);
        foreach (var method in reachable)
            foreach (var site in index.Outgoing(method))
            {
                token.ThrowIfCancellationRequested();
                foreach (var target in siteTargets.GetValueOrDefault(site.Id, []))
                    if (reachable.Contains(target)) graphBuilder.AddEdge(new(method, target));
            }
        var components = graphBuilder.Build().StronglyConnectedComponents(token);
        var componentOf = new Dictionary<CallableId, int>();
        for (var i = 0; i < components.Length; i++)
            foreach (var method in components[i]) componentOf.Add(method, i);
        var thirdParty = options.ThirdPartyCallOrigins ? index.Snapshot.ThirdPartyAssemblies.ToHashSet() : [];
        var thirdPartyCalls = new SortedDictionary<ThrowSiteId, CallSiteId>(Comparer<ThrowSiteId>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value)));
        var facts = reachable.ToDictionary(x => x, _ => new Dictionary<ExceptionSummaryResult.FlowKey, ExceptionSummaryResult.Stored>());
        var alternatives = new Dictionary<(CallableId, ExceptionSummaryResult.FlowKey), List<ExceptionSummaryResult.Link>>();
        var unknowns = reachable.ToDictionary(x => x, _ => new HashSet<ExceptionUncertainty>());
        var crossingFlows = new Dictionary<CallSiteId, HashSet<ExceptionEscapeFact>>();
        var crossingUnknowns = new Dictionary<CallSiteId, HashSet<ExceptionUncertainty>>();
        var crossingIntercepts = new Dictionary<CallSiteId, HashSet<ThrowSiteId>>();
        foreach (var method in reachable)
            foreach (var site in index.Outgoing(method))
            {
                token.ThrowIfCancellationRequested();
                crossingFlows.TryAdd(site.Id, []); crossingUnknowns.TryAdd(site.Id, []); crossingIntercepts.TryAdd(site.Id, []);
            }
        var queues = Enumerable.Range(0, components.Length)
            .Select(_ => new Queue<(CallableId Method, ExceptionSummaryResult.FlowKey Key)>()).ToArray();
        var unknownQueues = Enumerable.Range(0, components.Length)
            .Select(_ => new Queue<(CallableId Method, ExceptionUncertainty Value)>()).ToArray();
        static ExceptionSummaryResult.FlowKey Key(ExceptionFlow flow) => new(flow.Origin, flow.SubsequentOrigin,
            flow.StaticType, flow.ExactType, flow.Exact, flow.Changed, flow.ViaCandidate);
        static ExceptionEscapeFact Escape(ExceptionFlow flow) => new(flow.Origin, flow.SubsequentOrigin,
            flow.Type?.DisplayName, flow.Exact, flow.Changed ? ExceptionReason.ChangedMayEscape : ExceptionReason.MayEscape, flow.ViaCandidate);
        // Direct callers, then (when candidates are followed) callers through an interface/virtual declaration.
        IEnumerable<(CallSiteFact Site, bool Candidate)> IncomingSites(CallableId method)
        {
            foreach (var site in index.Incoming(method))
                if (site.Dispatch == DispatchKind.Direct && reachable.Contains(site.Caller)) yield return (site, false);
            if (candidateIncoming.TryGetValue(method, out var list))
                foreach (var site in list) yield return (site, true);
        }
        bool AddFlow(CallableId method, ExceptionFlow flow, ExceptionSummaryResult.Link? link)
        {
            var key = Key(flow);
            if (facts[method].ContainsKey(key))
            {
                if (link is not null) (alternatives.TryGetValue((method, key), out var list) ? list : alternatives[(method, key)] = []).Add(link);
                return true;
            }
            if (count >= options.MaxFacts) { factLimit = true; return false; }
            facts[method].Add(key, new(flow, link)); count++; queues[componentOf[method]].Enqueue((method, key)); return true;
        }
        bool AddUnknown(CallableId method, ExceptionUncertainty value)
        {
            if (unknowns[method].Contains(value)) return true;
            if (count >= options.MaxFacts) { factLimit = true; return false; }
            unknowns[method].Add(value); count++; unknownQueues[componentOf[method]].Enqueue((method, value)); return true;
        }
        foreach (var method in reachable.OrderBy(x => x.Value, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (workLimit || factLimit) break;
            var callable = index.Callable(method);
            // Compiler-provided members hold no explicit code: no origins, no uncertainty.
            if (callable.Body == BodyStatus.Synthesized) continue;
            if (callable.Body != BodyStatus.Source)
            {
                if (Spend()) AddUnknown(method, new(ExceptionReason.MissingBody, null, null,
                    "Missing body: explicit origins unknown.", callable.Declarations.FirstOrDefault()));
            }
            else
            {
                foreach (var origin in index.ThrowSites(method).Where(x => x.Kind == ThrowKind.Explicit))
                {
                    token.ThrowIfCancellationRequested();
                    if (factLimit || workLimit) break;
                    if (!Spend()) break;
                    var transfer = ExceptionTransfer.Start(index, origin, token, collectEvidence: false);
                    if (transfer.Status == ExceptionStatus.Unknown)
                        AddUnknown(method, new(transfer.Reason, origin.Id, null, transfer.Message, origin.Evidence));
                    else if (transfer.Status is null) AddFlow(method, transfer.Flow, null);
                }
                foreach (var gap in index.Gaps(method))
                {
                    token.ThrowIfCancellationRequested();
                    if (factLimit || workLimit) break;
                    if (!Spend()) break;
                    AddUnknown(method, new(ExceptionReason.ExtractionGap, null, null, gap.Message, gap.Evidence));
                }
                foreach (var site in index.Outgoing(method))
                {
                    token.ThrowIfCancellationRequested();
                    if (factLimit || workLimit) break;
                    if (!Spend()) break;
                    if (librarySites.TryGetValue(site.Id, out var library))
                    {
                        if (thirdParty.Contains(index.Callable(library).Compilation))
                        {
                            var origin = new ThrowSiteId("call|" + site.Id.Value); thirdPartyCalls[origin] = site.Id;
                            var transfer = ExceptionTransfer.StartAtCall(index, origin, site, token);
                            if (transfer.Status == ExceptionStatus.Unknown) AddUnknown(method, new(transfer.Reason, origin, site.Id, transfer.Message, site.Evidence));
                            else if (transfer.Status is null) AddFlow(method, transfer.Flow, null);
                        }
                        continue;
                    }
                    if (site.Dispatch != DispatchKind.Direct)
                    {
                        // Followed candidates carry their own effects in; only a call with nowhere to go is uncertain.
                        if (options.FollowCandidates && !siteTargets.GetValueOrDefault(site.Id, []).IsEmpty) continue;
                        var value = new ExceptionUncertainty(ExceptionReason.DispatchBoundary, null, site.Id,
                            options.FollowCandidates ? $"{site.Dispatch} dispatch: no source implementation found; runtime origins unknown."
                                                     : $"{site.Dispatch} dispatch: runtime origins unknown.", site.Evidence);
                        crossingUnknowns[site.Id].Add(value); AddUnknown(method, value);
                    }
                    else if (site.BoundTarget is not { } target || !index.ContainsCallable(target))
                    {
                        var value = new ExceptionUncertainty(ExceptionReason.UnresolvedTarget, null, site.Id,
                            "Unresolved call target.", site.Evidence);
                        crossingUnknowns[site.Id].Add(value); AddUnknown(method, value);
                    }
                }
            }
        }
        for (var component = 0; component < components.Length && !factLimit && !workLimit; component++)
        {
            var queue = queues[component]; var unknownQueue = unknownQueues[component];
            while (!factLimit && !workLimit && (queue.Count > 0 || unknownQueue.Count > 0))
            {
                token.ThrowIfCancellationRequested();
                if (queue.TryDequeue(out var item))
                {
                    var flow = facts[item.Method][item.Key].Flow;
                    foreach (var (site, viaCandidate) in IncomingSites(item.Method))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Spend()) break;
                        var input = viaCandidate && !flow.ViaCandidate ? flow with { ViaCandidate = true } : flow;
                        var crossed = ExceptionTransfer.Cross(index, input, site, token, collectEvidence: false);
                        if (crossed.Status == ExceptionStatus.Intercepted)
                            crossingIntercepts[site.Id].Add(flow.Origin);
                        else if (crossed.Status == ExceptionStatus.Unknown)
                        {
                            var uncertainty = new ExceptionUncertainty(crossed.Reason, flow.Origin, site.Id,
                                crossed.Message, site.Evidence);
                            crossingUnknowns[site.Id].Add(uncertainty);
                            AddUnknown(site.Caller, uncertainty);
                        }
                        else
                        {
                            if (crossed.Flow.Type is not null) crossingFlows[site.Id].Add(Escape(crossed.Flow));
                            else crossingUnknowns[site.Id].Add(new(ExceptionReason.UnboundExceptionType,
                                crossed.Flow.Origin, site.Id, "Exception type could not be bound.", site.Evidence));
                            AddFlow(site.Caller, crossed.Flow, new(item.Method, item.Key, site.Id));
                        }
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
        var incomplete = factLimit || workLimit;
        var methods = reachable.ToImmutableDictionary(method => method, method =>
        {
            var all = facts[method].Values.Select(x => x.Flow).Where(x => x.Type is not null)
                .Select(Escape).Distinct().ToList();
            // A possibility through an implementation candidate is redundant when the same effect is proven directly.
            var proven = all.Where(x => !x.ViaCandidate).Select(x => (x.Origin, x.SubsequentOrigin, x.ExceptionType, x.IsExactType)).ToHashSet();
            var escapes = all.Where(x => !x.ViaCandidate || !proven.Contains((x.Origin, x.SubsequentOrigin, x.ExceptionType, x.IsExactType)))
                .OrderBy(x => x.Origin.Value, StringComparer.Ordinal)
                .ThenBy(x => x.SubsequentOrigin?.Value, StringComparer.Ordinal).ThenBy(x => x.ViaCandidate).ToImmutableArray();
            var localUnknowns = unknowns[method].ToList();
            foreach (var flow in facts[method].Values.Select(x => x.Flow).Where(x => x.Type is null))
                localUnknowns.Add(new(ExceptionReason.UnboundExceptionType, flow.Origin, null, "Exception type could not be bound."));
            if (incomplete)
                localUnknowns.Add(new(factLimit ? ExceptionReason.FactLimit : ExceptionReason.WorkLimit, null, null,
                    factLimit ? "Summary fact budget exhausted; effects may be missing." : "Summary work budget exhausted; effects may be missing."));
            var sortedUnknowns = localUnknowns.Distinct().OrderBy(x => x.ReasonCode).ThenBy(x => x.Origin?.Value, StringComparer.Ordinal)
                .ThenBy(x => x.Site?.Value, StringComparer.Ordinal).ToImmutableArray();
            return new ExceptionMethodSummary(method, escapes, sortedUnknowns, sortedUnknowns.Length == 0);
        });
        var crossings = crossingFlows.Keys.ToImmutableDictionary(site => site, site =>
        {
            var uncertainties = crossingUnknowns[site].ToList();
            if (incomplete)
                uncertainties.Add(new(factLimit ? ExceptionReason.FactLimit : ExceptionReason.WorkLimit, null, site,
                    factLimit ? "Summary fact budget exhausted; crossing effects may be missing."
                              : "Summary work budget exhausted; crossing effects may be missing."));
            return new ExceptionCrossingSummary(site,
                [.. crossingFlows[site].OrderBy(x => x.Origin.Value, StringComparer.Ordinal)],
                [.. uncertainties.OrderBy(x => x.ReasonCode).ThenBy(x => x.Origin?.Value, StringComparer.Ordinal)],
                [.. crossingIntercepts[site].OrderBy(x => x.Value, StringComparer.Ordinal)], uncertainties.Count == 0);
        });
        var frozenFacts = facts.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableDictionary());
        return new(methods, roots.Distinct().ToImmutableDictionary(x => x, x => methods[x]), crossings,
            frozenFacts, alternatives, workLimit, factLimit, work, count, [.. index.Snapshot.Gaps.Where(x => x.Owner is null)])
        {
            Callees = callees.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray()),
            LibraryByMethod = libraryByMethod.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray()),
            ThirdPartyCalls = thirdPartyCalls.ToImmutableDictionary(),
        };
    }
}
