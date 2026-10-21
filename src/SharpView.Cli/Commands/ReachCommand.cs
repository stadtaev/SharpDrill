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
        Console.Error.WriteLine($"{Display.Plural(roots.Length, "root")} {(roots.Length == 1 ? "reaches" : "reach")} {Display.Plural(reach.Roots.SelectMany(x => x.Reached).Select(x => x.Callable).Distinct().Count(), "method")}.");
        if (reach.Roots.Any(x => x.DepthLimitReached)) Console.Error.WriteLine($"Some calls lie deeper than --max-depth {arguments.MaxDepth}; raise it to see them.");
        if (reach.WorkLimitReached) Console.Error.WriteLine($"Stopped early: the analysis budget ran out, so the list above is incomplete. Rerun with --budget {arguments.Budget * 10}.");
        return ValueTask.FromResult(reach.WorkLimitReached || stops > 0);
    }

    // Each reached method once with its distance and, beyond direct callees, the methods in between; then where it stopped.
    private static void Write(ReportContext context, RootReach reach)
    {
        var (index, display, output, arguments) = (context.Index, context.Display, context.Output, context.Arguments);
        output.WriteLine(display.Short(reach.Root));
        foreach (var reached in reach.Reached)
        {
            var via = reached.Witness.Length > 1 ? "  via " + string.Join(" -> ", reached.Witness.SkipLast(1).Select(x => display.Short(x.Target))) : "";
            output.WriteLine($"  [{reached.Distance}] {display.Short(reached.Callable)}  ({display.Project(reached.Callable)}){via}{(reached.WitnessUsesCandidate ? "  (through an assumed implementation)" : "")}");
            if (arguments.Locations) foreach (var step in reached.Witness) output.WriteLine($"      at {display.Where(index.CallSite(step.Site).Evidence)}");
        }
        foreach (var boundary in reach.Boundaries)
        {
            var where = boundary.Site is { } site ? display.Where(index.CallSite(site).Evidence)
                : index.Callable(boundary.Caller).Declarations.FirstOrDefault() is { } declaration ? display.Where(declaration) : "";
            output.WriteLine(boundary.Kind switch
            {
                ReachBoundaryKind.Dispatch => $"  Stops at: {boundary.Dispatch.ToString().ToLowerInvariant()} call to {display.Short(boundary.DeclaredTarget!.Value)}{(arguments.FollowCandidates ? " (no implementation in source)" : " (--follow-candidates follows its implementations)")}  {where}",
                ReachBoundaryKind.Unresolved => $"  Stops at: a call that does not compile in {display.Short(boundary.Caller)}  {where}",
                ReachBoundaryKind.MissingBody => $"  Stops at: {display.Short(boundary.Caller)}, which has no body  {where}",
                _ => $"  Stops at: --max-depth in {display.Short(boundary.Caller)}  {where}"
            });
        }
    }
}
