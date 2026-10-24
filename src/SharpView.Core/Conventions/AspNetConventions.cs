using System.Collections.Immutable;

namespace SharpView.Core;

// ASP.NET Core conventions classified from extracted facts, so framework knowledge stays out of the snapshot.
public static class AspNetConventions
{
    public const string ControllerActionConvention = "Declared public instance method on ControllerBase descendant or [Controller] type; excludes [NonAction].";
    private const string ControllerBase = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string ControllerAttribute = "Microsoft.AspNetCore.Mvc.ControllerAttribute";
    private const string NonControllerAttribute = "Microsoft.AspNetCore.Mvc.NonControllerAttribute";
    private const string NonActionAttribute = "Microsoft.AspNetCore.Mvc.NonActionAttribute";
    private const CallableModifiers NotAnAction = CallableModifiers.Static | CallableModifiers.Generic | CallableModifiers.Operator
        | CallableModifiers.ExplicitImplementation | CallableModifiers.Finalizer;

    // Source-declared actions ordered by ID. Inherited actions declared in referenced assemblies are not enumerated.
    public static ImmutableArray<ControllerActionFact> ControllerActions(ProgramIndex index, CancellationToken cancellationToken = default)
    {
        var builder = ImmutableArray.CreateBuilder<ControllerActionFact>();
        foreach (var callable in index.Snapshot.Callables.OrderBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsControllerAction(index, callable)) builder.Add(new(callable.Id, ControllerActionConvention));
        }
        return builder.ToImmutable();
    }

    public static bool IsControllerAction(ProgramIndex index, CallableFact method)
    {
        if (method.Body == BodyStatus.MetadataOnly || method.Kind != CallableKind.Method || (method.Modifiers & NotAnAction) != 0
            || method.Accessibility != "Public" || method.DeclaringType is not { } typeId || !index.TryGetType(typeId, out var type)) return false;
        for (CallableFact? current = method; current is not null; current = current.Overrides is { } next && index.TryGetCallable(next, out var overridden) ? overridden : null)
            if (Has(current.Attributes, NonActionAttribute)) return false;
        // Static classes are abstract in the runtime sense; generic covers types nested in generic types too.
        if ((type.Modifiers & (TypeModifiers.Abstract | TypeModifiers.Static)) != 0 || IsGeneric(index, type)) return false;
        var classes = BaseClassChain(index, type).ToArray();
        if (classes.Any(x => Has(x.Attributes, NonControllerAttribute))) return false;
        return classes.Any(x => x.DisplayName == ControllerBase || Has(x.Attributes, ControllerAttribute));
    }

    // Registrations that handle every exception leaving the action: exception middleware or a global MVC exception filter
    // registered in the action's project or in a project that references it (the host). AddExceptionHandler alone handles
    // nothing without UseExceptionHandler. This is registration evidence: middleware order and environment conditions are not checked.
    public static ImmutableArray<BoundaryHandlingFact> GlobalHandlers(ProgramIndex index, CallableId action)
    {
        var compilations = index.Snapshot.Compilations;
        var byId = compilations.ToDictionary(x => x.Id);
        var byPath = compilations.DistinctBy(x => x.ProjectPath).ToDictionary(x => x.ProjectPath, StringComparer.Ordinal);
        if (!byId.TryGetValue(index.Callable(action).Compilation, out var own)) return [];
        bool Runs(CompilationId host)
        {
            if (!byId.TryGetValue(host, out var hosting)) return false;
            var pending = new Stack<string>([hosting.ProjectPath]); var seen = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryPop(out var path))
            {
                if (path == own.ProjectPath) return true;
                if (seen.Add(path) && byPath.TryGetValue(path, out var project)) foreach (var reference in project.ProjectReferences) pending.Push(reference);
            }
            return false;
        }
        return [.. index.Snapshot.BoundaryHandling.Where(x => x.Registration != "AddExceptionHandler" && Runs(x.Compilation))];
    }

    private static bool Has(ImmutableArray<AttributeFact> attributes, string name) => attributes.Any(x => x.Name == name);

    private static bool IsGeneric(ProgramIndex index, TypeFact type)
    {
        for (TypeFact? current = type; current is not null; current = current.ContainingType is { } outer && index.TryGetType(outer, out var fact) ? fact : null)
            if ((current.Modifiers & TypeModifiers.Generic) != 0) return true;
        return false;
    }

    // The type followed by its base classes; cycle-safe for malformed metadata.
    private static IEnumerable<TypeFact> BaseClassChain(ProgramIndex index, TypeFact type)
    {
        var seen = new HashSet<TypeId>();
        for (TypeFact? current = type; current is not null && seen.Add(current.Id);
             current = current.BaseClass is { } baseClass && index.TryGetType(baseClass, out var fact) ? fact : null)
            yield return current;
    }
}
