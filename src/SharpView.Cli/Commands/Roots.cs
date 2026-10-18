using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

/// <summary>Actions: controller actions only. ActionsUnlessExplicit: actions by default; --root may name any source method.</summary>
internal enum RootScope { Actions, ActionsUnlessExplicit }

internal static class Roots
{
    internal static ImmutableArray<CallableId> Select(ProgramIndex index, Arguments arguments, RootScope scope, CancellationToken token)
    {
        var source = index.Snapshot.Callables.Where(x => x.Body != BodyStatus.MetadataOnly);
        var actionsOnly = scope switch
        {
            RootScope.Actions => true,
            RootScope.ActionsUnlessExplicit => arguments.Root is null,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        if (actionsOnly)
        {
            var actions = AspNetConventions.ControllerActions(index, token).Select(x => x.Method).ToHashSet();
            source = source.Where(x => actions.Contains(x.Id));
        }
        return arguments.Root is null
            ? source.Select(x => x.Id).ToImmutableArray()
            : [Match(source, arguments, actionsOnly ? $"{arguments.Command} requires a recognised action" : "source methods only").Id];
    }

    /// <summary>--root names an ID or an exact signature; an exact ID match wins.</summary>
    internal static CallableFact Match(IEnumerable<CallableFact> candidates, Arguments arguments, string requirement)
    {
        var all = candidates.ToArray();
        var byId = all.Where(x => x.Id.Value == arguments.Root).ToArray();
        var matches = byId.Length > 0 ? byId : all.Where(x => x.DisplaySignature == arguments.Root).ToArray();
        return matches switch
        {
            [var single] => single,
            [] => throw new ArgumentException($"No source callable matches --root ({requirement}). Find roots with inspect --methods."),
            _ => throw new ArgumentException("Ambiguous signature; pass one of these IDs as --root:\n" + string.Join("\n", matches.Select(x => x.Id.Value))),
        };
    }
}
