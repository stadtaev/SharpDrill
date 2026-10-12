using System.Collections.Immutable;

namespace Sharpview.Core;

public readonly record struct CompilationId(string Value);
public readonly record struct TypeId(string Value);
public readonly record struct CallableId(string Value);
public readonly record struct CallSiteId(string Value);
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
    ImmutableArray<string> ProtectedRegions, bool IsAwaited = false);
public sealed record DependencyFact(TypeId? OwningType, CallableId? OwningCallable, TypeUse ReferencedType,
    DependencyKind Kind, SourceEvidence? Evidence);
public sealed record ThrowSiteFact(string Id, CallableId Owner, ThrowKind Kind, TypeUse? StaticExceptionType,
    SourceEvidence Evidence, ImmutableArray<string> ProtectedRegions, bool IsExactType = false, string? CatchId = null, TypeUse? ExactExceptionType = null);
public sealed record CatchFact(string Id, CallableId Owner, TypeUse? CaughtType, string? Filter,
    SourceEvidence Evidence, SourceEvidence BodyEvidence, HandlerOutcome Outcome, string? SubsequentThrow);
public sealed record ExceptionRegionFact(string Id, CallableId Owner, SourceEvidence ProtectedBlock,
    ImmutableArray<string> Catches, bool HasFinally);
public sealed record ControllerActionFact(CallableId Method, string Convention);
public sealed record BoundaryHandlingFact(CompilationId Compilation, string Registration, string BoundMethod, SourceEvidence Evidence);
public sealed record AnalysisGap(GapKind Kind, CompilationId? Compilation, CallableId? Owner, string Message, SourceEvidence? Evidence);
public sealed record CompilationFact(CompilationId Id, string ProjectPath, string AssemblyIdentity,
    string TargetFramework, string Configuration, string LanguageVersion, string NullableContext,
    ImmutableArray<string> PreprocessorSymbols, ImmutableArray<string> Diagnostics);
public sealed record ProgramSnapshot(ImmutableArray<CompilationFact> Compilations, ImmutableArray<TypeFact> Types,
    ImmutableArray<CallableFact> Callables, ImmutableArray<CallSiteFact> CallSites, ImmutableArray<DependencyFact> Dependencies,
    ImmutableArray<ThrowSiteFact> ThrowSites, ImmutableArray<AnalysisGap> Gaps,
    ImmutableArray<CatchFact> Catches, ImmutableArray<ExceptionRegionFact> ExceptionRegions,
    ImmutableArray<ControllerActionFact> ControllerActions, ImmutableArray<BoundaryHandlingFact> BoundaryHandling);
