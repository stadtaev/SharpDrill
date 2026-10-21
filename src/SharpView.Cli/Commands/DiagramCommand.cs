using System.Collections.Immutable;
using System.Globalization;
using SharpView.Core;
using SharpView.Export.Mermaid;

namespace SharpView.Cli;

internal sealed class DiagramCommand(TextReader input, TextWriter output) : ICliCommand
{
    public ImmutableHashSet<string> Options { get; } = ["--root", "--follow-candidates", "--max-depth", "--max-nodes", "--max-edges", "--budget", "--output"];

    public void Validate(Arguments arguments) => Query(arguments).Validate();

    private static DiagramOptions Query(Arguments arguments) =>
        new(arguments.FollowCandidates, arguments.MaxDepth, arguments.MaxNodes, arguments.MaxEdges, arguments.Budget);

    public async Task<int> RunAsync(ProgramIndex index, Arguments arguments, CancellationToken token)
    {
        var actions = AspNetConventions.ControllerActions(index, token).Select(x => index.Callable(x.Method))
            .OrderBy(x => x.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
        CallableFact? selected;
        if (arguments.Root is not null) selected = Roots.Match(actions, arguments, "diagram requires a recognised action");
        else
        {
            if (actions.Length == 0) { await output.WriteLineAsync("No recognised controller actions. No diagram saved."); return 0; }
            await output.WriteLineAsync("Controller actions:");
            for (var i = 0; i < actions.Length; i++)
                await output.WriteLineAsync($"{i + 1}. {actions[i].DisplaySignature} [{index.ProjectName(actions[i].Compilation)}]");
            await output.WriteLineAsync("Choose an action number (q to cancel):");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var choice = await input.ReadLineAsync(token);
                if (choice is null || choice.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                { await output.WriteLineAsync("No diagram saved."); return 0; }
                if (int.TryParse(choice.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= actions.Length)
                { selected = actions[number - 1]; break; }
                await output.WriteLineAsync($"Choose a number from 1 to {actions.Length}, or q to cancel:");
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
