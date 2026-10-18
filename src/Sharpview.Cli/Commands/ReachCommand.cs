using System.Collections.Immutable;
using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class ReachCommand : ReportCommand
{
    public override ImmutableHashSet<string> Options { get; } = ["--root", "--layer", "--follow-candidates", "--max-depth", "--budget", "--locations"];

    public override void Validate(Arguments arguments) => Query(arguments).Validate();

    private static ReachOptions Query(Arguments arguments) => new(arguments.FollowCandidates, arguments.MaxDepth, arguments.Budget);

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var (index, arguments) = (context.Index, context.Arguments);
        var roots = Roots.Select(index, arguments, RootScope.ActionsUnlessExplicit, token);
        var reach = ReachabilityQuery.Analyze(index, roots, Query(arguments), token);
        foreach (var root in reach.Roots) { token.ThrowIfCancellationRequested(); context.Report.Reach(root); }
        var stops = reach.Roots.Sum(x => x.Boundaries.Length);
        Console.Error.WriteLine($"Roots: {roots.Length} (reported: {reach.Roots.Length}); reached: {reach.Roots.Sum(x => x.Reached.Length)}; boundaries: {stops}; candidates followed: {arguments.FollowCandidates}; examined sites: {reach.ExpandedSites}; depth limit: {reach.Roots.Any(x => x.DepthLimitReached)}; work limit: {reach.WorkLimitReached}.");
        return ValueTask.FromResult(reach.WorkLimitReached || stops > 0);
    }
}
