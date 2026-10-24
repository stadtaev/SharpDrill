using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SharpView.Core;

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
// Synthesized: a compiler-provided member of a source type (implicit or record constructor, auto-property accessor,
// record member). It contains no explicit code, so it cannot hold a throw, and it is never "missing".
// New values are appended: the snapshot serializes enums as numbers.
public enum BodyStatus { Source, MetadataOnly, NoBody, Unsupported, Synthesized }
public enum NullabilityKind { NotAnnotated, Annotated, Oblivious }
public enum DependencyKind { Signature, Inheritance, Constraint, Attribute, Construction, BodyUse, Field, Property }
public enum ImplementationKind { Interface, Override }
public enum ThrowKind { Explicit, Rethrow }
// Internal: SharpView failed on code it should handle, a bug to report rather than a limit of the model.
public enum GapKind { ProjectLoad, Compilation, Binding, Unsupported, ExcludedSource, Internal }
// Intercepts: every exit leaves the handler normally. Rethrows: every exit rethrows. MayRethrow: some exits rethrow,
// others leave normally. Replaces: every exit throws something else; that throw is an origin of its own.
public enum HandlerOutcome { Intercepts, Rethrows, Replaces, Unknown, MayRethrow }
public enum DeclaredTypeKind { Class, Record, Struct, RecordStruct, Interface, Enum, Delegate, Other }
[Flags] public enum TypeModifiers { None = 0, Abstract = 1, Sealed = 2, Static = 4, Generic = 8 }
// Operator covers user-defined operators and conversions.
[Flags] public enum CallableModifiers { None = 0, Static = 1, Abstract = 2, Virtual = 4, Override = 8, Sealed = 16, Generic = 32, Extension = 64, Operator = 128, ExplicitImplementation = 256, Finalizer = 512 }

public sealed record SourceEvidence(string Path, string Checksum, int SpanStart, int SpanLength,
    int StartLine, int StartColumn, bool IsGenerated)
{
    // Portable display form; paths outside the root stay as recorded rather than becoming "../" chains.
    public string RelativeTo(string root)
    {
        if (!System.IO.Path.IsPathRooted(root) || !System.IO.Path.IsPathRooted(Path)) return Path;
        var relative = System.IO.Path.GetRelativePath(root, Path);
        var outside = System.IO.Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) && (relative.Length == 2 || relative[2] is '/' or '\\');
        return outside ? Path : relative.Replace('\\', '/');
    }
}
public sealed record NamedArgumentFact(string Name, string Value);
// Name is the attribute class display name; argument values are rendered C# constants.
public sealed record AttributeFact(string Name, ImmutableArray<string> Arguments, ImmutableArray<NamedArgumentFact> NamedArguments);
public sealed record TypeUse(TypeId? Declaration, string DisplayName, NullabilityKind Nullability, ImmutableArray<TypeUse> Components);
public sealed record ParameterFact(string Name, TypeUse Type, string RefKind);
public sealed record TypeFact(TypeId Id, CompilationId Compilation, string DisplayName, string Accessibility,
    ImmutableArray<TypeUse> BaseTypes, ImmutableArray<AttributeFact> Attributes, ImmutableArray<SourceEvidence> Declarations,
    string Namespace = "", DeclaredTypeKind Kind = DeclaredTypeKind.Class, TypeModifiers Modifiers = TypeModifiers.None,
    TypeId? BaseClass = null, TypeId? ContainingType = null, string Key = "");
public sealed record CallableFact(CallableId Id, CompilationId Compilation, TypeId? DeclaringType,
    CallableId? ContainingCallable, CallableKind Kind, string DisplaySignature, string Accessibility, TypeUse ReturnType,
    ImmutableArray<ParameterFact> Parameters, ImmutableArray<AttributeFact> Attributes, BodyStatus Body,
    ImmutableArray<SourceEvidence> Declarations, bool IsAsync = false, bool ReturnsTask = false,
    CallableModifiers Modifiers = CallableModifiers.None, CallableId? Overrides = null, string Key = "")
{
    // True when the callable's code is fully known: written in source, or compiler-provided and free of explicit code.
    [JsonIgnore] public bool HasBody => Body is BodyStatus.Source or BodyStatus.Synthesized;
}
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
    ImmutableArray<CatchId> Catches, bool FinallyMayThrow);
// A candidate: Declared may be implemented/overridden by Implementation. These are not inferred runtime targets;
// consumers opt in to following them and own the resulting uncertainty (receiver type, DI registration, further overrides).
public sealed record ImplementationFact(CallableId Declared, CallableId Implementation, ImplementationKind Kind);
public sealed record ControllerActionFact(CallableId Method, string Convention);
public sealed record BoundaryHandlingFact(CompilationId Compilation, string Registration, string BoundMethod, SourceEvidence Evidence);
// A source span where a SharpView rule (SV0001...) is suppressed: `#pragma warning disable` up to its `restore` (or the
// end of the file), or the declaration carrying `[SuppressMessage]`. A null Rule is a bare `#pragma warning disable`: every rule.
public sealed record SuppressionFact(string? Rule, SourceEvidence Scope)
{
    public bool Covers(string rule, SourceEvidence at) => (Rule is null || Rule == rule) && at.Path == Scope.Path
        && at.SpanStart >= Scope.SpanStart && at.SpanStart < Scope.SpanStart + Scope.SpanLength;
}
public sealed record AnalysisGap(GapKind Kind, CompilationId? Compilation, CallableId? Owner, string Message, SourceEvidence? Evidence);
public sealed record CompilationFact(CompilationId Id, string ProjectPath, string AssemblyIdentity,
    string TargetFramework, string Configuration, string LanguageVersion, string NullableContext,
    ImmutableArray<string> PreprocessorSymbols, ImmutableArray<string> Diagnostics)
{
    // Full paths of <ProjectReference> items as MSBuild evaluated them; a target may be outside the loaded workspace.
    public ImmutableArray<string> ProjectReferences { get; init; } = [];
    // MSBuild properties as evaluated: Nullable, TreatWarningsAsErrors, WarningsAsErrors (split on ';').
    public string NullableSetting { get; init; } = "";
    public bool TreatWarningsAsErrors { get; init; }
    public ImmutableArray<string> WarningsAsErrors { get; init; } = [];
    // Compiler diagnostics after severity options (analyzers and NuGet excluded); promoted warnings count as errors.
    public int WarningCount { get; init; }
    public int ErrorCount { get; init; }
}
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
    public ImmutableArray<ImplementationFact> Implementations { get; init; } = [];
    public ImmutableArray<BoundaryHandlingFact> BoundaryHandling { get; init; } = [];
    public ImmutableArray<SuppressionFact> Suppressions { get; init; } = [];
    // Metadata compilation IDs of referenced assemblies that no .NET framework reference pack supplies: NuGet packages and
    // directly referenced DLLs.
    public ImmutableArray<CompilationId> ThirdPartyAssemblies { get; init; } = [];
    // Full path of the analysed input's directory; consumers use it with SourceEvidence.RelativeTo.
    public string SourceRoot { get; init; } = "";
    // Project files skipped at load because they are test projects (not a gap: excluding them is the default scope).
    public ImmutableArray<string> ExcludedTestProjects { get; init; } = [];
}
