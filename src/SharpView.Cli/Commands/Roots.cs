using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

internal static class Roots
{
    /// <summary>Every controller action, or the one --root names.</summary>
    internal static ImmutableArray<CallableId> Select(ProgramIndex index, Arguments arguments, CancellationToken token)
    {
        var actions = AspNetConventions.ControllerActions(index, token).Select(x => x.Method).ToHashSet();
        var source = index.Snapshot.Callables.Where(x => actions.Contains(x.Id)).ToArray();
        return arguments.Root is not { } root ? [.. source.Select(x => x.Id)] : [Match(index, source, root).Id];
    }

    /// <summary>--root names an action's ID or exact signature; an exact ID match wins.</summary>
    internal static CallableFact Match(ProgramIndex index, IReadOnlyCollection<CallableFact> actions, string root)
    {
        var byId = actions.Where(x => x.Id.Value == root).ToArray();
        var matches = byId.Length > 0 ? byId : actions.Where(x => x.DisplaySignature == root).ToArray();
        return matches switch
        {
            [var single] => single,
            [] => throw new ArgumentException(NoMatch(index, actions, root)),
            _ => throw new ArgumentException("Ambiguous signature; pass one of these IDs as --root:\n" + string.Join("\n", matches.Select(x => x.Id.Value))),
        };
    }

    // Near misses (the text inside a signature or ID, else the same method name), so a partial or mistyped root leads
    // straight to the exact text to pass.
    private static string NoMatch(ProgramIndex index, IReadOnlyCollection<CallableFact> actions, string root)
    {
        if (index.Snapshot.Callables.Any(x => x.Body != BodyStatus.MetadataOnly && (x.Id.Value == root || x.DisplaySignature == root)))
            return $"--root {root} is not a recognised controller action; exceptions and diagram start from actions.";
        static string Name(string text) => text[..(text.IndexOf('(') is var open and >= 0 ? open : text.Length)].Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        var containing = actions.Where(x => x.DisplaySignature.Contains(root, StringComparison.OrdinalIgnoreCase) || x.Id.Value.Contains(root, StringComparison.OrdinalIgnoreCase)).ToArray();
        var near = (containing.Length > 0 ? containing : actions.Where(x => Name(x.DisplaySignature).Equals(Name(root), StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.DisplaySignature).Distinct().Order(StringComparer.Ordinal).ToArray();
        return near.Length == 0
            ? $"No controller action matches --root {root}. Pass an action's full signature with namespace and parameter types, e.g. Api.OrdersController.Get(int); diagram without --root lists every action."
            : $"No controller action matches --root {root} exactly. Did you mean:\n  {string.Join("\n  ", near.Take(10))}{(near.Length > 10 ? $"\n  ... and {near.Length - 10} more" : "")}";
    }
}
