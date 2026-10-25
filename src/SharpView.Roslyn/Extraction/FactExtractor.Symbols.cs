using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpView.Core;

namespace SharpView.Roslyn;

// Type and callable registration: facts, attributes, kinds, modifiers and the deferred hierarchy links.
internal sealed partial class FactExtractor
{
    private static ImmutableArray<AttributeFact> Attributes(ISymbol s) => s.GetAttributes()
        .Select(x => new AttributeFact(x.AttributeClass?.ToDisplayString() ?? "<unresolved>", x.ConstructorArguments.Select(a => a.ToCSharpString()).ToImmutableArray(),
            x.NamedArguments.Select(a => new NamedArgumentFact(a.Key, a.Value.ToCSharpString())).ToImmutableArray()))
        .OrderBy(x => x.Name, StringComparer.Ordinal)
        .ThenBy(x => string.Join(", ", x.Arguments.Concat(x.NamedArguments.Select(a => a.Name + " = " + a.Value))), StringComparer.Ordinal).ToImmutableArray();
    private static DeclaredTypeKind KindOf(INamedTypeSymbol s) => s.TypeKind switch
    {
        TypeKind.Class => s.IsRecord ? DeclaredTypeKind.Record : DeclaredTypeKind.Class,
        TypeKind.Struct => s.IsRecord ? DeclaredTypeKind.RecordStruct : DeclaredTypeKind.Struct,
        TypeKind.Interface => DeclaredTypeKind.Interface,
        TypeKind.Enum => DeclaredTypeKind.Enum,
        TypeKind.Delegate => DeclaredTypeKind.Delegate,
        _ => DeclaredTypeKind.Other
    };
    // Static classes are abstract+sealed in metadata; report them only as static. Generic means the type declares its own type parameters.
    private static TypeModifiers ModifiersOf(INamedTypeSymbol s) => (s.IsStatic ? TypeModifiers.Static
        : (s.IsAbstract ? TypeModifiers.Abstract : 0) | (s.IsSealed ? TypeModifiers.Sealed : 0)) | (s.Arity > 0 ? TypeModifiers.Generic : 0);
    private static CallableModifiers ModifiersOf(IMethodSymbol s) =>
        (s.IsStatic ? CallableModifiers.Static : 0) | (s.IsAbstract ? CallableModifiers.Abstract : 0) | (s.IsVirtual ? CallableModifiers.Virtual : 0)
        | (s.IsOverride ? CallableModifiers.Override : 0) | (s.IsSealed ? CallableModifiers.Sealed : 0) | (s.IsGenericMethod ? CallableModifiers.Generic : 0)
        | (s.IsExtensionMethod ? CallableModifiers.Extension : 0) | (s.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion ? CallableModifiers.Operator : 0)
        | (s.ExplicitInterfaceImplementations.Length > 0 ? CallableModifiers.ExplicitImplementation : 0) | (s.MethodKind == MethodKind.Destructor ? CallableModifiers.Finalizer : 0);
    internal TypeUse Use(ITypeSymbol symbol)
    {
        var components = symbol switch
        {
            IArrayTypeSymbol a => ImmutableArray.Create(Use(a.ElementType)),
            IPointerTypeSymbol p => ImmutableArray.Create(Use(p.PointedAtType)),
            INamedTypeSymbol n => n.TypeArguments.Select(x => UseShallow(x)).ToImmutableArray(),
            _ => []
        };
        // An unresolved (error) type has no assembly to own it; it stays a name without a declaration.
        return new(symbol is INamedTypeSymbol { TypeKind: not TypeKind.Error } named ? RegisterType(named) : null, TypeName(symbol),
            symbol.NullableAnnotation == NullableAnnotation.Annotated ? NullabilityKind.Annotated : symbol.NullableAnnotation == NullableAnnotation.NotAnnotated ? NullabilityKind.NotAnnotated : NullabilityKind.Oblivious, components);
    }
    private TypeUse UseShallow(ITypeSymbol s) => s is ITypeParameterSymbol ? new(null, TypeName(s), NullabilityKind.Oblivious, []) : Use(s);
    private TypeId RegisterType(INamedTypeSymbol symbol)
    {
        var s = symbol.OriginalDefinition;
        var assembly = s.ContainingAssembly.Identity.ToString();
        var compilation = assemblyProjects.GetValueOrDefault(assembly, new CompilationId("metadata|" + assembly));
        var id = new TypeId(compilation.Value + "|" + (s.GetDocumentationCommentId() ?? TypeName(s)));
        if (types.ContainsKey(id)) return id;
        types[id] = new(id, compilation, TypeName(s), s.DeclaredAccessibility.ToString(), [], Attributes(s), Declarations(s),
            s.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "", KindOf(s), ModifiersOf(s),
            Key: ProjectName(compilation, s.ContainingAssembly) + "|" + (s.GetDocumentationCommentId() ?? TypeName(s)));
        registeredTypes.Add((id, s));
        var bases = new List<TypeUse>();
        if (s.BaseType is not null) bases.Add(Use(s.BaseType));
        bases.AddRange(s.Interfaces.Select(Use));
        types[id] = types[id] with { BaseTypes = bases.ToImmutableArray(), BaseClass = s.BaseType is null ? null : bases[0].Declaration };
        return id;
    }
    private static bool IsCompilerProvided(IMethodSymbol s) =>
        !s.IsAbstract && !s.IsExtern && s.ContainingType is { TypeKind: not (TypeKind.Interface or TypeKind.Delegate) }
        && (s.IsImplicitlyDeclared || s.AssociatedSymbol is IPropertySymbol or IEventSymbol
            || s.MethodKind == MethodKind.Constructor && s.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax));

    internal CallableId RegisterMethod(IMethodSymbol symbol)
    {
        var s = Canonical(symbol);
        var key = Key(s);
        var id = sourceIds.GetValueOrDefault(key, new CallableId("metadata|" + key));
        if (methods.ContainsKey(id)) return id;
        var compilation = assemblyProjects.GetValueOrDefault(s.ContainingAssembly.Identity.ToString(), new CompilationId("metadata|" + s.ContainingAssembly.Identity));
        var kind = s.MethodKind switch { MethodKind.Constructor or MethodKind.StaticConstructor => CallableKind.Constructor, MethodKind.PropertyGet => CallableKind.Getter, MethodKind.PropertySet => CallableKind.Setter, MethodKind.EventAdd => CallableKind.EventAdder, MethodKind.EventRemove => CallableKind.EventRemover, MethodKind.LocalFunction => CallableKind.LocalFunction, MethodKind.AnonymousFunction => CallableKind.Lambda, _ => CallableKind.Method };
        var bodyNode = bodyByKey.TryGetValue(key, out var bodyEntry) ? bodyEntry.Value.Node : null;
        var body = sourceIds.ContainsKey(key) ? bodyNode is not null && HasBody(bodyNode) ? BodyStatus.Source : BodyStatus.NoBody : BodyStatus.MetadataOnly;
        // Implicit constructors, auto-property accessors and record members of source types have no explicit code.
        if (body != BodyStatus.Source && assemblyProjects.ContainsKey(s.ContainingAssembly.Identity.ToString()) && IsCompilerProvided(s)) body = BodyStatus.Synthesized;
        // Insert before recursively registering containing local functions.
        methods[id] = new(id, compilation, s.ContainingType is null ? null : RegisterType(s.ContainingType), null, kind, Signature(s), s.DeclaredAccessibility.ToString(), Use(s.ReturnType), s.Parameters.Select(x => new ParameterFact(x.Name, Use(x.Type), x.RefKind.ToString())).ToImmutableArray(), Attributes(s), body, Declarations(s), s.IsAsync,
            s.ReturnType is INamedTypeSymbol returnType && returnType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && returnType.Name is "Task" or "ValueTask",
            ModifiersOf(s), null, CallableKey(s, compilation));
        registeredMethods.Add((id, s));
        if (s.ContainingSymbol is IMethodSymbol parent) methods[id] = methods[id] with { ContainingCallable = RegisterMethod(parent) };
        foreach (var use in new[] { methods[id].ReturnType }.Concat(methods[id].Parameters.Select(x => x.Type))) AddDependency(methods[id], use, DependencyKind.Signature, methods[id].Declarations.FirstOrDefault());
        return id;
    }
    private void AddDependency(CallableFact owner, TypeUse type, DependencyKind kind, SourceEvidence? evidence)
    {
        dependencies.Add(new(owner.DeclaringType, owner.Id, type, kind, evidence));
        foreach (var child in type.Components) AddDependency(owner, child, kind, evidence);
    }
    // Registers containing types and directly overridden members (transitively) after the baseline facts, appending to the worklists.
    private void LinkHierarchy(CancellationToken token)
    {
        for (int t = 0, m = 0; t < registeredTypes.Count || m < registeredMethods.Count;)
        {
            token.ThrowIfCancellationRequested();
            if (t < registeredTypes.Count)
            {
                var (id, symbol) = registeredTypes[t++];
                if (symbol.ContainingType is { } container) { var containing = RegisterType(container); types[id] = types[id] with { ContainingType = containing }; }
            }
            else
            {
                var (id, symbol) = registeredMethods[m++];
                if (symbol.OverriddenMethod is { } overridden) { var target = RegisterMethod(overridden); methods[id] = methods[id] with { Overrides = target }; }
            }
        }
    }
}
