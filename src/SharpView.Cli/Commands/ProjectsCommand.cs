using System.Collections.Immutable;
using SharpView.Core;
using SharpView.Export.Mermaid;

namespace SharpView.Cli;

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
        var printed = new HashSet<CompilationId>();
        foreach (var root in map.Roots) await Write(root, "", "", []);
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

        // A project referenced from several places is described in full once; later occurrences point back to it.
        async Task Write(CompilationId id, string lead, string indent, ImmutableHashSet<CompilationId> ancestors)
        {
            token.ThrowIfCancellationRequested();
            var project = map.Projects[id];
            if (ancestors.Contains(id)) { await output.WriteLineAsync($"{lead}{project.Name} (reference cycle)"); return; }
            if (!printed.Add(id)) { await output.WriteLineAsync($"{lead}{project.Name} (described above)"); return; }
            await output.WriteLineAsync($"{lead}{project.Name} [{Settings(project)}]");
            var children = map.Graph.OutEdges(id).Select(x => x.Target).Distinct().OrderBy(x => map.Projects[x].Name, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < children.Length; i++)
            {
                var last = i == children.Length - 1;
                await Write(children[i], indent + (last ? "└── " : "├── "), indent + (last ? "    " : "│   "), ancestors.Add(id));
            }
        }
    }

    // Framework, nullable context and warning count always; the rest only when it is set.
    private static string Settings(ProjectNode project) => string.Join(", ", new[]
    {
        project.Framework, $"nullable {project.Nullable.ToLowerInvariant()}", Display.Plural(project.Warnings, "warning"),
        project.WarningsAsErrors switch { WarningsAsErrorsLevel.All => "all warnings are errors", WarningsAsErrorsLevel.None => null, _ => "nullable warnings are errors" },
        project.Errors > 0 ? Display.Plural(project.Errors, "error") : null,
    }.OfType<string>());
}
