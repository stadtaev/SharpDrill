using SharpDrill.Core;

namespace SharpDrill.Cli;

/// <summary>How the CLI names program elements, shared by every command. Command-specific output lives in the command.</summary>
internal sealed class Display(ProgramIndex index, string? input = null)
{
    private readonly HashSet<string> ambiguous = index.Snapshot.Callables.GroupBy(x => x.DisplaySignature)
        .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
    private readonly string root = input is null ? "" : Path.GetDirectoryName(Path.GetFullPath(input)) ?? "";

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

    /// <summary>File and line relative to the analysed solution or project, so lines stay short.</summary>
    internal string Where(SourceEvidence evidence)
    {
        var relative = root.Length == 0 ? evidence.Path : Path.GetRelativePath(root, evidence.Path);
        return $"{(relative.StartsWith("..", StringComparison.Ordinal) ? evidence.Path : relative)}:{evidence.StartLine}";
    }

    internal static string Plural(int count, string one, string? many = null) => $"{count} {(count == 1 ? one : many ?? one + "s")}";
}
