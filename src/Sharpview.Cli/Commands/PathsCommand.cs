using System.Collections.Immutable;
using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class PathsCommand : ReportCommand
{
    public override ImmutableHashSet<string> Options { get; } = ["--root", "--max-depth", "--max-paths", "--budget", "--locations"];

    public override void Validate(Arguments arguments) => Query(arguments).Validate();

    private static ChainOptions Query(Arguments arguments) => new(arguments.MaxDepth, arguments.MaxPaths, arguments.Budget);

    protected override ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        var index = context.Index;
        var roots = Roots.Select(index, context.Arguments, RootScope.ActionsUnlessExplicit, token);
        var paths = CallChainQuery.Enumerate(index, roots, Query(context.Arguments), token);
        foreach (var path in paths.Chains) { token.ThrowIfCancellationRequested(); context.Report.Chain(path); }
        var boundaries = paths.Chains.Count(x => x.End is ChainEnd.DispatchBoundary or ChainEnd.UnknownTarget or ChainEnd.NoBody);
        var depth = paths.Chains.Count(x => x.End == ChainEnd.DepthLimit);
        Console.Error.WriteLine($"Roots: {roots.Length}; paths: {paths.Chains.Length}; examined sites: {paths.ExpandedSites}; uncertain boundaries: {boundaries}; depth limits: {depth}; output limit: {paths.OutputLimitReached}; work limit: {paths.WorkLimitReached}.");
        return ValueTask.FromResult(paths.OutputLimitReached || paths.WorkLimitReached || depth > 0 || boundaries > 0);
    }
}
