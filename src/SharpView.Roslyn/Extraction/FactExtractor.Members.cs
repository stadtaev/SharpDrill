using Microsoft.CodeAnalysis;
using SharpView.Core;

namespace SharpView.Roslyn;

// Member-level facts: field/property dependencies and implementation candidates.
internal sealed partial class FactExtractor
{
    // Field/property dependencies and interface implementation candidates; runs before LinkHierarchy so everything it registers gets linked.
    private void ExtractMembers(CancellationToken token)
    {
        foreach (var type in sourceTypes.Select(x => x.OriginalDefinition).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToArray())
        {
            token.ThrowIfCancellationRequested();
            var owner = RegisterType(type);
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.DeclaringSyntaxReferences.FirstOrDefault() is not { } syntax) continue;
                // Enum constants would only depend on their own enum.
                if (member is IFieldSymbol { ContainingType.TypeKind: not TypeKind.Enum } field) AddMemberDependency(owner, field.Type, DependencyKind.Field, syntax);
                else if (member is IPropertySymbol property) AddMemberDependency(owner, property.Type, DependencyKind.Property, syntax);
            }
            if (type.TypeKind == TypeKind.Interface) continue;
            foreach (var declared in type.AllInterfaces.SelectMany(x => x.GetMembers().OfType<IMethodSymbol>()))
                if (type.FindImplementationForInterfaceMember(declared) is IMethodSymbol implementation && sourceIds.ContainsKey(Key(implementation))
                    && !SymbolEqualityComparer.Default.Equals(Canonical(implementation), Canonical(declared)))
                    implementations.Add(new(RegisterMethod(declared), RegisterMethod(implementation), ImplementationKind.Interface));
        }
    }
    private void AddMemberDependency(TypeId owner, ITypeSymbol symbol, DependencyKind kind, SyntaxReference syntax)
    {
        var evidence = Evidence(syntax.GetSyntax());
        void Add(TypeUse use) { dependencies.Add(new(owner, null, use, kind, evidence)); foreach (var child in use.Components) Add(child); }
        Add(Use(symbol));
    }
}
