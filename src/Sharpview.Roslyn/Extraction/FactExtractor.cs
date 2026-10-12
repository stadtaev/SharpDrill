using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Sharpview.Core;

namespace Sharpview.Roslyn;

internal sealed class FactExtractor(ImmutableArray<CompilationFact> compilations, List<AnalysisGap> initialGaps)
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
    private readonly List<CallSiteFact> calls = [];
    private readonly List<ThrowSiteFact> throws = [];
    private readonly List<CatchFact> catches = [];
    private readonly List<ExceptionRegionFact> regions = [];
    private readonly List<DependencyFact> dependencies = [];
    private readonly List<ControllerActionFact> actions = [];
    private readonly List<BoundaryHandlingFact> boundaries = [];
    private readonly Dictionary<SyntaxTree, bool> generatedTrees = [];
    private readonly Dictionary<SyntaxTree, string> checksums = [];

    internal static IMethodSymbol Canonical(IMethodSymbol s) => (s.ReducedFrom ?? s).OriginalDefinition.PartialImplementationPart ?? (s.ReducedFrom ?? s).OriginalDefinition;
    private static string Key(IMethodSymbol symbol)
    {
        var s = Canonical(symbol);
        var doc = s.GetDocumentationCommentId();
        if (doc is null)
        {
            var syntax = s.DeclaringSyntaxReferences.FirstOrDefault();
            doc = $"{s.ContainingSymbol}|{syntax?.SyntaxTree.FilePath}:{syntax?.Span.Start}|{s.MetadataName}";
        }
        return $"{s.ContainingAssembly.Identity}|{doc}";
    }
    internal static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat).Replace("global::", "", StringComparison.Ordinal);
    private static string Signature(IMethodSymbol symbol)
    {
        var s = Canonical(symbol);
        var name = s.MethodKind == MethodKind.Constructor ? ".ctor" : s.MethodKind == MethodKind.StaticConstructor ? ".cctor" : s.Name;
        if (s.TypeParameters.Length > 0) name += "<" + string.Join(", ", s.TypeParameters.Select(x => x.Name)) + ">";
        var prefix = s.ContainingType is null ? "" : TypeName(s.ContainingType) + ".";
        if (s.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction)
            prefix = s.ContainingSymbol.ToDisplayString() + ".";
        return prefix + name + "(" + string.Join(", ", s.Parameters.Select(x => (x.RefKind == RefKind.None ? "" : x.RefKind.ToString().ToLowerInvariant() + " ") + TypeName(x.Type))) + ")";
    }
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
    private static ImmutableArray<string> Attributes(ISymbol s) => s.GetAttributes().Select(x => x.AttributeClass?.ToDisplayString() ?? "<unresolved>").Order(StringComparer.Ordinal).ToImmutableArray();

    public async Task RegisterAsync(Project project, Compilation compilation, CompilationFact fact, bool includeGenerated, CancellationToken token)
    {
        if (assemblyProjects.TryGetValue(compilation.Assembly.Identity.ToString(), out var previous) && previous != fact.Id)
            throw new InvalidOperationException($"Ambiguous source assembly identity {compilation.Assembly.Identity}; analyse separately.");
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
                if (ns.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && bound.Name is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods")
                    gaps.Add(new(GapKind.Unsupported, fact.Id, null, "Minimal API endpoint is outside controller-action analysis.", Evidence(invocation)));
            }
            foreach (var node in root.DescendantNodes().Where(x => x is GlobalStatementSyntax || x is EqualsValueClauseSyntax && x.Parent is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax } } || x is EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }))
                gaps.Add(new(GapKind.Unsupported, fact.Id, null, "Top-level/initializer callable ownership is outside scope; boundary registrations are extracted separately.", Evidence(node)));
        }
    }
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
    internal TypeUse Use(ITypeSymbol symbol)
    {
        var components = symbol switch
        {
            IArrayTypeSymbol a => ImmutableArray.Create(Use(a.ElementType)),
            IPointerTypeSymbol p => ImmutableArray.Create(Use(p.PointedAtType)),
            INamedTypeSymbol n => n.TypeArguments.Select(x => UseShallow(x)).ToImmutableArray(),
            _ => []
        };
        return new(symbol is INamedTypeSymbol named ? RegisterType(named) : null, TypeName(symbol),
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
        types[id] = new(id, compilation, TypeName(s), s.DeclaredAccessibility.ToString(), [], Attributes(s), Declarations(s));
        var bases = new List<TypeUse>();
        if (s.BaseType is not null) bases.Add(Use(s.BaseType));
        bases.AddRange(s.Interfaces.Select(Use));
        types[id] = types[id] with { BaseTypes = bases.ToImmutableArray() };
        return id;
    }
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
        // Insert before recursively registering containing local functions.
        methods[id] = new(id, compilation, s.ContainingType is null ? null : RegisterType(s.ContainingType), null, kind, Signature(s), s.DeclaredAccessibility.ToString(), Use(s.ReturnType), s.Parameters.Select(x => new ParameterFact(x.Name, Use(x.Type), x.RefKind.ToString())).ToImmutableArray(), Attributes(s), body, Declarations(s), s.IsAsync,
            s.ReturnType is INamedTypeSymbol returnType && returnType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && returnType.Name is "Task" or "ValueTask");
        if (s.ContainingSymbol is IMethodSymbol parent) methods[id] = methods[id] with { ContainingCallable = RegisterMethod(parent) };
        foreach (var use in new[] { methods[id].ReturnType }.Concat(methods[id].Parameters.Select(x => x.Type))) AddDependency(methods[id], use, DependencyKind.Signature, methods[id].Declarations.FirstOrDefault());
        return id;
    }
    private void AddDependency(CallableFact owner, TypeUse type, DependencyKind kind, SourceEvidence? evidence)
    {
        dependencies.Add(new(owner.DeclaringType, owner.Id, type, kind, evidence));
        foreach (var child in type.Components) AddDependency(owner, child, kind, evidence);
    }
    public void Extract(CancellationToken token)
    {
        foreach (var type in sourceTypes) RegisterType(type);
        foreach (var entry in bodies) RegisterMethod(entry.Symbol);
        foreach (var entry in bodies)
        {
            token.ThrowIfCancellationRequested();
            var id = RegisterMethod(entry.Symbol);
            if (IsControllerAction(entry.Symbol)) actions.Add(new(id, "Declared public instance method on ControllerBase descendant or [Controller] type; excludes [NonAction]."));
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
            else if (methods[id].Body == BodyStatus.Source) walker.Gap("Source body has no operation tree.", entry.Node);
            walker.ExtractRegions(entry.Node);
            if (entry.Symbol.MethodKind == MethodKind.Constructor && entry.Node is ConstructorDeclarationSyntax { Initializer: null } && entry.Symbol.ContainingType.BaseType?.SpecialType != SpecialType.System_Object)
                walker.Gap("Implicit base constructor invocation is outside supported extraction.", entry.Node);
        }
        foreach (var type in types.Values.ToArray()) foreach (var b in type.BaseTypes) dependencies.Add(new(type.Id, null, b, DependencyKind.Inheritance, type.Declarations.FirstOrDefault()));
    }
    private static bool IsControllerAction(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsGenericMethod || method.DeclaredAccessibility != Accessibility.Public) return false;
        for (var current = method; current is not null; current = current.OverriddenMethod)
            if (Attributes(current).Contains("Microsoft.AspNetCore.Mvc.NonActionAttribute")) return false;
        var type = method.ContainingType;
        if (type.IsAbstract || type.IsGenericType) return false;
        for (var current = type; current is not null; current = current.BaseType)
            if (Attributes(current).Contains("Microsoft.AspNetCore.Mvc.NonControllerAttribute")) return false;
        for (var t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == "Microsoft.AspNetCore.Mvc.ControllerBase" || Attributes(t).Contains("Microsoft.AspNetCore.Mvc.ControllerAttribute")) return true;
        return false;
    }
    public ProgramSnapshot Snapshot() => new()
    {
        Compilations = compilations,
        Types = types.Values.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        Callables = methods.Values.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        CallSites = calls.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        Dependencies = dependencies.Distinct().OrderBy(x => x.OwningCallable?.Value, StringComparer.Ordinal).ThenBy(x => x.ReferencedType.DisplayName, StringComparer.Ordinal).ToImmutableArray(),
        ThrowSites = throws.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        Gaps = gaps.OrderBy(x => x.Compilation?.Value, StringComparer.Ordinal).ThenBy(x => x.Evidence?.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence?.SpanStart).ThenBy(x => x.Message, StringComparer.Ordinal).ToImmutableArray(),
        Catches = catches.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        ExceptionRegions = regions.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        ControllerActions = actions.Distinct().OrderBy(x => x.Method.Value, StringComparer.Ordinal).ToImmutableArray(),
        BoundaryHandling = boundaries.OrderBy(x => x.Evidence.Path, StringComparer.Ordinal).ThenBy(x => x.Evidence.SpanStart).ToImmutableArray()
    };

    private sealed class BodyWalker(FactExtractor facts, SemanticModel model, CompilationFact compilation, CallableId owner, CancellationToken token) : OperationWalker
    {
        private int ordinal;
        private string NodeId(string kind, SyntaxNode n) => $"{owner.Value}|{kind}|{n.SyntaxTree.FilePath}:{n.SpanStart}";
        private static bool IsDeclaration(SyntaxNode node) => node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax;
        private ImmutableArray<ExceptionRegionId> Protected(SyntaxNode node) => node.Ancestors().TakeWhile(x => !IsDeclaration(x))
            .OfType<TryStatementSyntax>().Where(x => x.Block.Span.Contains(node.Span)).Select(x => new ExceptionRegionId(NodeId("try", x))).ToImmutableArray();
        private CatchId? CatchOwner(SyntaxNode node) => node.Ancestors().TakeWhile(x => !IsDeclaration(x)).OfType<CatchClauseSyntax>().Select(x => (CatchId?)new CatchId(NodeId("catch", x))).FirstOrDefault();
        internal void Gap(string message, SyntaxNode node) => facts.gaps.Add(new(GapKind.Unsupported, compilation.Id, owner, message, facts.Evidence(node)));
        public override void Visit(IOperation? operation) { token.ThrowIfCancellationRequested(); base.Visit(operation); }
        public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation) { }
        public override void VisitLocalFunction(ILocalFunctionOperation operation) { }
        private void Call(IMethodSymbol? target, SyntaxNode syntax, DispatchKind dispatch, bool implicitCall = false, bool awaited = false)
        {
            var id = target is null ? (CallableId?)null : facts.RegisterMethod(target);
            facts.calls.Add(new(new(NodeId("call", syntax) + ":" + ordinal++), owner, id, target is null ? "<unresolved>" : Signature(target), dispatch,
                target?.TypeArguments.Select(facts.Use).ToImmutableArray() ?? [], facts.Evidence(syntax), implicitCall, Protected(syntax), awaited));
        }
        private static DispatchKind Dispatch(IMethodSymbol target, SyntaxNode syntax)
        {
            if (target.MethodKind == MethodKind.DelegateInvoke) return DispatchKind.Delegate;
            if (syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } } or MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }) return DispatchKind.Direct;
            if (target.ContainingType.TypeKind == TypeKind.Interface) return DispatchKind.Interface;
            if (target.IsVirtual || target.IsOverride || target.IsAbstract) return DispatchKind.Virtual;
            return DispatchKind.Direct;
        }
        private static bool Awaited(IOperation operation)
        {
            var parent = operation.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation) parent = parent.Parent;
            return parent is IAwaitOperation;
        }
        public override void VisitInvocation(IInvocationOperation operation)
        {
            Call(operation.TargetMethod, operation.Syntax, Dispatch(operation.TargetMethod, operation.Syntax), operation.IsImplicit, Awaited(operation));
            if (operation.Type is { } type) facts.AddDependency(facts.methods[owner], facts.Use(type), DependencyKind.BodyUse, facts.Evidence(operation.Syntax));
            base.VisitInvocation(operation);
        }
        public override void VisitConversion(IConversionOperation operation)
        {
            if (operation.OperatorMethod is not null) Call(operation.OperatorMethod, operation.Syntax, DispatchKind.Direct, operation.IsImplicit);
            base.VisitConversion(operation);
        }
        public override void VisitBinaryOperator(IBinaryOperation operation)
        {
            if (operation.OperatorMethod is not null) Call(operation.OperatorMethod, operation.Syntax, DispatchKind.Direct, operation.IsImplicit);
            base.VisitBinaryOperator(operation);
        }
        public override void VisitUnaryOperator(IUnaryOperation operation)
        {
            if (operation.OperatorMethod is not null) Call(operation.OperatorMethod, operation.Syntax, DispatchKind.Direct, operation.IsImplicit);
            base.VisitUnaryOperator(operation);
        }
        public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
        {
            if (operation.OperatorMethod is not null) Call(operation.OperatorMethod, operation.Syntax, DispatchKind.Direct, operation.IsImplicit);
            base.VisitIncrementOrDecrement(operation);
        }
        public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
        {
            if (operation.OperatorMethod is not null) Call(operation.OperatorMethod, operation.Syntax, DispatchKind.Direct, operation.IsImplicit);
            if (operation.InConversion.MethodSymbol is not null || operation.OutConversion.MethodSymbol is not null) Gap("Compound assignment user-defined conversions are outside supported extraction.", operation.Syntax);
            base.VisitCompoundAssignment(operation);
        }
        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            Call(operation.Constructor, operation.Syntax, DispatchKind.Direct, operation.IsImplicit);
            if (operation.Type is { } type) facts.AddDependency(facts.methods[owner], facts.Use(type), DependencyKind.Construction, facts.Evidence(operation.Syntax));
            base.VisitObjectCreation(operation);
        }
        public override void VisitDynamicInvocation(IDynamicInvocationOperation operation) { Call(null, operation.Syntax, DispatchKind.Dynamic); base.VisitDynamicInvocation(operation); }
        public override void VisitFunctionPointerInvocation(IFunctionPointerInvocationOperation operation) { Call(null, operation.Syntax, DispatchKind.Unknown); base.VisitFunctionPointerInvocation(operation); }
        public override void VisitPropertyReference(IPropertyReferenceOperation operation)
        {
            var p = operation.Parent;
            var writes = p is ISimpleAssignmentOperation assignment && assignment.Target == operation || p is ICompoundAssignmentOperation compound && compound.Target == operation || p is IIncrementOrDecrementOperation;
            var reads = !writes || p is ICompoundAssignmentOperation or IIncrementOrDecrementOperation;
            if (reads && operation.Property.GetMethod is { } get) Call(get, operation.Syntax, Dispatch(get, operation.Syntax), true);
            if (writes && operation.Property.SetMethod is { } set) Call(set, operation.Syntax, Dispatch(set, operation.Syntax), true);
            if (p is ICoalesceAssignmentOperation) Gap("Coalescing property assignment accessor semantics unsupported.", operation.Syntax);
            base.VisitPropertyReference(operation);
        }
        public override void VisitEventAssignment(IEventAssignmentOperation operation)
        {
            if (operation.EventReference is IEventReferenceOperation ev)
            {
                var method = operation.Adds ? ev.Event.AddMethod : ev.Event.RemoveMethod;
                if (method is not null) Call(method, operation.Syntax, Dispatch(method, operation.Syntax), true);
            }
            base.VisitEventAssignment(operation);
        }
        public override void VisitThrow(IThrowOperation operation)
        {
            var staticOperand = operation.Exception;
            while (staticOperand is IConversionOperation { IsImplicit: true, OperatorMethod: null } or IParenthesizedOperation)
                staticOperand = staticOperand is IConversionOperation staticConversion ? staticConversion.Operand : ((IParenthesizedOperation)staticOperand).Operand;
            var exactOperand = operation.Exception;
            while (exactOperand is IConversionOperation { OperatorMethod: null } or IParenthesizedOperation)
                exactOperand = exactOperand is IConversionOperation exactConversion ? exactConversion.Operand : ((IParenthesizedOperation)exactOperand).Operand;
            var staticType = staticOperand?.Type;
            var exactType = exactOperand is IObjectCreationOperation { Type: { TypeKind: not TypeKind.Error } constructedType } ? constructedType : null;
            if (staticOperand?.ConstantValue is { HasValue: true, Value: null })
                Gap("Throwing null produces an implicit NullReferenceException; implicit exception origins are outside supported analysis.", operation.Syntax);
            facts.throws.Add(new(new(NodeId("throw", operation.Syntax)), owner, operation.Exception is null ? ThrowKind.Rethrow : ThrowKind.Explicit,
                staticType is null ? null : facts.Use(staticType), facts.Evidence(operation.Syntax), Protected(operation.Syntax), exactType is not null,
                CatchOwner(operation.Syntax), exactType is null ? null : facts.Use(exactType)));
            base.VisitThrow(operation);
        }
        public override void VisitAwait(IAwaitOperation operation)
        {
            IOperation operand = operation.Operation;
            while (operand is IConversionOperation or IParenthesizedOperation) operand = operand.ChildOperations.First();
            if (operand is not IInvocationOperation) Gap("Await of an aliased task/custom awaitable: exception origin tracking unsupported.", operation.Syntax);
            base.VisitAwait(operation);
        }
        public override void VisitForEachLoop(IForEachLoopOperation operation)
        {
            Gap("Implicit enumeration/disposal calls are outside supported extraction.", operation.Syntax);
            base.VisitForEachLoop(operation);
        }
        public override void VisitUsing(IUsingOperation operation) { Gap("Implicit disposal and using exception propagation unsupported.", operation.Syntax); base.VisitUsing(operation); }
        public override void VisitUsingDeclaration(IUsingDeclarationOperation operation) { Gap("Implicit disposal and using exception propagation unsupported.", operation.Syntax); base.VisitUsingDeclaration(operation); }
        public override void VisitInvalid(IInvalidOperation operation) { Gap("Invalid operation or unresolved binding.", operation.Syntax); Call(null, operation.Syntax, DispatchKind.Unknown); base.VisitInvalid(operation); }
        public void ExtractRegions(SyntaxNode declaration)
        {
            foreach (var region in declaration.DescendantNodes(x => x == declaration || !IsDeclaration(x)).OfType<TryStatementSyntax>())
            {
                var ids = ImmutableArray.CreateBuilder<CatchId>();
                foreach (var clause in region.Catches)
                {
                    var id = new CatchId(NodeId("catch", clause)); ids.Add(id);
                    var statements = clause.Block.Statements;
                    var outcome = HandlerOutcome.Unknown; ThrowSiteId? next = null;
                    if (statements.Count == 0 || statements.All(x => x is ReturnStatementSyntax { Expression: null } or EmptyStatementSyntax)) outcome = HandlerOutcome.Intercepts;
                    if (statements.Count == 1 && statements[0] is ThrowStatementSyntax t)
                    {
                        outcome = t.Expression is null ? HandlerOutcome.Rethrows : HandlerOutcome.Replaces;
                        next = new ThrowSiteId(NodeId("throw", t));
                    }
                    var type = clause.Declaration is null ? null : model.GetTypeInfo(clause.Declaration.Type, token).Type;
                    facts.catches.Add(new(id, owner, type is null ? null : facts.Use(type), clause.Filter?.FilterExpression.ToString(),
                        facts.Evidence(clause), facts.Evidence(clause.Block), outcome, next));
                }
                facts.regions.Add(new(new(NodeId("try", region)), owner, facts.Evidence(region.Block), ids.ToImmutable(), region.Finally is not null));
            }
            foreach (var node in declaration.DescendantNodes(x => x == declaration || !IsDeclaration(x)).Where(x => x is YieldStatementSyntax or LockStatementSyntax))
                Gap("Iterator/lock exception behaviour is outside the supported model.", node);
        }
    }
}
