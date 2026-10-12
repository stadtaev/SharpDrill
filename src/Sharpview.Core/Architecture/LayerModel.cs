using System.Collections.Immutable;

namespace Sharpview.Core;

public enum LayerScope { Project, Namespace, Type }
// Project prefixes match the project file name; namespace prefixes match a namespace or any nested namespace;
// type prefixes match a fully qualified type name or its nested types.
public sealed record LayerMapping(LayerScope Scope, string Prefix, string Layer);
public sealed record AllowedDependency(string From, string To);

// User-configured architecture: layers come only from explicit mappings, never from naming heuristics.
public sealed record LayerModel(ImmutableArray<LayerMapping> Mappings, ImmutableArray<AllowedDependency> Allowed)
{
    public LayerModel(ImmutableArray<LayerMapping> mappings) : this(mappings, []) { }

    // Distinct layer names in first-mapping order.
    public ImmutableArray<string> Layers => [.. Mappings.Select(x => x.Layer).Distinct(StringComparer.Ordinal)];

    // Same-layer dependencies are always allowed; otherwise only listed pairs.
    public bool Allows(string from, string to) =>
        string.Equals(from, to, StringComparison.Ordinal) || Allowed.Any(x => x.From == from && x.To == to);
}
