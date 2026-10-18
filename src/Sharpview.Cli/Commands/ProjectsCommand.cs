using System.Collections.Immutable;
using Sharpview.Core;
using Sharpview.Export.Mermaid;

namespace Sharpview.Cli;

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
        await output.WriteLineAsync($"Projects: {map.Projects.Count}. Each project is listed with the projects it references (<ProjectReference>) beneath it.");
        var printed = new HashSet<CompilationId>();
        foreach (var root in map.Roots) await Write(root, "", "", []);
        foreach (var (source, path) in map.Unloaded)
            await output.WriteLineAsync($"Unloaded reference: {map.Projects[source].Name} --> {path} (not part of the loaded workspace)");
        if (arguments.Mermaid)
        {
            var path = await MarkdownExport.SaveAsync(MermaidExporter.Markdown(map), arguments.Output, "projects", token);
            await output.WriteLineAsync("Saved diagram: " + path);
        }
        var loadGaps = index.Snapshot.Gaps.Where(x => x.Kind == GapKind.ProjectLoad).ToArray();
        foreach (var gap in loadGaps) Console.Error.WriteLine($"Gap [{gap.Kind}]: {gap.Message}");
        Console.Error.WriteLine($"Projects: {map.Projects.Count}; references: {map.Graph.EdgeCount}; unloaded references: {map.Unloaded.Length}; load gaps: {loadGaps.Length}.");
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

    private static string Settings(ProjectNode project) =>
        $"{project.Framework} | Nullable: {project.Nullable} | Nullable warnings as errors: {YesNo(project.WarningsAsErrors != WarningsAsErrorsLevel.None)}" +
        $" | All warnings as errors: {YesNo(project.WarningsAsErrors == WarningsAsErrorsLevel.All)} | Compiler warnings: {project.Warnings} | Errors: {project.Errors}";

    private static string YesNo(bool value) => value ? "yes" : "no";
}
