using System.Collections.Immutable;

namespace Sharpview.Core;

public enum ChainDispatchPolicy { Declared, Direct }
public enum ChainEnd { Leaf, Metadata, NoBody, DispatchBoundary, UnknownTarget, Cycle, DepthLimit }
public sealed record ChainOptions(ChainDispatchPolicy Dispatch = ChainDispatchPolicy.Declared,
    int MaxDepth = 20, int MaxChains = 1000, int MaxExpansions = 100000)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Dispatch)) throw new ArgumentException("Unknown dispatch policy.");
        if (MaxDepth < 0 || MaxChains < 1 || MaxExpansions < 1) throw new ArgumentException("Depth must be nonnegative; work and output limits must be positive.");
    }
}
public sealed record ChainStep(CallableId Caller, CallableId? Target, string TargetDisplay,
    DispatchKind Dispatch, ImmutableArray<CallSiteId> EvidenceSites, bool IsExcludedDispatch = false);
public sealed record CallChain(CallableId Root, ImmutableArray<ChainStep> Steps, ChainEnd End);
public sealed record ChainSearchResult(ImmutableArray<CallChain> Chains, bool OutputLimitReached, bool WorkLimitReached, int ExpandedSites);

public sealed class CallIndex
{
    public IReadOnlyDictionary<CallableId, CallableFact> Methods { get; }
    public IReadOnlyDictionary<CallableId, ImmutableArray<ChainStep>> Outgoing { get; }
    public CallIndex(ProgramSnapshot snapshot) : this(new ProgramIndex(snapshot)) { }
    public CallIndex(ProgramIndex index)
    {
        Methods = index.Snapshot.Callables.ToDictionary(x => x.Id);
        // Grouping collapses repeated relationships; the explicit order below makes the result independent of input order.
        Outgoing = index.Snapshot.Callables.Select(x => x.Id).Where(x => !index.Outgoing(x).IsEmpty).ToDictionary(x => x, x => index.Outgoing(x)
            .GroupBy(x => (x.BoundTarget, x.Dispatch, Unresolved: x.BoundTarget is null ? x.Id.Value : ""))
            .Select(e => new ChainStep(e.First().Caller, e.First().BoundTarget, e.First().TargetDisplay, e.First().Dispatch,
                e.OrderBy(x => x.Id.Value, StringComparer.Ordinal).Select(x => x.Id).ToImmutableArray()))
            .OrderBy(x => x.TargetDisplay, StringComparer.Ordinal).ThenBy(x => x.EvidenceSites[0].Value, StringComparer.Ordinal).ToImmutableArray());
    }
}
public static class CallChainQuery
{
    // Frames carry independent path state. The explicit stack avoids recursion at user-supplied depths.
    public static ChainSearchResult Enumerate(ProgramSnapshot snapshot, ImmutableArray<CallableId> roots,
        ChainOptions options, CancellationToken cancellationToken = default) => Enumerate(new ProgramIndex(snapshot), roots, options, cancellationToken);
    public static ChainSearchResult Enumerate(ProgramIndex program, ImmutableArray<CallableId> roots,
        ChainOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var index = new CallIndex(program);
        foreach (var root in roots) if (!index.Methods.ContainsKey(root)) throw new ArgumentException($"Unknown root ID: {root.Value}");
        var output = ImmutableArray.CreateBuilder<CallChain>();
        var work = 0; var outputLimited = false; var workLimited = false;
        var stack = new Stack<(CallableId Root, CallableId Current, ImmutableArray<ChainStep> Path, ImmutableHashSet<CallableId> Seen, ChainEnd? End)>();
        foreach (var root in roots.Distinct().OrderByDescending(x => index.Methods[x].DisplaySignature, StringComparer.Ordinal).ThenByDescending(x => x.Value, StringComparer.Ordinal))
            stack.Push((root, root, [], ImmutableHashSet.Create(root), null));
        while (stack.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edges = index.Outgoing.GetValueOrDefault(frame.Current, []);
            var end = frame.End;
            if (end is null && index.Methods[frame.Current].Body != BodyStatus.Source)
                end = index.Methods[frame.Current].Body == BodyStatus.MetadataOnly ? ChainEnd.Metadata : ChainEnd.NoBody;
            if (end is null && edges.IsEmpty) end = ChainEnd.Leaf;
            if (end is null && frame.Path.Length >= options.MaxDepth) end = ChainEnd.DepthLimit;
            if (end is not null)
            {
                if (output.Count >= options.MaxChains) { outputLimited = true; break; }
                output.Add(new(frame.Root, frame.Path, end.Value)); continue;
            }
            // Charge actual evidence sites, including duplicates, not just collapsed relationships.
            var pending = new List<(ChainStep Step, ChainEnd? End)>();
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edge.EvidenceSites.Length > options.MaxExpansions - work) { workLimited = true; break; }
                work += edge.EvidenceSites.Length;
                ChainEnd? terminal = edge.Target is null || !index.Methods.ContainsKey(edge.Target.Value) ? ChainEnd.UnknownTarget
                    : edge.Dispatch != DispatchKind.Direct ? ChainEnd.DispatchBoundary
                    : frame.Seen.Contains(edge.Target.Value) ? ChainEnd.Cycle : null;
                pending.Add((edge with { IsExcludedDispatch = options.Dispatch == ChainDispatchPolicy.Direct && edge.Dispatch != DispatchKind.Direct }, terminal));
            }
            // Retain already examined paths when the work budget stops this expansion.
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var (edge, terminal) = pending[i];
                var target = edge.Target ?? frame.Current;
                stack.Push((frame.Root, target, frame.Path.Add(edge), frame.Seen.Add(target), terminal));
            }
            if (workLimited)
            {
                // Do not descend further. Emit evaluated terminal branches only.
                while (stack.TryPop(out var stopped))
                {
                    if (stopped.End is null) continue;
                    if (output.Count >= options.MaxChains) { outputLimited = true; break; }
                    output.Add(new(stopped.Root, stopped.Path, stopped.End.Value));
                }
                break;
            }
        }
        return new(output.ToImmutable(), outputLimited, workLimited, work);
    }
}
public static class DependencyQueries
{
    public static IEnumerable<DependencyFact> SignatureUses(ProgramSnapshot snapshot, CallableId callable) =>
        snapshot.Dependencies.Where(x => x.OwningCallable == callable && x.Kind == DependencyKind.Signature);
}
