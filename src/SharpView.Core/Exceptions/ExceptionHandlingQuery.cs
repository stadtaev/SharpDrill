using System.Collections.Immutable;

namespace SharpView.Core;

// Definite: the catch type matches and no filter applies. Possible: a filter or the runtime type decides.
public enum CatchCertainty { Definite, Possible }

/// <summary>An origin reaching a matching catch clause; Example is one witness path from a root.</summary>
public sealed record CaughtException(ThrowSiteId Origin, CatchId Catch, CatchCertainty Certainty, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
}

/// <summary>An origin passing through a try/catch whose clauses do not match it; Outcomes are its final statuses
/// over every witness path (it may still be intercepted further up). Undetermined is a witness whose outcome is Unknown.
/// EscapesFrom are the roots of the witnesses where it may escape.</summary>
public sealed record MiscaughtException(ThrowSiteId Origin, MissedRegion Missed, ImmutableArray<ExceptionStatus> Outcomes, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
    public ExceptionWitness? Undetermined { get; init; }
    public ImmutableArray<CallableId> EscapesFrom { get; init; } = [];
}

/// <summary>Projections of detailed witnesses (ExceptionQuery) onto handlers, deduplicated across roots and paths.
/// The example is the shortest proven path, else the shortest through an implementation candidate.</summary>
public static class ExceptionHandlingQuery
{
    private static bool Better(ExceptionWitness candidate, ExceptionWitness current) =>
        candidate.ViaCandidate != current.ViaCandidate ? !candidate.ViaCandidate : candidate.Path.Length < current.Path.Length;

    public static ImmutableArray<CaughtException> Caught(ExceptionSearchResult result)
    {
        var found = new Dictionary<(ThrowSiteId, CatchId), CaughtException>();
        var order = new List<(ThrowSiteId, CatchId)>();
        foreach (var witness in result.Results)
        {
            if (witness.Origin is not { } origin) continue;
            for (var i = 0; i < witness.HandlerIds.Length; i++)
            {
                var certainty = witness.PossibleHandlers.Contains(witness.HandlerIds[i]) ? CatchCertainty.Possible : CatchCertainty.Definite;
                var key = (origin, witness.HandlerIds[i]);
                if (!found.TryGetValue(key, out var existing)) { found[key] = new(origin, key.Item2, certainty, witness); order.Add(key); }
                else if (Better(witness, existing.Example)) found[key] = existing with { Certainty = certainty, Example = witness };
            }
        }
        return [.. order.Select(key => found[key])];
    }

    public static ImmutableArray<MiscaughtException> Miscaught(ExceptionSearchResult result)
    {
        var groups = new Dictionary<(ThrowSiteId, ExceptionRegionId), (MissedRegion Missed, SortedSet<ExceptionStatus> Outcomes, ExceptionWitness Example)>();
        var undetermined = new Dictionary<(ThrowSiteId, ExceptionRegionId), ExceptionWitness>();
        var escapes = new Dictionary<(ThrowSiteId, ExceptionRegionId), SortedSet<string>>();
        var order = new List<(ThrowSiteId, ExceptionRegionId)>();
        foreach (var witness in result.Results)
        {
            if (witness.Origin is not { } origin) continue;
            foreach (var missed in witness.MissedRegions)
            {
                var key = (origin, missed.Region);
                if (!groups.TryGetValue(key, out var group)) { groups[key] = group = (missed, [], witness); order.Add(key); }
                else if (Better(witness, group.Example)) groups[key] = group = (group.Missed, group.Outcomes, witness);
                group.Outcomes.Add(witness.SubsequentStatus ?? witness.Status);
                if ((witness.SubsequentStatus ?? witness.Status) == ExceptionStatus.Unknown) undetermined.TryAdd(key, witness);
                if ((witness.SubsequentStatus ?? witness.Status) == ExceptionStatus.MayEscape)
                    (escapes.TryGetValue(key, out var roots) ? roots : escapes[key] = new(StringComparer.Ordinal)).Add(witness.Action.Value);
            }
        }
        return [.. order.Select(key => (Key: key, Group: groups[key])).Select(x => new MiscaughtException(x.Group.Example.Origin!.Value, x.Group.Missed, [.. x.Group.Outcomes], x.Group.Example)
            { Undetermined = undetermined.GetValueOrDefault(x.Key), EscapesFrom = [.. escapes.GetValueOrDefault(x.Key, []).Select(r => new CallableId(r))] })];
    }
}
