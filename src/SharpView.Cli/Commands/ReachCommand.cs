using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

internal sealed class ReachCommand : ReportCommand
{
    public override ImmutableHashSet<string> Options { get; } = ["--root", "--follow-candidates", "--max-depth", "--budget", "--locations"];

    public override void Validate(Arguments arguments) => Query(arguments).Validate();

    private static ReachOptions Query(Arguments arguments) => new(arguments.FollowCandidates, arguments.MaxDepth, arguments.Budget);

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var (index, arguments) = (context.Index, context.Arguments);
        var roots = Roots.Select(index, arguments, RootScope.ActionsUnlessExplicit, token);
        var reach = ReachabilityQuery.Analyze(index, roots, Query(arguments), token);
        foreach (var root in reach.Roots) { token.ThrowIfCancellationRequested(); Write(context, root); }
        var stops = reach.Roots.Sum(x => x.Boundaries.Length);
        Console.Error.WriteLine($"Roots: {roots.Length} (reported: {reach.Roots.Length}); reached: {reach.Roots.Sum(x => x.Reached.Length)}; boundaries: {stops}; candidates followed: {arguments.FollowCandidates}; examined sites: {reach.ExpandedSites}; depth limit: {reach.Roots.Any(x => x.DepthLimitReached)}; work limit: {reach.WorkLimitReached}.");
        return ValueTask.FromResult(reach.WorkLimitReached || stops > 0);
    }

    // Each reached method once with its distance; the path when it is not a direct callee; then where traversal stopped.
    private static void Write(ReportContext context, RootReach reach)
    {
        var (index, display, output, arguments) = (context.Index, context.Display, context.Output, context.Arguments);
        output.WriteLine($"Root: {display.Name(reach.Root)}");
        foreach (var reached in reach.Reached)
        {
            output.WriteLine($"  [{reached.Distance}] {display.Name(reached.Callable)} | Project: {display.Project(reached.Callable)}{(reached.ViaCandidate ? " (candidate)" : "")}");
            if (reached.Distance >= 2 || reached.WitnessUsesCandidate)
                output.WriteLine($"    Path: {display.Name(reach.Root)}{string.Concat(reached.Witness.Select(x => (x.Candidate ? " -[candidate]-> " : " --> ") + display.Name(x.Target)))}");
            if (arguments.FollowCandidates)
            {
                output.WriteLine($"    Direct distance: {reached.DirectDistance?.ToString() ?? "not established"}");
                if (reached.DirectDistance is not null && reached.WitnessUsesCandidate)
                {
                    output.WriteLine($"    Direct path: {string.Join(" --> ", reached.DirectWitness.Select(x => display.Name(x.Target)).Prepend(display.Name(reach.Root)))}");
                    if (arguments.Locations) foreach (var step in reached.DirectWitness)
                        output.WriteLine($"    Direct evidence: {Display.Location(index.CallSite(step.Site).Evidence)}");
                }
            }
            if (arguments.Locations)
                foreach (var step in reached.Witness)
                    output.WriteLine($"    Evidence: {Display.Location(index.CallSite(step.Site).Evidence)} | {(step.Candidate ? "implementation candidate" : "direct call")}");
        }
        foreach (var boundary in reach.Boundaries)
        {
            var where = boundary.Site is { } site ? Display.Location(index.CallSite(site).Evidence)
                : index.Callable(boundary.Caller).Declarations.FirstOrDefault() is { } declaration ? Display.Location(declaration) : "no source";
            output.WriteLine(boundary.Kind switch
            {
                ReachBoundaryKind.Dispatch => $"  Boundary: {boundary.Dispatch} dispatch --> {display.Name(boundary.DeclaredTarget!.Value)} | candidates followed: {boundary.CandidatesFollowed} | {where}",
                ReachBoundaryKind.Unresolved => $"  Boundary: unresolved target in {display.Name(boundary.Caller)} | {where}",
                ReachBoundaryKind.MissingBody => $"  Boundary: body unavailable for {display.Name(boundary.Caller)} | {where}",
                _ => $"  Boundary: depth limit at {display.Name(boundary.Caller)} | {where}"
            });
        }
    }
}
