namespace SharpView.Core;

// Resolves the configured layer of types and callables once per element. Precedence: longest prefix wins,
// project mapping wins a tie. Callables use their compilation's project and their declaring type.
public sealed class LayerMap(ProgramIndex index, LayerModel model)
{
    // Layer name for source elements that no mapping covers; referenced (metadata) elements stay null instead.
    public const string Unmapped = "(unmapped)";

    private readonly Dictionary<TypeId, string?> typeLayers = [];
    private readonly Dictionary<CallableId, string?> callableLayers = [];

    public LayerModel Model => model;

    public string? LayerOf(TypeId id)
    {
        if (typeLayers.TryGetValue(id, out var cached)) return cached;
        var layer = index.TryGetType(id, out var type) ? Resolve(type.Compilation, type.Namespace, type.DisplayName) : null;
        typeLayers[id] = layer;
        return layer;
    }

    public string? LayerOf(CallableId id)
    {
        if (callableLayers.TryGetValue(id, out var cached)) return cached;
        string? layer = null;
        if (index.TryGetCallable(id, out var callable))
            layer = Resolve(callable.Compilation, index.DeclaringType(id)?.Namespace ?? "", index.DeclaringType(id)?.DisplayName ?? "");
        callableLayers[id] = layer;
        return layer;
    }

    // True when the element belongs to an analysed project rather than a referenced assembly.
    public bool IsSource(CompilationId id) => index.Compilation(id) is not null;

    private string? Resolve(CompilationId compilation, string ns, string typeName)
    {
        // Referenced assemblies have no project name, so project mappings never match them.
        var project = index.Compilation(compilation) is { } fact ? Path.GetFileNameWithoutExtension(fact.ProjectPath) : "";
        return model.Mappings.Where(x => x.Scope switch
            {
                LayerScope.Project => project.StartsWith(x.Prefix, StringComparison.Ordinal),
                LayerScope.Namespace => ns == x.Prefix || ns.StartsWith(x.Prefix + ".", StringComparison.Ordinal),
                _ => typeName == x.Prefix || typeName.StartsWith(x.Prefix + ".", StringComparison.Ordinal)
            })
            .OrderByDescending(x => x.Prefix.Length).ThenBy(x => x.Scope == LayerScope.Project ? 0 : 1).Select(x => x.Layer).FirstOrDefault();
    }
}
