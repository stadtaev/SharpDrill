using System.Collections.Immutable;
using Sharpview.Core;

namespace Sharpview.Cli;

internal sealed class InspectCommand : ReportCommand
{
    public override ImmutableHashSet<string> Options { get; } = ["--methods", "--json", "--output"];

    public override void Validate(Arguments arguments)
    {
        if (arguments.Methods && arguments.Json) throw new ArgumentException("inspect accepts --methods or --json, not both.");
        if (arguments.Output is not null && !arguments.Json) throw new ArgumentException("--output requires inspect --json.");
    }

    protected override async ValueTask<bool> ExecuteAsync(ReportContext context, CancellationToken token)
    {
        if (context.Arguments.Json) await SnapshotJson.WriteAsync(context.OutputFile ?? Console.OpenStandardOutput(), context.Snapshot, "0.1.0", token);
        else if (context.Arguments.Methods) Methods(context, token);
        else Summary(context, token);
        return false;
    }

    private static void Methods(ReportContext context, CancellationToken token)
    {
        var methods = context.Snapshot.Callables.Where(x => x.Body != BodyStatus.MetadataOnly)
            .OrderBy(x => x.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal);
        foreach (var method in methods)
        {
            token.ThrowIfCancellationRequested();
            context.Output.WriteLine($"{context.Display.Name(method.Id)} [{method.Kind}, {method.Body}]\n  ID: {method.Id.Value}");
        }
    }

    // Loaded projects, fact counts, dispatch kinds, then every throw, catch and boundary registration with evidence.
    private static void Summary(ReportContext context, CancellationToken token)
    {
        var (index, snapshot, display, output) = (context.Index, context.Snapshot, context.Display, context.Output);
        foreach (var compilation in snapshot.Compilations)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Project: {compilation.ProjectPath} | Framework: {compilation.TargetFramework} | Configuration: {compilation.Configuration}");
        }
        output.WriteLine($"Types: {snapshot.Types.Length}; callables: {snapshot.Callables.Length}; calls: {snapshot.CallSites.Length}; dependencies: {snapshot.Dependencies.Length}; throws: {snapshot.ThrowSites.Length}; catches: {snapshot.Catches.Length}; actions: {AspNetConventions.ControllerActions(index).Length}; gaps: {snapshot.Gaps.Length}");
        foreach (var group in snapshot.CallSites.GroupBy(x => x.Dispatch).OrderBy(x => x.Key)) output.WriteLine($"Dispatch {group.Key}: {group.Count()}");
        output.WriteLine("Exception landscape (explicit source origins; static types are separate from exact construction evidence):");
        foreach (var origin in snapshot.ThrowSites)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Project: {display.Project(origin.Owner)}\nOrigin: {display.Name(origin.Owner)}\n  Type: {index.DeclaringType(origin.Owner)?.DisplayName}\n  Throw: {origin.Kind} | Static exception type: {origin.StaticExceptionType?.DisplayName ?? "unknown / original for rethrow"} ({(origin.IsExactType ? "static type" : "static; runtime type may differ")})\n  Exact construction type: {origin.ExactExceptionType?.DisplayName ?? (origin.IsExactType ? origin.StaticExceptionType?.DisplayName : null) ?? "unknown"}\n  Evidence: {Display.Location(origin.Evidence)}\n  Origin ID: {origin.Id.Value}");
        }
        foreach (var handler in snapshot.Catches)
        {
            token.ThrowIfCancellationRequested();
            output.WriteLine($"Catch: {handler.CaughtType?.DisplayName ?? "all exceptions"} | Handler: {handler.Outcome}\n  Project: {display.Project(handler.Owner)} | Method: {display.Name(handler.Owner)}\n  Filter: {handler.Filter ?? "none"}\n  Evidence: {Display.Location(handler.Evidence)}");
            foreach (var region in index.RegionsOf(handler.Id))
                output.WriteLine($"  Protected region: {Display.Location(region.ProtectedBlock)} | span {region.ProtectedBlock.SpanStart}+{region.ProtectedBlock.SpanLength} | Finally: {region.HasFinally}");
            if (handler.SubsequentThrow is { } next && index.TryGetThrowSite(next, out var nextThrow)) output.WriteLine($"  Subsequent origin: {Display.Location(nextThrow.Evidence)} ({next.Value})");
        }
        output.WriteLine("Boundary registrations (evidence only; runtime coverage and middleware order unknown):");
        foreach (var boundary in snapshot.BoundaryHandling)
            output.WriteLine($"  {boundary.Registration} | Project: {index.ProjectName(boundary.Compilation)} | {boundary.BoundMethod} | {Display.Location(boundary.Evidence)}");
        if (snapshot.BoundaryHandling.IsEmpty) output.WriteLine("  No supported registration evidence found; boundary handling unknown.");
    }
}
