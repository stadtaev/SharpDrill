using System.Collections.Immutable;
using Sharpview.Core;
using Sharpview.Roslyn;

namespace Sharpview.Cli;

internal static class Commands
{
    internal static async Task<int> RunAsync(Arguments options, CancellationToken token)
    {
        var snapshot = await RoslynProgramLoader.LoadAsync(options.Request, token);
        var index = new ProgramIndex(snapshot);
        if (options.Command == "diagram") return await DiagramCommand.RunAsync(index, options, Console.In, Console.Out, token);
        var roots = options.Command is "chains" or "controllers" or "exceptions" or "reach" ? SelectRoots(index, options, token) : [];
        using var file = options.Output is null ? null : new StreamWriter(new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write));
        var output = (TextWriter?)file ?? Console.Out;
        var report = new TextReport(index, output, options);
        var incomplete = !snapshot.Gaps.IsEmpty;
        switch (options.Command)
        {
            case "methods":
                foreach (var method in snapshot.Callables.Where(x => x.Body != BodyStatus.MetadataOnly).OrderBy(x => x.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    output.WriteLine($"{report.Name(method.Id)} [{method.Kind}, {method.Body}]\n  ID: {method.Id.Value}");
                }
                break;
            case "inspect":
                if (options.Format == "json")
                    await SnapshotJson.WriteAsync(file?.BaseStream ?? Console.OpenStandardOutput(), snapshot, "0.1.0", token);
                else report.Inspect(token);
                break;
            case "chains":
            case "controllers":
                var chains = CallChainQuery.Enumerate(index, roots, options.Traversal, token);
                foreach (var chain in chains.Chains) { token.ThrowIfCancellationRequested(); report.Chain(chain, options.Command == "controllers"); }
                var boundaries = chains.Chains.Count(x => x.End is ChainEnd.DispatchBoundary or ChainEnd.UnknownTarget or ChainEnd.NoBody);
                var depth = chains.Chains.Count(x => x.End == ChainEnd.DepthLimit);
                incomplete |= chains.OutputLimitReached || chains.WorkLimitReached || depth > 0 || boundaries > 0;
                Console.Error.WriteLine($"Policy: {options.Traversal.Dispatch}; roots: {roots.Length}; chains: {chains.Chains.Length}; examined sites: {chains.ExpandedSites}; uncertain boundaries: {boundaries}; depth limits: {depth}; output limit: {chains.OutputLimitReached}; work limit: {chains.WorkLimitReached}.");
                break;
            case "reach":
                var reach = ReachabilityQuery.Analyze(index, roots, new(options.FollowCandidates, options.Traversal.MaxDepth, options.Traversal.MaxExpansions), token);
                foreach (var root in reach.Roots) { token.ThrowIfCancellationRequested(); report.Reach(root); }
                var stops = reach.Roots.Sum(x => x.Boundaries.Length);
                incomplete |= reach.WorkLimitReached || stops > 0;
                Console.Error.WriteLine($"Roots: {roots.Length} (reported: {reach.Roots.Length}); reached: {reach.Roots.Sum(x => x.Reached.Length)}; boundaries: {stops}; candidates followed: {options.FollowCandidates}; examined sites: {reach.ExpandedSites}; depth limit: {reach.Roots.Any(x => x.DepthLimitReached)}; work limit: {reach.WorkLimitReached}.");
                break;
            case "layers":
                var graph = LayerGraph.Build(index, report.Layers, token);
                var findings = LayerRules.AllowedDependencies(graph, report.Layers.Model);
                report.LayerOverview(graph, findings);
                foreach (var name in options.Allowed.SelectMany(x => new[] { x.From, x.To }).Distinct().Where(x => !report.Layers.Model.Layers.Contains(x)))
                    Console.Error.WriteLine($"Warning: --allow names layer '{name}', which no --layer mapping defines.");
                var violations = findings.Count(x => x.Verdict == Verdict.Violation);
                var undecided = findings.Count(x => x.Verdict == Verdict.Unknown);
                incomplete |= violations > 0 || undecided > 0;
                Console.Error.WriteLine($"Layers: {string.Join(", ", report.Layers.Model.Layers)}; edges: {graph.Edges.Length}; external references: {graph.ExternalReferences}; unresolved calls: {graph.UnresolvedCalls}; rule evaluated: {!options.Allowed.IsEmpty}; violations: {violations}; unknown: {undecided}.");
                break;
            case "exceptions":
                if (options.Summary)
                {
                    var summaries = ExceptionSummaryQuery.Analyze(index, roots, new(options.Traversal.MaxExpansions, options.MaxFacts), token);
                    foreach (var root in roots.OrderBy(x => index.Callable(x).DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        var summary = summaries.Roots[root];
                        output.WriteLine($"Exception summary: {report.Name(root)}");
                        output.WriteLine($"  Explicit escapes: {summary.Escapes.Length}; unknown effects: {summary.Unknowns.Length}; complete within model: {summary.IsComplete}.");
                        foreach (var escape in summary.Escapes)
                        {
                            output.WriteLine($"  May escape: {escape.ExceptionType ?? "unknown type"} | origin: {escape.Origin.Value} | {escape.ReasonCode}");
                            var witness = summaries.GetWitness(root, escape);
                            output.WriteLine($"    Path: {string.Join(" --> ", witness.Path.Select(report.Name))}");
                            if (index.TryGetThrowSite(escape.Origin, out var origin)) output.WriteLine($"    Origin evidence: {TextReport.Location(origin.Evidence)}");
                            if (escape.SubsequentOrigin is { } subsequent && index.TryGetThrowSite(subsequent, out var replacement))
                                output.WriteLine($"    Subsequent origin: {subsequent.Value} | {TextReport.Location(replacement.Evidence)}");
                            if (options.Locations) foreach (var site in witness.Sites) output.WriteLine($"    Call-site evidence: {TextReport.Location(index.CallSite(site).Evidence)}");
                        }
                        foreach (var uncertainty in summary.Unknowns)
                            output.WriteLine($"  Unknown [{uncertainty.ReasonCode}]: {uncertainty.Message}" +
                                (uncertainty.Site is { } site ? $" | {TextReport.Location(index.CallSite(site).Evidence)}" : uncertainty.Evidence is { } evidence ? $" | {TextReport.Location(evidence)}" : ""));
                        foreach (var boundary in snapshot.BoundaryHandling.Where(x => x.Compilation == index.Callable(root).Compilation))
                            output.WriteLine($"  Boundary registration evidence (runtime coverage unknown): {boundary.Registration} | {TextReport.Location(boundary.Evidence)}");
                    }
                    incomplete |= summaries.WorkLimitReached || summaries.FactLimitReached || summaries.Roots.Values.Any(x => !x.IsComplete);
                    Console.Error.WriteLine($"Summary roots: {summaries.Roots.Count}; methods: {summaries.Methods.Count}; facts: {summaries.FactsAdded}; transfer work: {summaries.WorkItemsProcessed}; work limit: {summaries.WorkLimitReached}; fact limit: {summaries.FactLimitReached}.");
                    break;
                }
                report.Landscape(token);
                var exceptions = ExceptionQuery.Analyze(index, roots, options.Traversal, token);
                foreach (var witness in exceptions.Results) { token.ThrowIfCancellationRequested(); report.Witness(witness); }
                var unknown = exceptions.Results.Count(x => x.Status == ExceptionStatus.Unknown || x.SubsequentStatus == ExceptionStatus.Unknown);
                incomplete |= exceptions.OutputLimitReached || exceptions.WorkLimitReached || exceptions.DepthLimitReached || unknown > 0;
                Console.Error.WriteLine($"Action roots: {roots.Length}; witnesses: {exceptions.Results.Length}; unknown: {unknown}; examined sites: {exceptions.ExpandedSites}; depth limit: {exceptions.DepthLimitReached}; output limit: {exceptions.OutputLimitReached}; work limit: {exceptions.WorkLimitReached}.");
                break;
        }
        foreach (var gap in snapshot.Gaps.Take(20)) Console.Error.WriteLine($"Gap [{gap.Kind}]: {gap.Message}" + (gap.Evidence is null ? "" : $" ({TextReport.Location(gap.Evidence)})"));
        if (snapshot.Gaps.Length > 20) Console.Error.WriteLine($"Further gaps: {snapshot.Gaps.Length - 20}; inspect JSON for full evidence.");
        Console.Error.WriteLine($"Extraction gaps: {snapshot.Gaps.Length}. Coverage: explicit origins/direct calls; minimal APIs and inherited actions outside scope.");
        token.ThrowIfCancellationRequested();
        return incomplete ? 1 : 0;
    }

    private static ImmutableArray<CallableId> SelectRoots(ProgramIndex index, Arguments options, CancellationToken token)
    {
        var source = index.Snapshot.Callables.Where(x => x.Body != BodyStatus.MetadataOnly);
        // reach defaults to controller actions but accepts any explicit source root.
        if (options.Command is "controllers" or "exceptions" || options.Command == "reach" && options.Root is null && options.RootId is null)
        {
            var actions = AspNetConventions.ControllerActions(index, token).Select(x => x.Method).ToHashSet();
            source = source.Where(x => actions.Contains(x.Id));
        }
        if (options.Root is not null || options.RootId is not null)
        {
            var matches = source.Where(x => options.RootId is not null ? x.Id.Value == options.RootId : x.DisplaySignature == options.Root).ToArray();
            if (matches.Length == 0) throw new ArgumentException("No source callable matches the selected root (controllers/exceptions require a recognised action).");
            if (matches.Length > 1) throw new ArgumentException("Ambiguous signature; use --root-id:\n" + string.Join("\n", matches.Select(x => x.Id.Value)));
            return [matches[0].Id];
        }
        return source.Select(x => x.Id).ToImmutableArray();
    }
}
