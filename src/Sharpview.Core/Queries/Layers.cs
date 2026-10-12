using System.Collections.Immutable;
namespace Sharpview.Core;

public sealed record LayerMapping(string Scope, string Prefix, string Layer);
public static class LayerQuery
{
    public static string? Resolve(ProgramSnapshot snapshot, CallableFact method, ImmutableArray<LayerMapping> mappings) =>
        Resolve(new ProgramIndex(snapshot), method, mappings);
    public static string? Resolve(ProgramIndex index, CallableFact method, ImmutableArray<LayerMapping> mappings)
    {
        // Referenced assemblies have no project name, so project mappings never match them.
        var projectName = index.Compilation(method.Compilation) is { } project ? Path.GetFileNameWithoutExtension(project.ProjectPath) : "";
        var type = method.DeclaringType is { } id && index.TryGetType(id, out var fact) ? fact.DisplayName : "";
        return mappings.Where(x => x.Scope == "project" ? projectName.StartsWith(x.Prefix, StringComparison.Ordinal)
                : type.StartsWith(x.Prefix + ".", StringComparison.Ordinal) || type == x.Prefix)
            .OrderByDescending(x => x.Prefix.Length).ThenBy(x => x.Scope == "project" ? 0 : 1).Select(x => x.Layer).FirstOrDefault();
    }
}
