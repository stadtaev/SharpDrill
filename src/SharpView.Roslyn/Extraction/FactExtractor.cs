using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using SharpView.Core;

namespace SharpView.Roslyn;

internal sealed partial class FactExtractor(ImmutableArray<CompilationFact> compilations, List<AnalysisGap> initialGaps)
{
    private readonly List<AnalysisGap> gaps = initialGaps;
    internal static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private readonly Dictionary<string, CallableId> sourceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationId> assemblyProjects = new(StringComparer.Ordinal);
    private readonly Dictionary<CallableId, CallableFact> methods = [];
    private readonly Dictionary<TypeId, TypeFact> types = [];
    // Linked list keeps today's "remove then append" iteration order; the index makes key lookups O(1).
    private readonly LinkedList<(IMethodSymbol Symbol, SyntaxNode Node, SemanticModel Model, CompilationFact Compilation, bool Generated)> bodies = [];
    private readonly Dictionary<string, LinkedListNode<(IMethodSymbol Symbol, SyntaxNode Node, SemanticModel Model, CompilationFact Compilation, bool Generated)>> bodyByKey = new(StringComparer.Ordinal);
    private readonly List<INamedTypeSymbol> sourceTypes = [];
    private readonly List<(EqualsValueClauseSyntax Node, SemanticModel Model, CompilationFact Compilation)> initializers = [];
    private readonly List<CallSiteFact> calls = [];
    private readonly List<ThrowSiteFact> throws = [];
    private readonly List<CatchFact> catches = [];
    private readonly List<ExceptionRegionFact> regions = [];
    private readonly List<DependencyFact> dependencies = [];
    private readonly List<ImplementationFact> implementations = [];
    private readonly List<BoundaryHandlingFact> boundaries = [];
    private readonly Dictionary<SyntaxTree, bool> generatedTrees = [];
    private readonly Dictionary<SyntaxTree, string> checksums = [];
    private readonly Dictionary<(SyntaxTree Tree, TextSpan Span), int[]> lambdaStarts = [];
    private readonly Dictionary<CompilationId, CompilationFact> sourceProjects = compilations.ToDictionary(x => x.Id);
    // Symbols in registration order; hierarchy links are resolved after extraction so baseline registration order is untouched.
    private readonly List<(TypeId Id, INamedTypeSymbol Symbol)> registeredTypes = [];
    private readonly List<(CallableId Id, IMethodSymbol Symbol)> registeredMethods = [];
    internal SourceEvidence Evidence(SyntaxNode node)
    {
        var tree = node.SyntaxTree;
        if (!checksums.TryGetValue(tree, out var checksum))
        {
            checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tree.GetText().ToString())));
            checksums[tree] = checksum;
        }
        var pos = tree.GetLineSpan(node.Span).StartLinePosition;
        return new(tree.FilePath, checksum, node.SpanStart, node.Span.Length, pos.Line + 1, pos.Character + 1, generatedTrees.GetValueOrDefault(tree));
    }
    private ImmutableArray<SourceEvidence> Declarations(ISymbol s)
    {
        var syntax = s.DeclaringSyntaxReferences;
        if (s is IMethodSymbol method)
        {
            if (method.PartialDefinitionPart is { } definition) syntax = syntax.AddRange(definition.DeclaringSyntaxReferences);
            if (method.PartialImplementationPart is { } implementation) syntax = syntax.AddRange(implementation.DeclaringSyntaxReferences);
        }
        return syntax.Select(x => Evidence(x.GetSyntax())).Distinct().OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.SpanStart).ToImmutableArray();
    }
    public async Task RegisterAsync(Project project, Compilation compilation, CompilationFact fact, bool includeGenerated, CancellationToken token)
    {
        if (assemblyProjects.TryGetValue(compilation.Assembly.Identity.ToString(), out var previous) && previous != fact.Id)
            throw new AnalysisInputException($"Ambiguous source assembly identity {compilation.Assembly.Identity}; analyse separately.");
        assemblyProjects[compilation.Assembly.Identity.ToString()] = fact.Id;
        var generated = (await project.GetSourceGeneratedDocumentsAsync(token)).Select(x => x.FilePath).ToHashSet(StringComparer.Ordinal);
        var ordinary = project.Documents.Select(x => x.FilePath).ToHashSet(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees.OrderBy(x => x.FilePath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var isGenerated = generated.Contains(tree.FilePath) || !ordinary.Contains(tree.FilePath) || tree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || tree.GetText(token).ToString().TrimStart().StartsWith("// <auto-generated", StringComparison.Ordinal);
            generatedTrees[tree] = isGenerated;
            var root = await tree.GetRootAsync(token);
            if (isGenerated && !includeGenerated) { gaps.Add(new(GapKind.ExcludedSource, fact.Id, null, "Generated source excluded.", Evidence(root))); continue; }
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in root.DescendantNodes())
            {
                token.ThrowIfCancellationRequested();
                if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && model.GetDeclaredSymbol(node, token) is INamedTypeSymbol declaredType)
                    sourceTypes.Add(declaredType);
                IMethodSymbol? method = node switch
                {
                    BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax => model.GetDeclaredSymbol(node, token) as IMethodSymbol,
                    PropertyDeclarationSyntax { ExpressionBody: not null } or IndexerDeclarationSyntax { ExpressionBody: not null } => (model.GetDeclaredSymbol(node, token) as IPropertySymbol)?.GetMethod,
                    AnonymousFunctionExpressionSyntax => (model.GetOperation(node, token) as IAnonymousFunctionOperation)?.Symbol,
                    _ => null
                };
                if (method is null) continue;
                var methodKey = Key(method);
                var id = new CallableId(fact.Id.Value + "|" + methodKey);
                sourceIds[methodKey] = id;
                // Partial definition and implementation share one fact; only extract the implementation body.
                var known = bodyByKey.TryGetValue(methodKey, out var existing);
                if (!known || HasBody(node))
                {
                    if (known) bodies.Remove(existing!);
                    bodyByKey[methodKey] = bodies.AddLast((method, node, model, fact, isGenerated));
                }
            }
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol bound) continue;
                var ns = bound.ContainingNamespace.ToDisplayString();
                if (ns.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && bound.Name is "UseExceptionHandler" or "AddExceptionHandler" or "UseDeveloperExceptionPage")
                    boundaries.Add(new(fact.Id, bound.Name, bound.ToDisplayString(), Evidence(invocation)));
                if (GlobalExceptionFilter(bound, invocation, model, token) is { } filter)
                    boundaries.Add(new(fact.Id, $"Filters.{bound.Name}({filter.Name})", bound.ToDisplayString(), Evidence(invocation)));
                if (ns.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && bound.Name is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods")
                    gaps.Add(new(GapKind.Unsupported, fact.Id, null, "Minimal API endpoint is outside controller-action analysis.", Evidence(invocation)));
            }
            // Top-level statements are one gap per file. Initializers matter only when they can run source code or throw;
            // literals, constants and library constructors/calls (`= 5`, `= new()`, `= string.Empty`) have nothing to model.
            if (root.DescendantNodes().OfType<GlobalStatementSyntax>().FirstOrDefault() is { } topLevel)
                gaps.Add(new(GapKind.Unsupported, fact.Id, null, "Top-level statements have no owning callable; boundary registrations are extracted separately.", Evidence(topLevel)));
            foreach (var node in root.DescendantNodes().OfType<EqualsValueClauseSyntax>().Where(x => x.Parent is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax } } or PropertyDeclarationSyntax))
                if (RunsSourceOrThrows(node.Value, model, token)) initializers.Add((node, model, fact));
        }
    }
    // `options.Filters.Add<F>()`, `Add(typeof(F))`, `Add(new F())` or `AddService<F>()` where F is an MVC exception filter.
    private static INamedTypeSymbol? GlobalExceptionFilter(IMethodSymbol bound, InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken token)
    {
        if (bound.Name is not ("Add" or "AddService") || bound.ContainingType.ToDisplayString() != "Microsoft.AspNetCore.Mvc.Filters.FilterCollection") return null;
        var type = bound.TypeArguments.FirstOrDefault() ?? (model.GetOperation(invocation, token) as IInvocationOperation)?.Arguments.FirstOrDefault()?.Value switch
        {
            ITypeOfOperation x => x.TypeOperand,
            IConversionOperation x => x.Operand.Type,
            var x => x?.Type,
        };
        return type is INamedTypeSymbol named && named.AllInterfaces.Any(x => x.ToDisplayString() is "Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter" or "Microsoft.AspNetCore.Mvc.Filters.IAsyncExceptionFilter") ? named : null;
    }
    // True when the code can raise an exception the model tracks: an explicit throw or a call into source.
    private static bool RunsSourceOrThrows(SyntaxNode code, SemanticModel model, CancellationToken token) =>
        code.DescendantNodesAndSelf(x => x is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).Any(x => x switch
        {
            ThrowExpressionSyntax or ThrowStatementSyntax => true,
            InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or ElementAccessExpressionSyntax =>
                model.GetSymbolInfo(x, token).Symbol is not { } symbol || symbol.Locations.Any(l => l.IsInSource),
            _ => false
        });
    private static bool HasBody(SyntaxNode n) => n switch
    {
        BaseMethodDeclarationSyntax x => x.Body is not null || x.ExpressionBody is not null,
        AccessorDeclarationSyntax x => x.Body is not null || x.ExpressionBody is not null,
        LocalFunctionStatementSyntax x => x.Body is not null || x.ExpressionBody is not null,
        AnonymousFunctionExpressionSyntax => true,
        PropertyDeclarationSyntax x => x.ExpressionBody is not null,
        IndexerDeclarationSyntax x => x.ExpressionBody is not null,
        _ => false
    };
    public void Extract(CancellationToken token)
    {
        foreach (var type in sourceTypes) RegisterType(type);
        foreach (var entry in bodies) RegisterMethod(entry.Symbol);
        foreach (var entry in bodies)
        {
            token.ThrowIfCancellationRequested();
            var id = RegisterMethod(entry.Symbol);
            var walker = new BodyWalker(this, entry.Model, entry.Compilation, id, token);
            IOperation? operation = entry.Node switch
            {
                AnonymousFunctionExpressionSyntax n => (entry.Model.GetOperation(n, token) as IAnonymousFunctionOperation)?.Body,
                LocalFunctionStatementSyntax n => entry.Model.GetOperation((SyntaxNode?)n.Body ?? n.ExpressionBody!.Expression, token),
                PropertyDeclarationSyntax { ExpressionBody: { } expression } => entry.Model.GetOperation(expression.Expression, token),
                IndexerDeclarationSyntax { ExpressionBody: { } expression } => entry.Model.GetOperation(expression.Expression, token),
                _ => entry.Model.GetOperation(entry.Node, token)
            };
            if (operation is not null) walker.Visit(operation);
            else if (methods[id].Body == BodyStatus.Source) walker.InternalGap($"Source body has no operation tree ({Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(entry.Node)}).", entry.Node);
            walker.ExtractRegions(entry.Node);
            // An implicit `: base()` is an ordinary implicit call to the base's parameterless constructor.
            if (entry.Symbol.MethodKind == MethodKind.Constructor && entry.Node is ConstructorDeclarationSyntax { Initializer: null } && entry.Symbol.ContainingType.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            {
                var baseConstructor = baseType.InstanceConstructors.FirstOrDefault(x => x.Parameters.All(p => p.IsOptional || p.IsParams));
                if (baseConstructor is null) walker.InternalGap("Implicit base constructor could not be resolved.", entry.Node);
                else walker.ImplicitBaseConstructor(baseConstructor, entry.Node);
            }
        }
        foreach (var initializer in initializers) ExtractInitializer(initializer.Node, initializer.Model, initializer.Compilation, token);
        ExtractMembers(token);
        LinkHierarchy(token);
        foreach (var (id, fact) in methods.Where(x => x.Value.Overrides is not null && x.Value.Body != BodyStatus.MetadataOnly).Select(x => (x.Key, x.Value)))
            implementations.Add(new(fact.Overrides!.Value, id, ImplementationKind.Override));
        foreach (var type in types.Values.ToArray()) foreach (var b in type.BaseTypes) dependencies.Add(new(type.Id, null, b, DependencyKind.Inheritance, type.Declarations.FirstOrDefault()));
    }
    // An initializer runs inside every constructor that does not chain to `this(...)` and is not a record copy constructor
    // (static ones inside the static constructor), so its calls and throws belong to those constructors. A constructor with no code of its own then has a body.
    private void ExtractInitializer(EqualsValueClauseSyntax node, SemanticModel model, CompilationFact compilation, CancellationToken token)
    {
        if (model.GetDeclaredSymbol(node.Parent!, token) is not { ContainingType: { } type } member || model.GetOperation(node, token) is not { } operation)
        { gaps.Add(new(GapKind.Internal, compilation.Id, null, "Field/property initializer could not be bound.", Evidence(node))); return; }
        var owners = member.IsStatic ? type.StaticConstructors
            : type.InstanceConstructors.Where(c => !c.DeclaringSyntaxReferences.Any(r => r.GetSyntax(token) is ConstructorDeclarationSyntax { Initializer.ThisOrBaseKeyword.ValueText: "this" })
                && !(type.IsRecord && c.Parameters is [{ Type: var copied }] && SymbolEqualityComparer.Default.Equals(copied, type))).ToImmutableArray();
        if (owners.IsEmpty) { gaps.Add(new(GapKind.Unsupported, compilation.Id, null, "Field/property initializer has no constructor to run in.", Evidence(node))); return; }
        foreach (var constructor in owners)
        {
            var id = RegisterMethod(constructor);
            if (methods[id].Body == BodyStatus.Synthesized) methods[id] = methods[id] with { Body = BodyStatus.Source };
            new BodyWalker(this, model, compilation, id, token).Visit(operation);
        }
    }
    public ProgramSnapshot Snapshot()
    {
        // Keys collide when two projects share a file name or two versions of one metadata assembly are referenced;
        // later IDs (ordinal order) get "#2", "#3", ... skipping any key that occurs naturally.
        var reserved = types.Values.Select(x => x.Key).Concat(methods.Values.Select(x => x.Key)).ToHashSet(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        string Unique(string key)
        {
            if (assigned.Add(key)) return key;
            for (var n = 2; ; n++) if (!reserved.Contains($"{key}#{n}") && assigned.Add($"{key}#{n}")) return $"{key}#{n}";
        }
        return new()
        {
            Compilations = compilations,
            Types = types.Values.OrderBy(x => x.Id.Value, StringComparer.Ordinal).Select(x => Unique(x.Key) is var key && key == x.Key ? x : x with { Key = key }).ToImmutableArray(),
            Callables = methods.Values.OrderBy(x => x.Id.Value, StringComparer.Ordinal).Select(x => Unique(x.Key) is var key && key == x.Key ? x : x with { Key = key }).ToImmutableArray(),
            CallSites = calls.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            Dependencies = dependencies.Distinct().OrderBy(x => x.OwningCallable?.Value, StringComparer.Ordinal).ThenBy(x => x.ReferencedType.DisplayName, StringComparer.Ordinal).ToImmutableArray(),
            ThrowSites = throws.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            Gaps = gaps.OrderBy(x => x.Compilation?.Value, StringComparer.Ordinal).ThenBy(x => x.Evidence?.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence?.SpanStart).ThenBy(x => x.Message, StringComparer.Ordinal).ToImmutableArray(),
            Catches = catches.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            ExceptionRegions = regions.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            Implementations = implementations.Distinct().OrderBy(x => x.Declared.Value, StringComparer.Ordinal).ThenBy(x => x.Implementation.Value, StringComparer.Ordinal).ThenBy(x => x.Kind).ToImmutableArray(),
            BoundaryHandling = boundaries.OrderBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart).ToImmutableArray()
        };
    }
}
