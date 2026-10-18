using System.Collections.Immutable;
using SharpView.Graph;

namespace SharpView.Core;

// Source is the owning type (the declaring type when a callable owns the fact); OwnerCallable keeps that callable.
// Inheritance facts contribute one edge per declared type in the base type, including its type arguments; other
// dependency kinds are already one fact per type argument.
public sealed record DependencyEdge(TypeId Source, TypeId Target, DependencyFact Fact, CallableId? OwnerCallable) : IEdge<TypeId>;

// Whole-program type dependency graph over every type, edges in dependency-fact order. Facts whose referenced type
// has no declaration (type parameters) or whose owner is unknown produce no edge.
public static class DependencyGraph
{
    public static DiGraph<TypeId, DependencyEdge> Build(ProgramIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = DiGraph<TypeId, DependencyEdge>.CreateBuilder();
        foreach (var type in index.Snapshot.Types) { cancellationToken.ThrowIfCancellationRequested(); builder.AddNode(type.Id); }
        foreach (var dependency in index.Snapshot.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallableId? ownerCallable = dependency.OwningCallable is { } owner && index.ContainsCallable(owner) ? owner : null;
            var source = ownerCallable is { } callable ? index.Callable(callable).DeclaringType ?? dependency.OwningType : dependency.OwningType;
            if (source is not { } from || !index.TryGetType(from, out _)) continue;
            foreach (var use in dependency.Kind == DependencyKind.Inheritance ? Flatten(dependency.ReferencedType, cancellationToken) : [dependency.ReferencedType])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (use.Declaration is { } target && index.TryGetType(target, out _)) builder.AddEdge(new(from, target, dependency, ownerCallable));
            }
        }
        return builder.Build();
    }

    private static IEnumerable<TypeUse> Flatten(TypeUse use, CancellationToken cancellationToken)
    {
        var pending = new Stack<TypeUse>(); pending.Push(use);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return current;
            for (var i = current.Components.Length - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Push(current.Components[i]);
            }
        }
    }
}
