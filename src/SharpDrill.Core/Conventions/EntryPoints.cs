using System.Collections.Immutable;

namespace SharpDrill.Core;

// Main covers a static Main and top-level statements (Program.<Main>$). PublicApi is a public method of a library
// project that no loaded project references: its callers are outside the analysed code.
public enum EntryPointKind { ControllerAction, Main, PublicApi }

/// <summary>Where control enters the analysed code from outside it; exception paths and diagrams start here.</summary>
public sealed record EntryPoint(CallableFact Method, EntryPointKind Kind);

public static class ProgramEntryPoints
{
    private const string TopLevel = "<Main>$";

    /// <summary>Controller actions and program entry points in every project; for a library project that no loaded project
    /// references, its public methods. Ordered by signature, then ID.</summary>
    public static ImmutableArray<EntryPoint> EntryPoints(this ProgramIndex program, CancellationToken token = default)
    {
        var actions = AspNetConventions.ControllerActions(program, token).Select(x => x.Method).ToHashSet();
        var found = program.Snapshot.Callables.Where(x => x.Body == BodyStatus.Source)
            .Select(x => actions.Contains(x.Id) ? new EntryPoint(x, EntryPointKind.ControllerAction) : IsMain(x) ? new EntryPoint(x, EntryPointKind.Main) : null)
            .OfType<EntryPoint>().ToList();
        // A referenced library is entered through the code that references it, which is already traced.
        var entered = found.Select(x => x.Method.Compilation).ToHashSet();
        var referenced = program.Snapshot.Compilations.SelectMany(x => x.ProjectReferences).ToHashSet(StringComparer.Ordinal);
        var libraries = program.Snapshot.Compilations.Where(x => !entered.Contains(x.Id) && !referenced.Contains(x.ProjectPath)).Select(x => x.Id).ToHashSet();
        found.AddRange(program.Snapshot.Callables.Where(x => libraries.Contains(x.Compilation) && IsPublicApi(program, x)).Select(x => new EntryPoint(x, EntryPointKind.PublicApi)));
        return [.. found.OrderBy(x => x.Method.DisplaySignature, StringComparer.Ordinal).ThenBy(x => x.Method.Id.Value, StringComparer.Ordinal)];
    }

    /// <summary>The entry point a name stands for: its ID, its exact signature, or a part of one signature (case-insensitive),
    /// such as <c>OrdersController.Get</c>. Throws ArgumentException listing the candidates when none or several match.</summary>
    public static EntryPoint EntryPoint(this ProgramIndex program, string name, CancellationToken token = default)
    {
        var all = program.EntryPoints(token);
        var exact = all.Where(x => x.Method.Id.Value == name).ToArray() is { Length: > 0 } byId ? byId : all.Where(x => x.Method.DisplaySignature == name).ToArray();
        var matches = exact.Length > 0 ? exact : all.Where(x => x.Method.DisplaySignature.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Signatures shared by several entry points (Program.<Main>$ in each app) are told apart by ID, which --from also takes.
        static string List(IReadOnlyCollection<EntryPoint> found)
        {
            var shared = found.GroupBy(x => x.Method.DisplaySignature).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            return string.Join("\n  ", found.Take(10).Select(x => shared.Contains(x.Method.DisplaySignature) ? x.Method.Id.Value : x.Method.DisplaySignature))
                + (found.Count > 10 ? $"\n  ... and {found.Count - 10} more" : "");
        }
        // A mistyped namespace or parameter list still names the method, which leads to the exact text to pass.
        static string Method(string text) => text[..(text.IndexOf('(') is var open and >= 0 ? open : text.Length)].Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        return matches switch
        {
            [var single] => single,
            [] when program.Snapshot.Callables.Any(x => x.Body != BodyStatus.MetadataOnly && (x.Id.Value == name || x.DisplaySignature.Contains(name, StringComparison.OrdinalIgnoreCase))) =>
                throw new ArgumentException($"{name} is not an entry point (a controller action, Main, top-level statements, or a public method of a library nothing references)."),
            [] when all.Where(x => Method(x.Method.DisplaySignature).Equals(Method(name), StringComparison.OrdinalIgnoreCase)).ToArray() is { Length: > 0 } near =>
                throw new ArgumentException($"No entry point matches {name}. Did you mean:\n  {List(near)}"),
            [] => throw new ArgumentException($"No entry point matches {name}." + (all.IsEmpty ? " The code has none." : "")),
            _ when exact.Length > 1 => throw new ArgumentException($"{name} names several entry points; pass one of these IDs:\n  " + string.Join("\n  ", exact.Select(x => x.Method.Id.Value))),
            _ => throw new ArgumentException($"{name} matches {matches.Length} entry points; pass more of the signature:\n  {List(matches)}"),
        };
    }

    // The forms the C# compiler accepts as an entry point: static Main returning void, int or a task of them, taking nothing or string[].
    private static bool IsMain(CallableFact method) => method.Kind == CallableKind.Method && method.DeclaringType is not null
        && (method.DisplaySignature.EndsWith("." + TopLevel + "(string[])", StringComparison.Ordinal)
            || (method.Modifiers & (CallableModifiers.Static | CallableModifiers.Generic)) == CallableModifiers.Static && method.DisplaySignature.Split('(')[0].EndsWith(".Main", StringComparison.Ordinal)
               && method.Parameters is [] or [{ Type.DisplayName: "string[]" }] && method.ReturnType.DisplayName is "void" or "int" or "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<int>");

    // Public and reachable from outside: every containing type public too. Compiler-provided members hold no code.
    private static bool IsPublicApi(ProgramIndex program, CallableFact method)
    {
        if (method.Body != BodyStatus.Source || method.Accessibility != "Public" || method.Kind is CallableKind.LocalFunction or CallableKind.Lambda
            || (method.Modifiers & CallableModifiers.Abstract) != 0 || method.DeclaringType is not { } declaring) return false;
        for (TypeFact? type = program.TryGetType(declaring, out var found) ? found : null; type is not null; type = type.ContainingType is { } outer && program.TryGetType(outer, out var next) ? next : null)
            if (type.Accessibility != "Public") return false;
        return true;
    }
}
