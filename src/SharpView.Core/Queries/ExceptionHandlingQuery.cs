using System.Collections.Immutable;

namespace SharpView.Core;

// Definite: the catch type matches and no filter applies. TypeUncertain/Filtered: the catch may or may not take it.
public enum CatchCertainty { Definite, TypeUncertain, Filtered }

/// <summary>An origin reaching a matching catch clause; Example is one witness path from a root.</summary>
public sealed record CaughtException(ThrowSiteId Origin, CatchId Catch, CatchCertainty Certainty, ExceptionWitness Example);

/// <summary>An origin passing through a try/catch whose clauses do not match it; Outcomes are its final statuses
/// over every witness path (it may still be intercepted further up).</summary>
public sealed record MiscaughtException(ThrowSiteId Origin, MissedRegion Missed, ImmutableArray<ExceptionStatus> Outcomes, ExceptionWitness Example);

/// <summary>Projections of detailed witnesses (ExceptionQuery) onto handlers, deduplicated across roots and paths.</summary>
public static class ExceptionHandlingQuery
{
    public static ImmutableArray<CaughtException> Caught(ExceptionSearchResult result)
    {
        var seen = new HashSet<(ThrowSiteId, CatchId)>();
        var caught = ImmutableArray.CreateBuilder<CaughtException>();
        foreach (var witness in result.Results)
        {
            if (witness.Origin is not { } origin) continue;
            for (var i = 0; i < witness.HandlerIds.Length; i++)
            {
                var certainty = i == witness.HandlerIds.Length - 1 ? Certainty(witness) : CatchCertainty.Definite;
                if (seen.Add((origin, witness.HandlerIds[i]))) caught.Add(new(origin, witness.HandlerIds[i], certainty, witness));
            }
        }
        return caught.ToImmutable();
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
