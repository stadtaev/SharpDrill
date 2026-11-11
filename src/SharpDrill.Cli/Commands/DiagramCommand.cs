using System.Collections.Immutable;
using System.Globalization;
using SharpDrill.Core;
using SharpDrill.Export.Mermaid;

namespace SharpDrill.Cli;

internal sealed class DiagramCommand(TextReader input, TextWriter output) : ICliCommand
{
    public ImmutableHashSet<string> Options { get; } = ["--from", "--declared-only", "--max-depth", "--max-nodes", "--max-edges", "--budget", "--output"];

    public void Validate(Arguments arguments) => Query(arguments).Validate();

    private static DiagramOptions Query(Arguments arguments) =>
        new(!arguments.DeclaredOnly, arguments.MaxDepth, arguments.MaxNodes, arguments.MaxEdges, arguments.Budget);

    public async Task<int> RunAsync(ProgramIndex index, Arguments arguments, CancellationToken token)
    {
        CallableFact selected;
        if (arguments.From is { } from) selected = index.EntryPoint(from, token).Method;
        else
        {
            var entries = index.EntryPoints(token).Select(x => x.Method).ToArray();
            if (entries.Length == 0) { await output.WriteLineAsync("No entry points found. No diagram saved."); return 0; }
            await output.WriteLineAsync("Entry points:");
            for (var i = 0; i < entries.Length; i++)
                await output.WriteLineAsync($"{i + 1}. {entries[i].DisplaySignature} [{index.ProjectName(entries[i].Compilation)}]");
            await output.WriteLineAsync("Choose an entry point number (q to cancel):");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var choice = await input.ReadLineAsync(token);
                if (choice is null || choice.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                { await output.WriteLineAsync("No diagram saved."); return 0; }
                if (int.TryParse(choice.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= entries.Length)
                { selected = entries[number - 1]; break; }
                await output.WriteLineAsync($"Choose a number from 1 to {entries.Length}, or q to cancel:");
            }
        }
        var diagram = CallDiagramQuery.Build(index, selected.Id, Query(arguments), token);
        var document = MermaidExporter.Markdown(diagram);
        var path = await MarkdownExport.SaveAsync(document, arguments.Output, selected.DisplaySignature, token);
        await output.WriteLineAsync("Saved diagram: " + path);
        if (!diagram.Limits.IsEmpty) await output.WriteLineAsync($"The diagram is cut short ({string.Join(", ", diagram.Limits).ToLowerInvariant()}); raise --max-depth, --max-nodes or --max-edges to see more.");
        return diagram.IsComplete ? 0 : 1;
    }
}
