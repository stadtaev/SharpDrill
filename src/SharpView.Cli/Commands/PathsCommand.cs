using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

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
        foreach (var path in paths.Chains) { token.ThrowIfCancellationRequested(); Write(context, path); }
        var boundaries = paths.Chains.Count(x => x.End is ChainEnd.DispatchBoundary or ChainEnd.UnknownTarget or ChainEnd.NoBody);
        var depth = paths.Chains.Count(x => x.End == ChainEnd.DepthLimit);
        Console.Error.WriteLine($"Roots: {roots.Length}; paths: {paths.Chains.Length}; examined sites: {paths.ExpandedSites}; uncertain boundaries: {boundaries}; depth limits: {depth}; output limit: {paths.OutputLimitReached}; work limit: {paths.WorkLimitReached}.");
        return ValueTask.FromResult(paths.OutputLimitReached || paths.WorkLimitReached || depth > 0 || boundaries > 0);
    }

    // One line for the path and why it ends, then each method on it with project, type and distance from the root.
    private static void Write(ReportContext context, CallChain path)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var names = new List<string> { display.Name(path.Root) };
        foreach (var step in path.Steps)
            names.Add(step.Target is { } target ? display.Name(target) : $"[{step.Dispatch.ToString().ToLowerInvariant()} invocation: {step.TargetDisplay}]");
        var marker = path.End switch
        {
            ChainEnd.Leaf => " [no outgoing calls]",
            ChainEnd.Metadata => " [metadata body unavailable]",
            ChainEnd.NoBody => " [body unavailable]",
            ChainEnd.UnknownTarget => " [unresolved target]",
            ChainEnd.Cycle => " [cycle]",
            ChainEnd.DepthLimit => " [max depth]",
            ChainEnd.DispatchBoundary => $" [{path.Steps[^1].Dispatch.ToString().ToLowerInvariant()} target]",
            _ => ""
        };
        output.WriteLine(string.Join(" --> ", names) + marker);
        Method(path.Root, 0);
        for (var i = 0; i < path.Steps.Length; i++) if (path.Steps[i].Target is { } target) Method(target, i + 1);
        if (context.Arguments.Locations)
            foreach (var step in path.Steps)
                foreach (var site in step.EvidenceSites) output.WriteLine($"  Evidence: {Display.Location(index.CallSite(site).Evidence)} | {step.Dispatch} declared binding");

        void Method(CallableId id, int transitions)
        {
            if (index.TryGetCallable(id, out _))
                output.WriteLine($"  {display.Name(id)} | Project: {display.Project(id)} | Type: {display.TypeOf(id)} | Transitions: {transitions}");
        }
    }
}
