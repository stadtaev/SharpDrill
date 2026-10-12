using System.Collections.Immutable;
namespace Sharpview.Core;

public sealed record LayerMapping(string Scope, string Prefix, string Layer);
public static class LayerQuery
{
    public static string? Resolve(ProgramSnapshot snapshot, CallableFact method, ImmutableArray<LayerMapping> mappings)
    {
        var project = snapshot.Compilations.FirstOrDefault(x => x.Id == method.Compilation);
        var projectName = project is null ? "" : Path.GetFileNameWithoutExtension(project.ProjectPath);
        var type = snapshot.Types.FirstOrDefault(x => x.Id == method.DeclaringType)?.DisplayName ?? "";
        return mappings.Where(x => x.Scope == "project" ? projectName.StartsWith(x.Prefix, StringComparison.Ordinal)
                : type.StartsWith(x.Prefix + ".", StringComparison.Ordinal) || type == x.Prefix)
            .OrderByDescending(x => x.Prefix.Length).ThenBy(x => x.Scope == "project" ? 0 : 1).Select(x => x.Layer).FirstOrDefault();
    }
}
