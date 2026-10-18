using System.Collections.Immutable;
using Sharpview.Graph;

namespace Sharpview.Core;

public enum WarningsAsErrorsLevel { None, Nullable, All }

public sealed record ProjectNode(CompilationId Id, string Name, string Path, string Framework, string Nullable,
    WarningsAsErrorsLevel WarningsAsErrors, ImmutableArray<string> WarningsAsErrorsIds, int Warnings, int Errors);

// A <ProjectReference> from Source to Target. Targets outside the loaded workspace are listed in ProjectMap.Unloaded.
public sealed record ProjectReferenceEdge(CompilationId Source, CompilationId Target) : IEdge<CompilationId>;

/// <summary>The workspace's project reference graph with each project's evaluated build settings.</summary>
/// <remarks>Built from MSBuild facts only; code-level dependencies between projects are not consulted.</remarks>
public sealed class ProjectMap
{
    public DiGraph<CompilationId, ProjectReferenceEdge> Graph { get; }
    public ImmutableDictionary<CompilationId, ProjectNode> Projects { get; }
    // (Referencing project, referenced project path) for references to projects that were not loaded.
    public ImmutableArray<(CompilationId Source, string Path)> Unloaded { get; }

    private ProjectMap(DiGraph<CompilationId, ProjectReferenceEdge> graph, ImmutableDictionary<CompilationId, ProjectNode> projects,
        ImmutableArray<(CompilationId, string)> unloaded) { Graph = graph; Projects = projects; Unloaded = unloaded; }

    // Projects nothing else references, in name order: the entry points of the tree.
    public ImmutableArray<CompilationId> Roots => [.. Graph.Nodes.Where(x => Graph.InEdges(x).IsEmpty)];

    public static ProjectMap Build(ProgramSnapshot snapshot)
    {
        var compilations = snapshot.Compilations.OrderBy(Name, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
        var byPath = compilations.GroupBy(x => System.IO.Path.GetFullPath(x.ProjectPath), PathComparer).ToDictionary(x => x.Key, x => x.First().Id, PathComparer);
        var builder = DiGraph<CompilationId, ProjectReferenceEdge>.CreateBuilder();
        var unloaded = ImmutableArray.CreateBuilder<(CompilationId, string)>();
        foreach (var compilation in compilations) builder.AddNode(compilation.Id);
        foreach (var compilation in compilations)
            foreach (var reference in compilation.ProjectReferences)
                if (byPath.TryGetValue(reference, out var target)) builder.AddEdge(new(compilation.Id, target));
                else unloaded.Add((compilation.Id, reference));
        var projects = compilations.ToImmutableDictionary(x => x.Id, x => new ProjectNode(x.Id, Name(x), x.ProjectPath, x.TargetFramework,
            string.IsNullOrEmpty(x.NullableSetting) ? "disable" : x.NullableSetting, Level(x), x.WarningsAsErrors, x.WarningCount, x.ErrorCount));
        return new(builder.Build(), projects, unloaded.ToImmutable());
    }

    private static string Name(CompilationFact compilation) => System.IO.Path.GetFileNameWithoutExtension(compilation.ProjectPath);

    // TreatWarningsAsErrors covers every warning; WarningsAsErrors=nullable is the MSBuild shorthand for nullable warnings.
    private static WarningsAsErrorsLevel Level(CompilationFact compilation) =>
        compilation.TreatWarningsAsErrors ? WarningsAsErrorsLevel.All
        : compilation.WarningsAsErrors.Contains("nullable", StringComparer.OrdinalIgnoreCase) ? WarningsAsErrorsLevel.Nullable
        : WarningsAsErrorsLevel.None;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
