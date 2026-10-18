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
        else context.Report.Inspect(token);
        return false;
    }

    private static void Methods(ReportContext context, CancellationToken token)
    {
        var methods = context.Snapshot.Callables.Where(x => x.Body != BodyStatus.MetadataOnly)
            .OrderBy(x => x.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal);
        foreach (var method in methods)
        {
            token.ThrowIfCancellationRequested();
            context.Output.WriteLine($"{context.Report.Name(method.Id)} [{method.Kind}, {method.Body}]\n  ID: {method.Id.Value}");
        }
    }
}
