using System.Globalization;
using System.Text;
using Sharpview.Core;
using Sharpview.Export.Mermaid;

namespace Sharpview.Cli;

internal static class DiagramCommand
{
    internal static async Task<int> RunAsync(ProgramIndex index, Arguments options, TextReader input, TextWriter output, CancellationToken token)
    {
        var actions = AspNetConventions.ControllerActions(index, token).Select(x => index.Callable(x.Method))
            .OrderBy(x => x.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
        CallableFact? selected;
        if (options.Root is not null || options.RootId is not null)
        {
            var matches = actions.Where(x => options.RootId is not null ? x.Id.Value == options.RootId : x.DisplaySignature == options.Root).ToArray();
            if (matches.Length == 0) throw new ArgumentException("No source callable matches the selected root (diagram requires a recognised action).");
            if (matches.Length > 1) throw new ArgumentException("Ambiguous signature; use --root-id:\n" + string.Join("\n", matches.Select(x => x.Id.Value)));
            selected = matches[0];
        }
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
        var diagram = CallDiagramQuery.Build(index, selected.Id,
            new(options.FollowCandidates, options.Traversal.MaxDepth, options.MaxNodes, options.MaxEdges, options.Traversal.MaxExpansions), token);
        var document = MermaidExporter.Markdown(diagram);
        var path = options.Output is null ? DefaultPath(selected.DisplaySignature) : Path.GetFullPath(options.Output);
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
