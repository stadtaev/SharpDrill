using SharpView.Core;

namespace SharpView.Cli;

/// <summary>How the CLI names program elements, shared by every command. Command-specific output lives in the command.</summary>
internal sealed class Display(ProgramIndex index)
{
    private readonly HashSet<string> ambiguous = index.Snapshot.Callables.GroupBy(x => x.DisplaySignature)
        .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The method's signature, with its compilation when another method shares the signature.</summary>
    internal string Name(CallableId id)
    {
        if (!index.TryGetCallable(id, out var method)) return id.Value;
        return method.DisplaySignature + (ambiguous.Contains(method.DisplaySignature) ? $" [{method.Compilation.Value}]" : "");
    }

    /// <summary>Type.Method without namespaces or parameters, e.g. <c>OrdersController.Get</c>; constructors read <c>Type.ctor</c>.</summary>
    internal string Short(CallableId id)
    {
        if (!index.TryGetCallable(id, out var method)) return id.Value;
        var signature = method.DisplaySignature;
        var name = signature[..(signature.IndexOf('(') is var open and >= 0 ? open : signature.Length)];
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Length > 2 ? parts[^2..] : parts);
    }

    /// <summary>True for fakes, mocks, stubs, in-memory doubles and test projects, judged by type, namespace and project names.</summary>
    internal bool IsTestDouble(CallableId id)
    {
        var haystack = (index.DeclaringType(id)?.DisplayName ?? "") + "|" + Project(id);
        return Markers.Any(x => haystack.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
    private static readonly string[] Markers = ["Fake", "Mock", "Stub", "InMemory", "Dummy", ".Tests", "UnitTests", "IntegrationTests"];

    internal string Project(CallableId id) => index.ProjectName(index.Callable(id).Compilation);

    internal string TypeOf(CallableId id) => index.DeclaringType(id)?.DisplayName ?? "<no containing type>";

    internal static string Location(SourceEvidence evidence) => $"{evidence.Path}:{evidence.StartLine}:{evidence.StartColumn}";
}
