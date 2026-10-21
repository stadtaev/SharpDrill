using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SharpView.Core;

namespace SharpView.Roslyn;

// Walks one callable body's operation tree and records its call sites, throws, catches, regions and gaps.
internal sealed partial class FactExtractor
{
    private sealed class BodyWalker(FactExtractor facts, SemanticModel model, CompilationFact compilation, CallableId owner, CancellationToken token) : OperationWalker
    {
        private int ordinal;
        private string NodeId(string kind, SyntaxNode n) => $"{owner.Value}|{kind}|{n.SyntaxTree.FilePath}:{n.SpanStart}";
        private static bool IsDeclaration(SyntaxNode node) => node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax;
        private ImmutableArray<ExceptionRegionId> Protected(SyntaxNode node) => node.Ancestors().TakeWhile(x => !IsDeclaration(x))
            .OfType<TryStatementSyntax>().Where(x => x.Block.Span.Contains(node.Span)).Select(x => new ExceptionRegionId(NodeId("try", x))).ToImmutableArray();
        private CatchId? CatchOwner(SyntaxNode node) => node.Ancestors().TakeWhile(x => !IsDeclaration(x)).OfType<CatchClauseSyntax>().Select(x => (CatchId?)new CatchId(NodeId("catch", x))).FirstOrDefault();
        internal void Gap(string message, SyntaxNode node) => facts.gaps.Add(new(GapKind.Unsupported, compilation.Id, owner, message, facts.Evidence(node)));
        internal void InternalGap(string message, SyntaxNode node) => facts.gaps.Add(new(GapKind.Internal, compilation.Id, owner, message, facts.Evidence(node)));
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
        private bool Awaited(IOperation operation)
        {
            var parent = operation.Parent;
            // `await call.ConfigureAwait(false)` observes the call's task; `return call;` hands it to the caller, which observes it.
            while (parent is IConversionOperation or IParenthesizedOperation or IInvocationOperation { TargetMethod.Name: "ConfigureAwait" }) parent = parent.Parent;
            if (parent is IAwaitOperation || parent is IReturnOperation && operation.Type is { Name: "Task" or "ValueTask" }) return true;
            // `var t = CallAsync(); ... await t;` or `await Task.WhenAll(t, u)`: the task is observed where the local is awaited.
            var local = parent switch
            {
                IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator } => declarator.Symbol,
                ISimpleAssignmentOperation { Target: ILocalReferenceOperation reference } => reference.Local,
                _ => null,
            };
            return local is not null && AwaitsLocal(local, operation.Syntax);
        }
        private bool AwaitsLocal(ILocalSymbol local, SyntaxNode within) =>
            within.Ancestors().FirstOrDefault(IsDeclaration) is { } body && body.DescendantNodes().OfType<AwaitExpressionSyntax>()
                .Any(x => x.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(n => n.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, token).Symbol, local)));
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
        internal void ImplicitBaseConstructor(IMethodSymbol constructor, SyntaxNode syntax) => Call(constructor, syntax, DispatchKind.Direct, implicitCall: true);
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
            // `throw e` of the catch variable rethrows the caught exception (with a reset stack trace); it is not a new origin.
            var rethrow = operation.Exception is null || staticOperand is ILocalReferenceOperation { Local.DeclaringSyntaxReferences: [var declaration] } && declaration.GetSyntax(token) is CatchDeclarationSyntax;
            facts.throws.Add(new(new(NodeId("throw", operation.Syntax)), owner, rethrow ? ThrowKind.Rethrow : ThrowKind.Explicit,
                staticType is null ? null : facts.Use(staticType), facts.Evidence(operation.Syntax), Protected(operation.Syntax), exactType is not null,
                CatchOwner(operation.Syntax), exactType is null ? null : facts.Use(exactType)));
            base.VisitThrow(operation);
        }
        public override void VisitAwait(IAwaitOperation operation)
        {
            IOperation operand = operation.Operation;
            while (operand is IConversionOperation or IParenthesizedOperation) operand = operand.ChildOperations.First();
            // An awaited local was observed at the call that produced it (see Awaited).
            if (operand is not (IInvocationOperation or ILocalReferenceOperation)) Gap("Await of a task held in a field, property or parameter: its producing call is not known.", operation.Syntax);
            base.VisitAwait(operation);
        }
        // foreach and using call GetEnumerator/MoveNext/Dispose implicitly. They are recorded as implicit call sites, so
        // library enumerators are library calls and a Dispose written in source is analysed like any other callee.
        // `await foreach` and `await using` are the same implicit calls, awaited.
        public override void VisitForEachLoop(IForEachLoopOperation operation)
        {
            // Arrays are enumerated without calls (the operation tree wraps them in a conversion to IEnumerable).
            var collection = operation.Collection;
            while (collection is IConversionOperation { IsImplicit: true } conversion) collection = conversion.Operand;
            if (collection.Type is IArrayTypeSymbol) { base.VisitForEachLoop(operation); return; }
            if (operation.Syntax is not CommonForEachStatementSyntax statement) InternalGap("Enumeration pattern could not be bound.", operation.Syntax);
            else
            {
                // `await foreach` awaits MoveNextAsync and DisposeAsync.
                var info = model.GetForEachStatementInfo(statement);
                var enumerator = info.GetEnumeratorMethod?.ReturnType;
                if (info.GetEnumeratorMethod is { } getEnumerator) ImplicitCall(getEnumerator, operation.Collection.Type, operation.Syntax);
                if (info.MoveNextMethod is { } moveNext) ImplicitCall(moveNext, enumerator, operation.Syntax, operation.IsAsynchronous);
                if (info.DisposeMethod is { } dispose) ImplicitCall(dispose, enumerator, operation.Syntax, operation.IsAsynchronous);
                if (info.GetEnumeratorMethod is null) Gap("Enumeration pattern could not be bound.", operation.Syntax);
            }
            base.VisitForEachLoop(operation);
        }
        public override void VisitUsing(IUsingOperation operation) { Disposal(operation.IsAsynchronous, operation.Resources, operation.Syntax); base.VisitUsing(operation); }
        public override void VisitUsingDeclaration(IUsingDeclarationOperation operation) { Disposal(operation.IsAsynchronous, operation.DeclarationGroup, operation.Syntax); base.VisitUsingDeclaration(operation); }
        private void Disposal(bool asynchronous, IOperation? resources, SyntaxNode syntax)
        {
            var types = resources is IVariableDeclarationGroupOperation group
                ? group.Declarations.SelectMany(x => x.Declarators).Select(x => (ITypeSymbol?)x.Symbol.Type)
                : [resources?.Type];
            foreach (var type in types)
                if ((asynchronous ? DisposeAsyncMethod(type) : DisposeMethod(type)) is { } dispose) ImplicitCall(dispose, type, syntax, asynchronous);
        }
        private IMethodSymbol? DisposeAsyncMethod(ITypeSymbol? type)
        {
            if (type is null || model.Compilation.GetTypeByMetadataName("System.IAsyncDisposable") is not { } disposable) return null;
            if (SymbolEqualityComparer.Default.Equals(type, disposable) || type.AllInterfaces.Contains(disposable, SymbolEqualityComparer.Default))
                return disposable.GetMembers("DisposeAsync").OfType<IMethodSymbol>().FirstOrDefault();
            return type.GetMembers("DisposeAsync").OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.IsEmpty && !x.IsStatic);
        }
        // IDisposable.Dispose for ordinary resources; a pattern Dispose() for ref structs.
        private IMethodSymbol? DisposeMethod(ITypeSymbol? type)
        {
            if (type is null) return null;
            var disposable = model.Compilation.GetSpecialType(SpecialType.System_IDisposable);
            if (SymbolEqualityComparer.Default.Equals(type, disposable) || type.AllInterfaces.Contains(disposable, SymbolEqualityComparer.Default))
                return disposable.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault();
            return type.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.IsEmpty && !x.IsStatic);
        }
        // The compiler reaches these members through an interface (IDisposable.Dispose, IEnumerator.MoveNext), but a call
        // written in source binds to the receiver type's own member. Resolve it the same way: a library receiver is then a
        // library call, and a source receiver has candidates only among its own overrides, not every implementer of the interface.
        private void ImplicitCall(IMethodSymbol declared, ITypeSymbol? receiver, SyntaxNode syntax, bool awaited = false)
        {
            var target = declared.ContainingType.TypeKind == TypeKind.Interface && receiver is { TypeKind: TypeKind.Class or TypeKind.Struct }
                && receiver.FindImplementationForInterfaceMember(declared) is IMethodSymbol implementation ? implementation : declared;
            Call(target, syntax, Dispatch(target, syntax), implicitCall: true, awaited);
        }
        public override void VisitInvalid(IInvalidOperation operation) { Gap("Invalid operation or unresolved binding.", operation.Syntax); Call(null, operation.Syntax, DispatchKind.Unknown); base.VisitInvalid(operation); }
        // What happens to the caught exception, from the handler's control flow: normal exits (fall-through, return, break,
        // goto) end it; `throw;` or `throw e` of the catch variable continues it; any other throw replaces it. Calls such as
        // logging in between do not change the outcome. Throws in nested lambdas and nested catch clauses belong elsewhere.
        private (HandlerOutcome Outcome, ThrowSiteId? Next) Handler(CatchClauseSyntax clause)
        {
            var flow = model.AnalyzeControlFlow(clause.Block);
            var leaves = flow is not { Succeeded: true } || flow.EndPointIsReachable || !flow.ExitPoints.IsEmpty;
            var variable = clause.Declaration is { Identifier.ValueText: { Length: > 0 } } ? model.GetDeclaredSymbol(clause.Declaration, token) : null;
            var thrown = clause.Block.DescendantNodes(x => x is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or CatchClauseSyntax))
                .Select(x => (Node: x, Expression: x switch { ThrowStatementSyntax s => s.Expression, ThrowExpressionSyntax e => e.Expression, _ => null }))
                .Where(x => x.Node is ThrowStatementSyntax or ThrowExpressionSyntax).ToArray();
            bool Rethrow(ExpressionSyntax? e) => e is null || variable is not null && e is IdentifierNameSyntax && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e, token).Symbol, variable);
            var rethrows = thrown.Where(x => Rethrow(x.Expression)).ToArray();
            ThrowSiteId Site(SyntaxNode node) => new(NodeId("throw", node));
            if (rethrows.Length > 0) return (!leaves && rethrows.Length == thrown.Length ? HandlerOutcome.Rethrows : HandlerOutcome.MayRethrow, Site(rethrows[0].Node));
            if (!leaves && thrown.Length > 0) return (HandlerOutcome.Replaces, Site(thrown[0].Node));
            return (HandlerOutcome.Intercepts, null);
        }
        public void ExtractRegions(SyntaxNode declaration)
        {
            foreach (var region in declaration.DescendantNodes(x => x == declaration || !IsDeclaration(x)).OfType<TryStatementSyntax>())
            {
                var ids = ImmutableArray.CreateBuilder<CatchId>();
                foreach (var clause in region.Catches)
                {
                    var id = new CatchId(NodeId("catch", clause)); ids.Add(id);
                    var (outcome, next) = Handler(clause);
                    var type = clause.Declaration is null ? null : model.GetTypeInfo(clause.Declaration.Type, token).Type;
                    facts.catches.Add(new(id, owner, type is null ? null : facts.Use(type), clause.Filter?.FilterExpression.ToString(),
                        facts.Evidence(clause), facts.Evidence(clause.Block), outcome, next));
                }
                // A finally runs library cleanup (Dispose, logging) far more often than code that can throw; only the latter can
                // replace the propagating exception.
                facts.regions.Add(new(new(NodeId("try", region)), owner, facts.Evidence(region.Block), ids.ToImmutable(),
                    region.Finally is { } final && RunsSourceOrThrows(final.Block, model, token)));
            }
            // `lock` only adds Monitor calls that cannot raise a modelled exception. An iterator defers its body, so one gap per iterator.
            if (declaration.DescendantNodes(x => x == declaration || !IsDeclaration(x)).FirstOrDefault(x => x is YieldStatementSyntax) is { } yield)
                Gap("Iterator bodies run deferred; exception timing is outside the supported model.", yield);
        }
    }
}
