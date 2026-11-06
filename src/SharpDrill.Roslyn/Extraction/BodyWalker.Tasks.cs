using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpDrill.Roslyn;

// BodyWalker's hooks for tasks: records what TaskApi decides as call sites and gaps.
internal sealed partial class FactExtractor
{
    private sealed partial class BodyWalker
    {
        private bool Awaited(IOperation operation)
        {
            // `await call.ConfigureAwait(false)` observes the call's task; `return call;` hands it to the caller, which observes it.
            var parent = TaskApi.Consumer(operation);
            if (TaskApi.Observes(parent)) return true;
            // Only a task can be observed later: a string local passed as an argument inside an await is not awaited.
            if (operation.Type is not { Name: "Task" or "ValueTask" }) return false;
            if (parent is IReturnOperation) return true;
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
                    .Any(n => n.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, token).Symbol, local)
                        // `await Task.WhenAny(t, u)` completes normally whatever t and u hold.
                        && !n.Ancestors().TakeWhile(a => a != x).OfType<InvocationExpressionSyntax>().Any(i => model.GetSymbolInfo(i, token).Symbol is IMethodSymbol { Name: "WhenAny" } m && TaskApi.IsTaskType(m.ContainingType))));

        // One task expression observed at `at`. A call or local was recorded where it was produced (see Awaited); the calls
        // behind a field surface here, inside this body's try blocks; anything else cannot be followed.
        private void Observe(IOperation? task, SyntaxNode at)
        {
            task = TaskApi.Unwrap(task);
            if (TaskApi.IsTaskMethod(task, "WhenAll"))
                foreach (var inner in TaskApi.Tasks(((IInvocationOperation)task!).Arguments[0].Value))
                    if (inner is null) Gap("Task.WhenAll of a collection: the tasks in it are not known.", at); else Observe(inner, at);
            else if (task is IFieldReferenceOperation { Field: var field } && TaskApi.StoredTask(field, model.Compilation, token).Calls is { IsEmpty: false } stored) Surface(stored, at);
            else if (task is not (IInvocationOperation or ILocalReferenceOperation)) Gap("Await of a task held in a field, property or parameter: its producing call is not known.", at);
        }
        private void Surface(ImmutableArray<IInvocationOperation> calls, SyntaxNode at)
        {
            foreach (var call in calls)
            {
                var target = TaskApi.RunDelegate(call) ?? call.TargetMethod;
                Call(target, at, Dispatch(target, call.Syntax), implicitCall: true, awaited: true);
            }
        }
        public override void VisitAwait(IAwaitOperation operation) { Observe(operation.Operation, operation.Syntax); base.VisitAwait(operation); }

        // `Task Get() => _task` hands the task on: what it holds leaves here and is observed where Get's result is.
        public override void VisitReturn(IReturnOperation operation)
        {
            if (operation.Kind == OperationKind.Return && TaskApi.Unwrap(operation.ReturnedValue) is IFieldReferenceOperation { Field: var field } && TaskApi.IsTaskType(field.Type))
            {
                if (TaskApi.StoredTask(field, model.Compilation, token).Calls is { IsEmpty: false } stored) Surface(stored, operation.Syntax);
                else Gap("Return of a task held in a field: its producing call is not known.", operation.Syntax);
            }
            base.VisitReturn(operation);
        }

        // Task API effects of an invocation: Task.Run's delegate when its task is observed (unobserved, the exception reaches
        // nobody); GetResult observes like await; Wait/WaitAll wrap in AggregateException, which is not modelled.
        private void TaskCall(IInvocationOperation call)
        {
            if (TaskApi.RunDelegate(call) is { } body && Awaited(call)) Call(body, call.Syntax, Dispatch(body, call.Syntax), implicitCall: true, awaited: true);
            if (TaskApi.IsAwaiterResult(call) && TaskApi.Unwrap(call.Instance) is IInvocationOperation { TargetMethod.Name: "GetAwaiter", Instance: var task }) Observe(task, call.Syntax);
            if (TaskApi.IsTaskMethod(call, "Wait")) Blocks(call.Instance, call.Syntax);
            if (TaskApi.IsTaskMethod(call, "WaitAll") && !call.Arguments.IsEmpty) foreach (var inner in TaskApi.Tasks(call.Arguments[0].Value)) Blocks(inner, call.Syntax);
        }
        // A call or local already reports its task as not awaited; a task from elsewhere would otherwise pass silently.
        private void Blocks(IOperation? task, SyntaxNode at)
        {
            if (TaskApi.Unwrap(task) is not (IInvocationOperation or ILocalReferenceOperation)) Gap("Blocking on a stored task (.Result, .Wait, Task.WaitAll) throws AggregateException, which is not modelled.", at);
        }

        // `_task = CallAsync()` with `await _task` elsewhere: a faulted task throws at the await, not at this assignment.
        private bool StoresObservedTask(IOperation call) =>
            call.Type is { Name: "Task" or "ValueTask" } && TaskApi.Consumer(call) switch
            {
                IFieldInitializerOperation { InitializedFields: [var field] } => field,
                ISimpleAssignmentOperation { Target: IFieldReferenceOperation reference } => reference.Field,
                ICoalesceAssignmentOperation { Target: IFieldReferenceOperation reference } => reference.Field,
                _ => null,
            } is { } stored && TaskApi.StoredTask(stored, model.Compilation, token) is { Calls.IsEmpty: false, Observed: true };

    }
}
