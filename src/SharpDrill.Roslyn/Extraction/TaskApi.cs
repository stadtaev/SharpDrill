using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpDrill.Roslyn;

// What the BCL Task API does to exceptions, the one place it is known. A task holds what its call threw; it is thrown where
// the task is observed: await, GetAwaiter().GetResult(), or Task.WhenAll of it being observed. Task.WhenAny never throws
// what its tasks hold. Task.Run runs its delegate, so the delegate's exceptions are in the task.
internal static class TaskApi
{
    internal static bool IsTaskType(ITypeSymbol? type) => type is { Name: "Task" or "ValueTask" } && type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    internal static bool IsTaskMethod(IOperation? operation, string name) => operation is IInvocationOperation { TargetMethod: var method } && method.Name == name && IsTaskType(method.ContainingType);
    internal static bool IsAwaiterResult(IOperation? operation) => operation is IInvocationOperation { TargetMethod: { Name: "GetResult", ContainingNamespace: var ns } } && ns?.ToDisplayString() == "System.Runtime.CompilerServices";
    internal static bool Observes(IOperation? operation) => operation is IAwaitOperation || IsAwaiterResult(operation);

    // What a task expression feeds, past what only passes the same task along.
    internal static IOperation? Consumer(IOperation? operation)
    {
        var parent = operation?.Parent;
        while (parent is IConversionOperation or IParenthesizedOperation or IArrayInitializerOperation or IArrayCreationOperation or ICollectionExpressionOperation
            or IInvocationOperation { TargetMethod.Name: "ConfigureAwait" or "GetAwaiter" } or IArgumentOperation { Parent: IInvocationOperation { TargetMethod.Name: "WhenAll" } }
            || IsTaskMethod(parent, "WhenAll")) parent = parent!.Parent;
        return parent;
    }
    internal static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation or IInvocationOperation { TargetMethod.Name: "ConfigureAwait" }) operation = operation.ChildOperations.First();
        return operation;
    }
    // The tasks in a WhenAll/WaitAll argument; null for one that a collection hides.
    internal static IEnumerable<IOperation?> Tasks(IOperation? value) => Unwrap(value) switch
    {
        IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues.SelectMany(Tasks),
        ICollectionExpressionOperation collection => collection.Elements.SelectMany(Tasks),
        { Type: var type } task when IsTaskType(type) => [task],
        _ => [null],
    };

    // The delegate Task.Run was given, when it is one a call site can name.
    internal static IMethodSymbol? RunDelegate(IInvocationOperation call) =>
        !IsTaskMethod(call, "Run") || call.Arguments.IsEmpty ? null : Unwrap(call.Arguments[0].Value) switch
        {
            IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } => lambda.Symbol,
            IDelegateCreationOperation { Target: IMethodReferenceOperation reference } => reference.Method,
            _ => null,
        };

    // The calls a field's task can come from. Known only when nothing outside this type can write the field, every write
    // is a call, and no call can throw at the assignment instead (a non-async source method does; an implementation
    // behind an interface or virtual member is assumed async, like the other Task-returning candidates).
    internal static (ImmutableArray<IInvocationOperation> Calls, bool Observed) StoredTask(IFieldSymbol field, Compilation compilation, CancellationToken token)
    {
        (ImmutableArray<IInvocationOperation>, bool) unknown = ([], false);
        field = field.OriginalDefinition;
        if (!field.IsReadOnly && field.DeclaredAccessibility != Accessibility.Private || !SymbolEqualityComparer.Default.Equals(field.ContainingAssembly, compilation.Assembly)) return unknown;
        var writes = field.DeclaringSyntaxReferences.Select(x => x.GetSyntax(token)).OfType<VariableDeclaratorSyntax>()
            .Where(x => x.Initializer is not null).Select(x => x.Initializer!.Value).ToList();
        var observed = false;
        foreach (var type in field.ContainingType.DeclaringSyntaxReferences.Select(x => x.GetSyntax(token)))
        {
            var semantic = compilation.GetSemanticModel(type.SyntaxTree);
            foreach (var name in type.DescendantNodes().OfType<IdentifierNameSyntax>().Where(x => x.Identifier.ValueText == field.Name
                && SymbolEqualityComparer.Default.Equals(semantic.GetSymbolInfo(x, token).Symbol?.OriginalDefinition, field)))
            {
                ExpressionSyntax reference = name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : name;
                if (reference.Parent is AssignmentExpressionSyntax assignment && assignment.Left == reference)
                {
                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && !assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)) return unknown;
                    writes.Add(assignment.Right);
                }
                // Written some way that hides the value: `Exchange(ref _task, ...)`, `(_task, x) = ...`, `ref var r = ref _task`.
                else if (reference.Parent is RefExpressionSyntax or ArgumentSyntax { Parent: TupleExpressionSyntax } or ArgumentSyntax { RefKindKeyword.RawKind: not 0 }) return unknown;
                else if (Consumer(semantic.GetOperation(reference, token)) is var consumer && (Observes(consumer) || consumer is IReturnOperation)) observed = true;
            }
        }
        var calls = ImmutableArray.CreateBuilder<IInvocationOperation>();
        foreach (var write in writes)
        {
            var value = compilation.GetSemanticModel(write.SyntaxTree).GetOperation(write, token);
            while (value is IConversionOperation or IParenthesizedOperation) value = value.ChildOperations.First();
            if (value is not IInvocationOperation { TargetMethod: var target } call
                || target.DeclaringSyntaxReferences.Length > 0 && !target.IsAsync && !target.IsAbstract && !target.IsVirtual) return unknown;
            calls.Add(call);
        }
        return ([.. calls.DistinctBy(x => x.TargetMethod, SymbolEqualityComparer.Default)], observed);
    }
}
