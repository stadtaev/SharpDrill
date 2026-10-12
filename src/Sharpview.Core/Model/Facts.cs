using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Sharpview.Core;

[JsonConverter(typeof(StringIdConverter<CompilationId>))]
public readonly record struct CompilationId(string Value) : IStringId<CompilationId> { static CompilationId IStringId<CompilationId>.Create(string value) => new(value); }
[JsonConverter(typeof(StringIdConverter<TypeId>))]
public readonly record struct TypeId(string Value) : IStringId<TypeId> { static TypeId IStringId<TypeId>.Create(string value) => new(value); }
[JsonConverter(typeof(StringIdConverter<CallableId>))]
public readonly record struct CallableId(string Value) : IStringId<CallableId> { static CallableId IStringId<CallableId>.Create(string value) => new(value); }
[JsonConverter(typeof(StringIdConverter<CallSiteId>))]
public readonly record struct CallSiteId(string Value) : IStringId<CallSiteId> { static CallSiteId IStringId<CallSiteId>.Create(string value) => new(value); }
[JsonConverter(typeof(StringIdConverter<ThrowSiteId>))]
public readonly record struct ThrowSiteId(string Value) : IStringId<ThrowSiteId> { static ThrowSiteId IStringId<ThrowSiteId>.Create(string value) => new(value); }
[JsonConverter(typeof(StringIdConverter<CatchId>))]
public readonly record struct CatchId(string Value) : IStringId<CatchId> { static CatchId IStringId<CatchId>.Create(string value) => new(value); }
[JsonConverter(typeof(StringIdConverter<ExceptionRegionId>))]
public readonly record struct ExceptionRegionId(string Value) : IStringId<ExceptionRegionId> { static ExceptionRegionId IStringId<ExceptionRegionId>.Create(string value) => new(value); }
public enum CallableKind { Method, Constructor, Getter, Setter, EventAdder, EventRemover, LocalFunction, Lambda }
public enum DispatchKind { Direct, Interface, Virtual, Delegate, Dynamic, Unknown }
public enum BodyStatus { Source, MetadataOnly, NoBody, Unsupported }
public enum NullabilityKind { NotAnnotated, Annotated, Oblivious }
public enum DependencyKind { Signature, Inheritance, Constraint, Attribute, Construction, BodyUse }
public enum ThrowKind { Explicit, Rethrow }
public enum GapKind { ProjectLoad, Compilation, Binding, Unsupported, ExcludedSource }
public enum HandlerOutcome { Intercepts, Rethrows, Replaces, Unknown }

public sealed record SourceEvidence(string Path, string Checksum, int SpanStart, int SpanLength,
    int StartLine, int StartColumn, bool IsGenerated);
public sealed record TypeUse(TypeId? Declaration, string DisplayName, NullabilityKind Nullability, ImmutableArray<TypeUse> Components);
public sealed record ParameterFact(string Name, TypeUse Type, string RefKind);
public sealed record TypeFact(TypeId Id, CompilationId Compilation, string DisplayName, string Accessibility,
    ImmutableArray<TypeUse> BaseTypes, ImmutableArray<string> Attributes, ImmutableArray<SourceEvidence> Declarations);
public sealed record CallableFact(CallableId Id, CompilationId Compilation, TypeId? DeclaringType,
    CallableId? ContainingCallable, CallableKind Kind, string DisplaySignature, string Accessibility, TypeUse ReturnType,
    ImmutableArray<ParameterFact> Parameters, ImmutableArray<string> Attributes, BodyStatus Body,
    ImmutableArray<SourceEvidence> Declarations, bool IsAsync = false, bool ReturnsTask = false);
public sealed record CallSiteFact(CallSiteId Id, CallableId Caller, CallableId? BoundTarget, string TargetDisplay,
    DispatchKind Dispatch, ImmutableArray<TypeUse> TypeArguments, SourceEvidence Evidence, bool IsImplicit,
    ImmutableArray<ExceptionRegionId> ProtectedRegions, bool IsAwaited = false);
public sealed record DependencyFact(TypeId? OwningType, CallableId? OwningCallable, TypeUse ReferencedType,
    DependencyKind Kind, SourceEvidence? Evidence);
public sealed record ThrowSiteFact(ThrowSiteId Id, CallableId Owner, ThrowKind Kind, TypeUse? StaticExceptionType,
    SourceEvidence Evidence, ImmutableArray<ExceptionRegionId> ProtectedRegions, bool IsExactType = false, CatchId? CatchId = null, TypeUse? ExactExceptionType = null);
public sealed record CatchFact(CatchId Id, CallableId Owner, TypeUse? CaughtType, string? Filter,
    SourceEvidence Evidence, SourceEvidence BodyEvidence, HandlerOutcome Outcome, ThrowSiteId? SubsequentThrow);
public sealed record ExceptionRegionFact(ExceptionRegionId Id, CallableId Owner, SourceEvidence ProtectedBlock,
    ImmutableArray<CatchId> Catches, bool HasFinally);
public sealed record ControllerActionFact(CallableId Method, string Convention);
public sealed record BoundaryHandlingFact(CompilationId Compilation, string Registration, string BoundMethod, SourceEvidence Evidence);
public sealed record AnalysisGap(GapKind Kind, CompilationId? Compilation, CallableId? Owner, string Message, SourceEvidence? Evidence);
public sealed record CompilationFact(CompilationId Id, string ProjectPath, string AssemblyIdentity,
    string TargetFramework, string Configuration, string LanguageVersion, string NullableContext,
    ImmutableArray<string> PreprocessorSymbols, ImmutableArray<string> Diagnostics);
// Init-only with empty defaults so new fact families do not break existing construction sites.
public sealed record ProgramSnapshot
{
    public ImmutableArray<CompilationFact> Compilations { get; init; } = [];
    public ImmutableArray<TypeFact> Types { get; init; } = [];
    public ImmutableArray<CallableFact> Callables { get; init; } = [];
    public ImmutableArray<CallSiteFact> CallSites { get; init; } = [];
    public ImmutableArray<DependencyFact> Dependencies { get; init; } = [];
    public ImmutableArray<ThrowSiteFact> ThrowSites { get; init; } = [];
    public ImmutableArray<AnalysisGap> Gaps { get; init; } = [];
    public ImmutableArray<CatchFact> Catches { get; init; } = [];
    public ImmutableArray<ExceptionRegionFact> ExceptionRegions { get; init; } = [];
    public ImmutableArray<ControllerActionFact> ControllerActions { get; init; } = [];
    public ImmutableArray<BoundaryHandlingFact> BoundaryHandling { get; init; } = [];
}
