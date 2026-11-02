using System.Collections.Immutable;

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
    // A fact: one distinct effect that may leave a method.
    internal readonly record struct FactRef(CallableId Method, FlowKey Key);
    // One transfer of an explicit origin: leaving its own method's try regions (From and Site null), or a callee fact
    // crossing a call site in Owner. Result.Flow lists only the handlers and missed regions of this step; To is the
    // fact it became when it got past (null when intercepted, unknown or over budget).
    internal sealed record Step(FactRef? From, CallSiteFact? Site, CallableId Owner, ExceptionTransferResult Result, FactRef? To);
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
    // Every transfer step of an explicit origin, in processing order, and the steps leaving each fact: what
    // ExceptionHandlingQuery reads catches, misses and their outcomes from.
    internal ImmutableArray<Step> Steps { get; init; } = [];
    internal ILookup<FactRef, Step> StepsFrom { get; init; } = Array.Empty<Step>().ToLookup(x => default(FactRef));
    // Sites through which each reached method is called from reached code (direct callers, then candidate callers).
    internal ImmutableDictionary<CallableId, ImmutableArray<(CallSiteFact Site, bool Candidate)>> Callers { get; init; } =
        ImmutableDictionary<CallableId, ImmutableArray<(CallSiteFact Site, bool Candidate)>>.Empty;

    internal ExceptionFlow Flow(FactRef fact) => facts[fact.Method][fact.Key].Flow;
    internal IEnumerable<FlowKey> KeysAt(CallableId method) => facts.TryGetValue(method, out var known) ? known.Keys : [];

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

    // Breadth-first over every provenance link from a method's facts (a root's, or any) down to a seed (a fact without a link: the throw's
    // own method). Ties go to the earliest recorded link, so the result is deterministic.
    internal ExceptionSummaryWitness Shortest(CallableId root, IEnumerable<FlowKey> starts)
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
