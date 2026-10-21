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
        Console.Error.WriteLine($"{Display.Plural(paths.Chains.Length, "path")} from {Display.Plural(roots.Length, "root")}.");
        if (depth > 0) Console.Error.WriteLine($"{Display.Plural(depth, "path")} cut at --max-depth {context.Arguments.MaxDepth}; raise it to see further.");
        if (paths.OutputLimitReached) Console.Error.WriteLine($"Stopped after {context.Arguments.MaxPaths} paths; raise --max-paths to see the rest.");
        if (paths.WorkLimitReached) Console.Error.WriteLine($"Stopped early: the analysis budget ran out, so the list above is incomplete. Rerun with --budget {context.Arguments.Budget * 10}.");
        return ValueTask.FromResult(paths.OutputLimitReached || paths.WorkLimitReached || depth > 0 || boundaries > 0);
    }

    // One line per path, from the root to where it ends; the end is named when it is not simply a method without calls.
    private static void Write(ReportContext context, CallChain path)
    {
        var (index, display, output) = (context.Index, context.Display, context.Output);
        var names = new List<string> { display.Short(path.Root) };
        foreach (var step in path.Steps)
            names.Add(step.Target is { } target && path.End != ChainEnd.DispatchBoundary ? display.Short(target) : $"[{step.Dispatch.ToString().ToLowerInvariant()} call {step.TargetDisplay}]");
        var end = path.End switch
        {
            ChainEnd.NoBody => "  (ends: no body)",
            ChainEnd.UnknownTarget => "  (ends: a call that does not compile)",
            ChainEnd.Cycle => "  (ends: recursion)",
            ChainEnd.DepthLimit => "  (ends: --max-depth)",
            _ => ""
        };
        output.WriteLine(string.Join(" -> ", names) + end);
        if (context.Arguments.Locations)
            foreach (var step in path.Steps)
                foreach (var site in step.EvidenceSites) output.WriteLine($"    at {display.Where(index.CallSite(site).Evidence)}");
    }
}
