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
            // `await call.ConfigureAwait(false)` observes the call's task; `return call;` hands it to the caller, which observes it.
            while (parent is IConversionOperation or IParenthesizedOperation or IInvocationOperation { TargetMethod.Name: "ConfigureAwait" }) parent = parent.Parent;
            return parent is IAwaitOperation || parent is IReturnOperation && operation.Type is { Name: "Task" or "ValueTask" };
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
        // foreach and using call GetEnumerator/MoveNext/Dispose implicitly. They are recorded as implicit call sites, so
        // library enumerators are library calls and a Dispose written in source is analysed like any other callee.
        // Asynchronous enumeration and disposal stay a gap: awaited propagation of implicit calls is unsupported.
        public override void VisitForEachLoop(IForEachLoopOperation operation)
        {
            // Arrays are enumerated without calls (the operation tree wraps them in a conversion to IEnumerable).
            var collection = operation.Collection;
            while (collection is IConversionOperation { IsImplicit: true } conversion) collection = conversion.Operand;
            if (collection.Type is IArrayTypeSymbol) { base.VisitForEachLoop(operation); return; }
            if (operation.IsAsynchronous || operation.Syntax is not CommonForEachStatementSyntax statement)
                Gap("Asynchronous enumeration (await foreach) is outside supported extraction.", operation.Syntax);
            else
            {
                var info = model.GetForEachStatementInfo(statement);
                var enumerator = info.GetEnumeratorMethod?.ReturnType;
                if (info.GetEnumeratorMethod is { } getEnumerator) ImplicitCall(getEnumerator, operation.Collection.Type, operation.Syntax);
                if (info.MoveNextMethod is { } moveNext) ImplicitCall(moveNext, enumerator, operation.Syntax);
                if (info.DisposeMethod is { } dispose) ImplicitCall(dispose, enumerator, operation.Syntax);
                if (info.GetEnumeratorMethod is null) Gap("Enumeration pattern could not be bound.", operation.Syntax);
            }
            base.VisitForEachLoop(operation);
        }
        public override void VisitUsing(IUsingOperation operation) { Disposal(operation.IsAsynchronous, operation.Resources, operation.Syntax); base.VisitUsing(operation); }
        public override void VisitUsingDeclaration(IUsingDeclarationOperation operation) { Disposal(operation.IsAsynchronous, operation.DeclarationGroup, operation.Syntax); base.VisitUsingDeclaration(operation); }
        private void Disposal(bool asynchronous, IOperation? resources, SyntaxNode syntax)
        {
            if (asynchronous) { Gap("Asynchronous disposal (await using) is outside supported extraction.", syntax); return; }
            var types = resources is IVariableDeclarationGroupOperation group
                ? group.Declarations.SelectMany(x => x.Declarators).Select(x => (ITypeSymbol?)x.Symbol.Type)
                : [resources?.Type];
            foreach (var type in types)
                if (DisposeMethod(type) is { } dispose) ImplicitCall(dispose, type, syntax);
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
        private void ImplicitCall(IMethodSymbol declared, ITypeSymbol? receiver, SyntaxNode syntax)
        {
            var target = declared.ContainingType.TypeKind == TypeKind.Interface && receiver is { TypeKind: TypeKind.Class or TypeKind.Struct }
                && receiver.FindImplementationForInterfaceMember(declared) is IMethodSymbol implementation ? implementation : declared;
            Call(target, syntax, Dispatch(target, syntax), implicitCall: true);
        }
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
