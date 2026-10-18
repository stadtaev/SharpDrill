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

    internal string Project(CallableId id) => index.ProjectName(index.Callable(id).Compilation);

    internal string TypeOf(CallableId id) => index.DeclaringType(id)?.DisplayName ?? "<no containing type>";

    internal static string Location(SourceEvidence evidence) => $"{evidence.Path}:{evidence.StartLine}:{evidence.StartColumn}";
}
