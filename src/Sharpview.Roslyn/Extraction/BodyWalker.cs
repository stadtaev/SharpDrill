using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Sharpview.Core;

namespace Sharpview.Roslyn;

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
