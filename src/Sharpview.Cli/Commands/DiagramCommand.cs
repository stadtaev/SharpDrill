using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Sharpview.Core;
using Sharpview.Export.Mermaid;

namespace Sharpview.Cli;

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
        var path = arguments.Output is null ? DefaultPath(selected.DisplaySignature) : Path.GetFullPath(arguments.Output);
        token.ThrowIfCancellationRequested();
        // CreateNew protects against races as well as accidental input-file overwrites.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(document);
                await file.WriteAsync(bytes, token);
                await file.FlushAsync(token);
            }
            catch
            {
                await file.DisposeAsync();
                File.Delete(path);
                throw;
            }
        }
        await output.WriteLineAsync("Saved diagram: " + path);
        await output.WriteLineAsync($"Nodes: {diagram.Nodes.Length}; edges: {diagram.Edges.Length}; boundaries: {diagram.Boundaries.Length}; extraction gaps: {diagram.Gaps.Length}; limits: {(diagram.Limits.IsEmpty ? "none" : string.Join(", ", diagram.Limits))}.");
        return diagram.IsComplete ? 0 : 1;
    }

    private static string DefaultPath(string signature)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagrams_exports");
        Directory.CreateDirectory(directory);
        var slug = new string(signature.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "action";
        if (slug.Length > 80) slug = slug[..80];
        return Path.GetFullPath(Path.Combine(directory, $"{slug}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md"));
    }
}
