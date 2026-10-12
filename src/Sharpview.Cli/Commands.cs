using System.Collections.Immutable;
using System.Text.Json;
using Sharpview.Core;
using Sharpview.Roslyn;

namespace Sharpview.Cli;

internal static class Commands
{
    internal static async Task<int> RunAsync(Arguments options, CancellationToken token)
    {
        var snapshot = await RoslynProgramLoader.LoadAsync(options.Request, token);
        var roots = options.Command is "chains" or "controllers" or "exceptions" ? SelectRoots(snapshot, options) : [];
        using var file = options.Output is null ? null : new StreamWriter(new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write));
        var output = (TextWriter?)file ?? Console.Out;
        var report = new TextReport(snapshot, output, options);
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
                    await JsonSerializer.SerializeAsync(file?.BaseStream ?? Console.OpenStandardOutput(),
                        new { schemaVersion = 2, producerVersion = "0.1.0", snapshot },
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }, token);
                else report.Inspect(token);
                break;
            case "chains":
            case "controllers":
                var chains = CallChainQuery.Enumerate(snapshot, roots, options.Traversal, token);
                foreach (var chain in chains.Chains) { token.ThrowIfCancellationRequested(); report.Chain(chain, options.Command == "controllers"); }
                var boundaries = chains.Chains.Count(x => x.End is ChainEnd.DispatchBoundary or ChainEnd.UnknownTarget or ChainEnd.NoBody);
                var depth = chains.Chains.Count(x => x.End == ChainEnd.DepthLimit);
                incomplete |= chains.OutputLimitReached || chains.WorkLimitReached || depth > 0 || boundaries > 0;
                Console.Error.WriteLine($"Policy: {options.Traversal.Dispatch}; roots: {roots.Length}; chains: {chains.Chains.Length}; examined sites: {chains.ExpandedSites}; uncertain boundaries: {boundaries}; depth limits: {depth}; output limit: {chains.OutputLimitReached}; work limit: {chains.WorkLimitReached}.");
                break;
            case "exceptions":
                report.Landscape(token);
                var exceptions = ExceptionQuery.Analyze(snapshot, roots, options.Traversal, token);
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

    private static ImmutableArray<CallableId> SelectRoots(ProgramSnapshot snapshot, Arguments options)
    {
        var source = snapshot.Callables.Where(x => x.Body != BodyStatus.MetadataOnly);
        if (options.Command is "controllers" or "exceptions")
        {
            var actions = snapshot.ControllerActions.Select(x => x.Method).ToHashSet();
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
