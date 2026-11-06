using System.Collections.Immutable;
using SharpDrill.Core;
using SharpDrill.Export.Mermaid;

namespace SharpDrill.Cli;

/// <summary>The workspace's project reference tree from MSBuild, with each project's key build settings.</summary>
internal sealed class ProjectsCommand : ICliCommand
{
    public ImmutableHashSet<string> Options { get; } = ["--mermaid", "--output"];

    public void Validate(Arguments arguments)
    {
        if (arguments.Output is not null && !arguments.Mermaid) throw new ArgumentException("--output requires projects --mermaid.");
    }

    public async Task<int> RunAsync(ProgramIndex index, Arguments arguments, CancellationToken token)
    {
        var map = ProjectMap.Build(index.Snapshot);
        var output = Console.Out;
        foreach (var line in Tree(map))
        {
            token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(line);
        }
        foreach (var (source, path) in map.Unloaded)
            await output.WriteLineAsync($"Not loaded: {map.Projects[source].Name} references {path}, which is not in the solution");
        if (arguments.Mermaid)
        {
            var path = await MarkdownExport.SaveAsync(MermaidExporter.Markdown(map), arguments.Output, "projects", token);
            await output.WriteLineAsync("Saved diagram: " + path);
        }
        var loadGaps = index.Snapshot.Gaps.Where(x => x.Kind == GapKind.ProjectLoad).ToArray();
        foreach (var gap in loadGaps) Console.Error.WriteLine($"Not loaded: {gap.Message}");
        token.ThrowIfCancellationRequested();
        return map.Unloaded.IsEmpty && loadGaps.Length == 0 ? 0 : 1;
    }

    // Each project gets one line, nested under the first project to reach it; its other referrers name it after "->".
    internal static IEnumerable<string> Tree(ProjectMap map)
    {
        var prefix = SharedPrefix(map.Projects.Values.Select(x => x.Name));
        // Placed before writing: a sibling's subtree can claim a reference after the parent's own line would already be out.
        var parents = new Dictionary<CompilationId, CompilationId>();
        foreach (var root in map.Roots) Place(root);
        return map.Roots.SelectMany(x => Write(x, "", "", []));

        CompilationId[] References(CompilationId id) => [.. map.Graph.OutEdges(id).Select(x => x.Target).Distinct().OrderBy(x => map.Projects[x].Name, StringComparer.Ordinal)];
        void Place(CompilationId id) { foreach (var child in References(id)) if (parents.TryAdd(child, id)) Place(child); }

        IEnumerable<string> Write(CompilationId id, string lead, string indent, ImmutableHashSet<CompilationId> ancestors)
        {
            var path = ancestors.Add(id);
            var references = References(id);
            var children = references.Where(x => parents[x] == id).ToArray();
            var inline = references.Except(children).Select(x => map.Projects[x].Name[prefix.Length..] + (path.Contains(x) ? " (reference cycle)" : "")).ToArray();
            yield return $"{lead}{map.Projects[id].Name} [{Settings(map.Projects[id])}]" + (inline.Length > 0 ? " -> " + string.Join(", ", inline) : "");
            for (var i = 0; i < children.Length; i++)
            {
                var last = i == children.Length - 1;
                foreach (var line in Write(children[i], indent + (last ? "└── " : "├── "), indent + (last ? "    " : "│   "), path)) yield return line;
            }
        }
    }

    // The dotted prefix every project shares says nothing in a reference list; a project's own line keeps its full name.
    private static string SharedPrefix(IEnumerable<string> names)
    {
        var segments = names.Select(x => x.Split('.')).ToArray();
        var first = segments.FirstOrDefault() ?? [];
        var length = 0;
        while (length < first.Length - 1 && segments.All(x => length < x.Length - 1 && x[length] == first[length])) length++;
        return string.Concat(first.Take(length).Select(x => x + "."));
    }

    // Framework, nullable context and warning count always; the rest only when it is set.
    private static string Settings(ProjectNode project) => string.Join(", ", new[]
    {
        project.Framework, $"nullable {project.Nullable.ToLowerInvariant()}", Display.Plural(project.Warnings, "warning"),
        project.WarningsAsErrors switch { WarningsAsErrorsLevel.All => "all warnings are errors", WarningsAsErrorsLevel.None => null, _ => "nullable warnings are errors" },
        project.Errors > 0 ? Display.Plural(project.Errors, "error") : null,
    }.OfType<string>());
}
