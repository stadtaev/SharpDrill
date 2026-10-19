using System.Collections.Immutable;

namespace SharpView.Core;

// Definite: the catch type matches and no filter applies. TypeUncertain/Filtered: the catch may or may not take it.
public enum CatchCertainty { Definite, TypeUncertain, Filtered }

/// <summary>An origin reaching a matching catch clause; Example is one witness path from a root.</summary>
public sealed record CaughtException(ThrowSiteId Origin, CatchId Catch, CatchCertainty Certainty, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
}

/// <summary>An origin passing through a try/catch whose clauses do not match it; Outcomes are its final statuses
/// over every witness path (it may still be intercepted further up).</summary>
public sealed record MiscaughtException(ThrowSiteId Origin, MissedRegion Missed, ImmutableArray<ExceptionStatus> Outcomes, ExceptionWitness Example)
{
    public bool ViaCandidate => Example.ViaCandidate;
}

/// <summary>Projections of detailed witnesses (ExceptionQuery) onto handlers, deduplicated across roots and paths.
/// When the same origin and handler are reached both by a proven path and through an implementation candidate,
/// the proven path is the example.</summary>
public static class ExceptionHandlingQuery
{
    public static ImmutableArray<CaughtException> Caught(ExceptionSearchResult result)
    {
        var found = new Dictionary<(ThrowSiteId, CatchId), CaughtException>();
        var order = new List<(ThrowSiteId, CatchId)>();
        foreach (var witness in result.Results)
        {
            if (witness.Origin is not { } origin) continue;
            for (var i = 0; i < witness.HandlerIds.Length; i++)
            {
                var certainty = i == witness.HandlerIds.Length - 1 ? Certainty(witness) : CatchCertainty.Definite;
                var key = (origin, witness.HandlerIds[i]);
                if (!found.TryGetValue(key, out var existing)) { found[key] = new(origin, key.Item2, certainty, witness); order.Add(key); }
                else if (existing.ViaCandidate && !witness.ViaCandidate) found[key] = existing with { Certainty = certainty, Example = witness };
            }
        }
        return [.. order.Select(key => found[key])];
    }

    public static ImmutableArray<MiscaughtException> Miscaught(ExceptionSearchResult result)
    {
        var groups = new Dictionary<(ThrowSiteId, ExceptionRegionId), (MissedRegion Missed, SortedSet<ExceptionStatus> Outcomes, ExceptionWitness Example)>();
        var order = new List<(ThrowSiteId, ExceptionRegionId)>();
        foreach (var witness in result.Results)
        {
            if (witness.Origin is not { } origin) continue;
            foreach (var missed in witness.MissedRegions)
            {
                var key = (origin, missed.Region);
                if (!groups.TryGetValue(key, out var group)) { groups[key] = group = (missed, [], witness); order.Add(key); }
                else if (group.Example.ViaCandidate && !witness.ViaCandidate) groups[key] = group = (group.Missed, group.Outcomes, witness);
                group.Outcomes.Add(witness.SubsequentStatus ?? witness.Status);
            }
        }
        return [.. order.Select(key => groups[key]).Select(x => new MiscaughtException(x.Example.Origin!.Value, x.Missed, [.. x.Outcomes], x.Example))];
    }

    // Only the last recorded handler can be the one whose matching ended the analysis as Unknown.
    private static CatchCertainty Certainty(ExceptionWitness witness) => witness.ReasonCode switch
    {
        ExceptionReason.HandlerTypeUncertain => CatchCertainty.TypeUncertain,
        ExceptionReason.CatchFilter => CatchCertainty.Filtered,
        _ => CatchCertainty.Definite,
    };
}
