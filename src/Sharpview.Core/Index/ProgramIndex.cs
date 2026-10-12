using System.Collections.Immutable;

namespace Sharpview.Core;

// Built once per snapshot: O(n) construction, dictionary lookups afterwards. Every grouped view keeps a
// documented ordinal order so consumers (queries, rules, reports) stay deterministic without re-sorting.
public sealed class ProgramIndex
{
    private readonly Dictionary<CallableId, CallableFact> callables;
    private readonly Dictionary<TypeId, TypeFact> types;
    private readonly Dictionary<CompilationId, CompilationFact> compilations;
    private readonly Dictionary<CallableId, ImmutableArray<CallSiteFact>> outgoing;
    private readonly Dictionary<CallableId, ImmutableArray<CallSiteFact>> incoming;
    private readonly Dictionary<CallableId, ImmutableArray<ThrowSiteFact>> throwsByOwner;
    private readonly Dictionary<CallableId, ImmutableArray<CatchFact>> catchesByOwner;
    private readonly Dictionary<CallableId, ImmutableArray<AnalysisGap>> gapsByOwner;
    private readonly Dictionary<ThrowSiteId, ThrowSiteFact> throwSites;
    private readonly Dictionary<CatchId, CatchFact> catches;
    private readonly Dictionary<ExceptionRegionId, ExceptionRegionFact> regions;
    private readonly Dictionary<CatchId, ImmutableArray<ExceptionRegionFact>> regionsByCatch;
    private readonly Dictionary<TypeId, ImmutableArray<CallableFact>> members;
    private readonly Dictionary<CallableId, ImmutableArray<ImplementationFact>> implementations;
    private readonly Dictionary<CallableId, ImmutableArray<ImplementationFact>> implemented;
    private readonly Dictionary<CallSiteId, CallSiteFact> sites;

    public ProgramSnapshot Snapshot { get; }

    public ProgramIndex(ProgramSnapshot snapshot)
    {
        Snapshot = snapshot;
        callables = snapshot.Callables.ToDictionary(x => x.Id);
        types = snapshot.Types.ToDictionary(x => x.Id);
        compilations = snapshot.Compilations.ToDictionary(x => x.Id);
        sites = snapshot.CallSites.ToDictionary(x => x.Id);
        throwSites = snapshot.ThrowSites.ToDictionary(x => x.Id);
        catches = snapshot.Catches.ToDictionary(x => x.Id);
        regions = snapshot.ExceptionRegions.ToDictionary(x => x.Id);
        outgoing = Group(snapshot.CallSites, x => x.Caller, x => x.OrderBy(s => s.TargetDisplay, StringComparer.Ordinal).ThenBy(s => s.Id.Value, StringComparer.Ordinal));
        incoming = Group(snapshot.CallSites.Where(x => x.BoundTarget is not null), x => x.BoundTarget!.Value, x => x.OrderBy(s => s.Id.Value, StringComparer.Ordinal));
        throwsByOwner = Group(snapshot.ThrowSites, x => x.Owner, x => x.OrderBy(s => s.Id.Value, StringComparer.Ordinal));
        catchesByOwner = Group(snapshot.Catches, x => x.Owner, x => x.OrderBy(s => s.Id.Value, StringComparer.Ordinal));
        // Snapshot order is retained: gaps are already sorted by compilation, location and message.
        gapsByOwner = Group(snapshot.Gaps.Where(x => x.Owner is not null), x => x.Owner!.Value, x => x);
        regionsByCatch = Group(snapshot.ExceptionRegions.SelectMany(r => r.Catches.Select(c => (Catch: c, Region: r))), x => x.Catch, x => x)
            .ToDictionary(x => x.Key, x => x.Value.Select(v => v.Region).ToImmutableArray());
        members = Group(snapshot.Callables.Where(x => x.DeclaringType is not null), x => x.DeclaringType!.Value,
            x => x.OrderBy(c => c.DisplaySignature, StringComparer.Ordinal).ThenBy(c => c.Id.Value, StringComparer.Ordinal));
        implementations = Group(snapshot.Implementations, x => x.Declared, x => x.OrderBy(i => i.Implementation.Value, StringComparer.Ordinal).ThenBy(i => i.Kind));
        implemented = Group(snapshot.Implementations, x => x.Implementation, x => x.OrderBy(i => i.Declared.Value, StringComparer.Ordinal).ThenBy(i => i.Kind));
    }

    private static Dictionary<TKey, ImmutableArray<T>> Group<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, Func<IEnumerable<T>, IEnumerable<T>> order)
        where TKey : notnull => items.GroupBy(key).ToDictionary(g => g.Key, g => order(g).ToImmutableArray());

    public CallableFact Callable(CallableId id) => callables.TryGetValue(id, out var fact) ? fact : throw new KeyNotFoundException($"Unknown callable: {id.Value}");
    public bool TryGetCallable(CallableId id, out CallableFact fact) => callables.TryGetValue(id, out fact!);
    public bool ContainsCallable(CallableId id) => callables.ContainsKey(id);
    public TypeFact Type(TypeId id) => types.TryGetValue(id, out var fact) ? fact : throw new KeyNotFoundException($"Unknown type: {id.Value}");
    public bool TryGetType(TypeId id, out TypeFact fact) => types.TryGetValue(id, out fact!);
    public IReadOnlyDictionary<TypeId, TypeFact> Types => types;
    public CompilationFact? Compilation(CompilationId id) => compilations.GetValueOrDefault(id);
    // Project file name without extension; "metadata" for referenced assemblies outside the analysed solution.
    public string ProjectName(CompilationId id) => compilations.TryGetValue(id, out var fact) ? Path.GetFileNameWithoutExtension(fact.ProjectPath) : "metadata";
    public TypeFact? DeclaringType(CallableId id) => callables.TryGetValue(id, out var fact) && fact.DeclaringType is { } type ? types.GetValueOrDefault(type) : null;

    public CallSiteFact CallSite(CallSiteId id) => sites[id];
    // Ordered by target display, then site ID.
    public ImmutableArray<CallSiteFact> Outgoing(CallableId caller) => outgoing.GetValueOrDefault(caller, []);
    // Sites whose declared binding is the callable, ordered by site ID.
    public ImmutableArray<CallSiteFact> Incoming(CallableId target) => incoming.GetValueOrDefault(target, []);
    // Ordered by ID.
    public ImmutableArray<ThrowSiteFact> ThrowSites(CallableId owner) => throwsByOwner.GetValueOrDefault(owner, []);
    public ImmutableArray<CatchFact> Catches(CallableId owner) => catchesByOwner.GetValueOrDefault(owner, []);
    public ImmutableArray<AnalysisGap> Gaps(CallableId owner) => gapsByOwner.GetValueOrDefault(owner, []);
    public ThrowSiteFact ThrowSite(ThrowSiteId id) => throwSites[id];
    public bool TryGetThrowSite(ThrowSiteId id, out ThrowSiteFact fact) => throwSites.TryGetValue(id, out fact!);
    public CatchFact Catch(CatchId id) => catches[id];
    public ExceptionRegionFact Region(ExceptionRegionId id) => regions[id];
    // Regions listing the handler, in snapshot order.
    public ImmutableArray<ExceptionRegionFact> RegionsOf(CatchId handler) => regionsByCatch.GetValueOrDefault(handler, []);
    // Callables declared by the type, ordered by display signature, then ID.
    public ImmutableArray<CallableFact> Members(TypeId type) => members.GetValueOrDefault(type, []);

    // Transitive base classes and interfaces, breadth-first in declaration order; each type once, cycle-safe.
    // A base without a TypeFact is listed but not expanded; a base use without a declaration ID is skipped.
    public ImmutableArray<TypeId> Ancestors(TypeId type)
    {
        var result = ImmutableArray.CreateBuilder<TypeId>();
        var seen = new HashSet<TypeId> { type };
        var pending = new Queue<TypeId>(); pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            if (!types.TryGetValue(current, out var fact)) continue;
            foreach (var b in fact.BaseTypes)
                if (b.Declaration is { } id && seen.Add(id)) { result.Add(id); pending.Enqueue(id); }
        }
        return result.ToImmutable();
    }

    // Implementation candidates (never inferred runtime targets) for a declared member, ordered by implementation ID.
    public ImmutableArray<ImplementationFact> Implementations(CallableId declared) => implementations.GetValueOrDefault(declared, []);
    // Declared members the callable implements or overrides, ordered by declared ID.
    public ImmutableArray<ImplementationFact> Declarations(CallableId implementation) => implemented.GetValueOrDefault(implementation, []);
}
