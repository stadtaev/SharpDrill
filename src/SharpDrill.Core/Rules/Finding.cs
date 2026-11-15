using System.Collections.Immutable;

namespace SharpDrill.Core;

// Unknown means the rule could not decide from the available facts; it is never folded into Pass.
internal enum Verdict { Violation, Pass, Unknown }

// Common result shape for rules, which are plain functions over the index and derived models.
internal sealed record Finding(string RuleId, Verdict Verdict, string Subject, string Message, ImmutableArray<SourceEvidence> Evidence);
