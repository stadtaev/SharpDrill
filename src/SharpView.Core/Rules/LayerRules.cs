using System.Collections.Immutable;

namespace SharpView.Core;

public static class LayerRules
{
    public const string AllowedDependenciesRuleId = "layer-dependency";

    // Evaluates cross-layer edges against the model's allowed dependencies. Passing edges produce no findings.
    // Candidate calls are excluded: the source depends on the declared binding, and a candidate is not a runtime fact.
    // With no allowed dependencies configured the rule is not evaluated.
    public static ImmutableArray<Finding> AllowedDependencies(LayerGraph graph, LayerModel model)
    {
        if (model.Allowed.IsEmpty) return [];
        var findings = ImmutableArray.CreateBuilder<Finding>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == LayerEdgeKind.CandidateCall || edge.From == edge.To) continue;
            var subject = $"{edge.From} --> {edge.To} ({edge.Kind})";
            var evidence = edge.Evidence is null ? ImmutableArray<SourceEvidence>.Empty : [edge.Evidence];
            if (edge.From == LayerMap.Unmapped || edge.To == LayerMap.Unmapped)
                findings.Add(new(AllowedDependenciesRuleId, Verdict.Unknown, subject,
                    $"Source without a layer mapping ({edge.Count} supporting facts), e.g. {edge.WitnessSource} --> {edge.WitnessTarget}.", evidence));
            else if (!model.Allows(edge.From, edge.To))
                findings.Add(new(AllowedDependenciesRuleId, Verdict.Violation, subject,
                    $"Dependency not allowed ({edge.Count} supporting facts), e.g. {edge.WitnessSource} --> {edge.WitnessTarget}.", evidence));
        }
        return findings.ToImmutable();
    }
}
