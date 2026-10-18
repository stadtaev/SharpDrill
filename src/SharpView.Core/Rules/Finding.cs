using System.Collections.Immutable;

namespace SharpView.Core;

// Unknown means the rule could not decide from the available facts; it is never folded into Pass.
public enum Verdict { Violation, Pass, Unknown }

// Common result shape for rules, which are plain functions over the index and derived models.
public sealed record Finding(string RuleId, Verdict Verdict, string Subject, string Message, ImmutableArray<SourceEvidence> Evidence);
