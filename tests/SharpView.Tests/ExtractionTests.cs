using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SharpView.Core;
using SharpView.Roslyn;
using Xunit;

namespace SharpView.Tests;

public sealed class ExtractionTests
{
    private static async Task<ProgramSnapshot> Extract(string source, bool includeGenerated = true, string path = "/fixture/Input.cs", string projectPath = "/fixture/Fixture.csproj")
    {
        using var workspace = new AdhocWorkspace();
        var (project, compilation) = await Compile(workspace, "ExtractionFixture", source, path);
        var fact = new CompilationFact(new("fixture"), projectPath, compilation.Assembly.Identity.ToString(), "net10.0", "Debug", "Preview", "Disable", [], []);
        var extractor = new FactExtractor([fact], []);
        await extractor.RegisterAsync(project, compilation, fact, includeGenerated, CancellationToken.None);
        extractor.Extract(CancellationToken.None);
        return extractor.Snapshot();
    }
    private static async Task<(Project Project, Compilation Compilation)> Compile(AdhocWorkspace workspace, string name, string source, string path)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x));
        var project = workspace.AddProject(name, LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .AddMetadataReferences(references)
            .AddDocument("Input.cs", SourceText.From(source), filePath: path).Project;
        var compilation = (await project.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), x => x.Severity == DiagnosticSeverity.Error);
        return (project, compilation);
    }

    [Fact]
    public async Task Initializers_lock_and_library_base_constructors_are_not_gaps()
    {
        var snapshot = await Extract("""
            using System.Collections.Generic; using System.Net.Http;
            public class Plain : DelegatingHandler {
                public int A { get; set; } = 5; public List<int> L { get; } = new(); public string S = string.Empty;
                public Plain() { }
                object gate = new();
                public void Locked() { lock (gate) { } }
            }
            public class Active { static string Build() => "x"; public string T = Build(); public string U = (string?)null ?? throw new System.Exception(); }
            public class Base { public Base() { } } public class Derived : Base { public Derived() { } }
            public static class Iter { public static IEnumerable<int> It() { yield return 1; yield return 2; } }
            """);
        Assert.DoesNotContain(snapshot.Gaps, x => x.Message.StartsWith("Field/property initializer", StringComparison.Ordinal));
        // Initializers that run source code or throw belong to the constructor they run in, which then has a body.
        var active = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Active..ctor()");
        Assert.Equal(BodyStatus.Source, active.Body);
        Assert.Contains(snapshot.CallSites, x => x.Caller == active.Id && x.TargetDisplay == "Active.Build()");
        Assert.Contains(snapshot.ThrowSites, x => x.Owner == active.Id);
        Assert.DoesNotContain(snapshot.Gaps, x => x.Message.Contains("Implicit base constructor", StringComparison.Ordinal) || x.Message.Contains("lock", StringComparison.Ordinal));
        Assert.Single(snapshot.Gaps, x => x.Message.StartsWith("Iterator bodies", StringComparison.Ordinal));
        var derived = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Derived..ctor()");
        Assert.Contains(snapshot.CallSites, x => x.Caller == derived.Id && x.TargetDisplay == "Base..ctor()" && x.IsImplicit);
    }

    [Fact]
    public async Task Expression_bodied_property_and_base_accessor_have_source_and_direct_dispatch()
    {
        var snapshot = await Extract("""
            public class Base { public virtual int Value => Leaf(); public static int Leaf() => 42; }
            public class Derived : Base { public int Read() => base.Value; }
            """);
        var getter = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Base.get_Value()");
        Assert.Equal(BodyStatus.Source, getter.Body);
        var leaf = Assert.Single(snapshot.CallSites, x => x.Caller == getter.Id);
        Assert.Equal("Base.Leaf()", leaf.TargetDisplay);
        var read = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Derived.Read()");
        var access = Assert.Single(snapshot.CallSites, x => x.Caller == read.Id);
        Assert.Equal(getter.Id, access.BoundTarget);
        Assert.Equal(DispatchKind.Direct, access.Dispatch);
    }

    [Fact]
    public async Task Expression_bodies_wrapped_in_parentheses_or_suppression_are_walked()
    {
        var snapshot = await Extract("""
            public class Sample {
                public bool Wrapped => (Leaf() == 0 || Leaf() == 1);
                public string Suppressed => Name()!;
                public int this[int i] => (Leaf());
                public int Local() { int Inner() => (Leaf()); return Inner(); }
                public static int Leaf() => 42;
                public static string? Name() => null;
            }
            """);
        Assert.DoesNotContain(snapshot.Gaps, x => x.Message.Contains("no operation tree", StringComparison.Ordinal));
        foreach (var (owner, target, count) in new[] { ("Sample.get_Wrapped()", "Sample.Leaf()", 2), ("Sample.get_Suppressed()", "Sample.Name()", 1), ("Sample.get_Item(int)", "Sample.Leaf()", 1) })
        {
            var getter = Assert.Single(snapshot.Callables, x => x.DisplaySignature == owner);
            Assert.Equal(count, snapshot.CallSites.Count(x => x.Caller == getter.Id && x.TargetDisplay == target));
        }
        var local = Assert.Single(snapshot.Callables, x => x.Kind == CallableKind.LocalFunction);
        Assert.Single(snapshot.CallSites, x => x.Caller == local.Id && x.TargetDisplay == "Sample.Leaf()");
    }

    [Fact]
    public async Task Nested_callables_own_calls_without_invented_parent_edges()
    {
        var snapshot = await Extract("""
            using System;
            public static class Sample {
                public static void Parent() { void Local() { Leaf(); } Action lambda = () => Leaf(); Local(); }
                public static void Leaf() { }
            }
            """);
        var parent = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Parent()");
        var local = Assert.Single(snapshot.Callables, x => x.Kind == CallableKind.LocalFunction);
        var lambda = Assert.Single(snapshot.Callables, x => x.Kind == CallableKind.Lambda);
        Assert.Equal(parent.Id, local.ContainingCallable);
        Assert.Equal(parent.Id, lambda.ContainingCallable);
        Assert.Equal(local.Id, Assert.Single(snapshot.CallSites, x => x.Caller == parent.Id).BoundTarget);
        Assert.Equal("Sample.Leaf()", Assert.Single(snapshot.CallSites, x => x.Caller == local.Id).TargetDisplay);
        Assert.Equal("Sample.Leaf()", Assert.Single(snapshot.CallSites, x => x.Caller == lambda.Id).TargetDisplay);
    }

    [Fact]
    public async Task Protected_regions_filters_and_throw_runtime_precision_are_preserved()
    {
        var snapshot = await Extract("""
            using System;
            public static class Sample {
                public static void Run(Exception error, bool enabled) {
                    Quiet();
                    try { Quiet(); throw error; }
                    catch (ArgumentException) when (enabled) { throw new InvalidOperationException(); }
                    catch { throw; }
                }
                public static void Quiet() { }
            }
            """);
        var run = Assert.Single(snapshot.Callables, x => x.DisplaySignature.StartsWith("Sample.Run(", StringComparison.Ordinal));
        var calls = snapshot.CallSites.Where(x => x.Caller == run.Id && x.TargetDisplay == "Sample.Quiet()").OrderBy(x => x.Evidence.SpanStart).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Empty(calls[0].ProtectedRegions);
        var region = Assert.Single(snapshot.ExceptionRegions);
        Assert.Equal(region.Id, Assert.Single(calls[1].ProtectedRegions));
        var origin = Assert.Single(snapshot.ThrowSites, x => x.CatchId is null);
        Assert.False(origin.IsExactType);
        Assert.Equal("System.Exception", origin.StaticExceptionType?.DisplayName);
        Assert.Equal(region.Id, Assert.Single(origin.ProtectedRegions));
        var filtered = Assert.Single(snapshot.Catches, x => x.Filter is not null);
        Assert.Equal("enabled", filtered.Filter);
        Assert.Equal(HandlerOutcome.Replaces, filtered.Outcome);
        Assert.True(Assert.Single(snapshot.ThrowSites, x => x.Id == filtered.SubsequentThrow).IsExactType);
        Assert.Equal(ThrowKind.Rethrow, Assert.Single(snapshot.ThrowSites, x => x.Kind == ThrowKind.Rethrow).Kind);
        Assert.All(snapshot.ThrowSites, x => { Assert.Equal(64, x.Evidence.Checksum.Length); Assert.True(x.Evidence.StartLine > 0); });
    }

    [Fact]
    public async Task Cast_throw_retains_static_type_and_exact_construction_separately()
    {
        var snapshot = await Extract("""
            using System;
            public sealed class StorageException : Exception { }
            public static class Sample {
                public static void Cast() { throw (Exception)new StorageException(); }
                public static void Null() { throw null; }
            }
            """);
        var cast = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Cast()");
        var origin = Assert.Single(snapshot.ThrowSites, x => x.Owner == cast.Id);
        Assert.Equal("System.Exception", origin.StaticExceptionType?.DisplayName);
        Assert.True(origin.IsExactType);
        Assert.Equal("StorageException", origin.ExactExceptionType?.DisplayName);
        var nullMethod = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Null()");
        Assert.False(Assert.Single(snapshot.ThrowSites, x => x.Owner == nullMethod.Id).IsExactType);
        Assert.Contains(snapshot.Gaps, x => x.Owner == nullMethod.Id && x.Message.Contains("implicit NullReferenceException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Awaiting_a_task_directly_or_through_a_local_observes_it()
    {
        var snapshot = await Extract("""
            using System.Threading.Tasks;
            public static class Sample {
                public static Task Produce() => Task.CompletedTask;
                public static async Task Consume() { await Produce(); var task = Produce(); await task; var a = Produce(); var b = Produce(); await Task.WhenAll(a, b); var lost = Produce(); }
                static Task held = Produce();
                public static async Task Field() => await held;
            }
            """);
        var consume = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Consume()");
        var calls = snapshot.CallSites.Where(x => x.Caller == consume.Id && x.TargetDisplay == "Sample.Produce()").OrderBy(x => x.Evidence.SpanStart).ToArray();
        Assert.Equal([true, true, true, true, false], calls.Select(x => x.IsAwaited));
        Assert.DoesNotContain(snapshot.Gaps, x => x.Owner == consume.Id);
        var field = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Field()");
        Assert.Contains(snapshot.Gaps, x => x.Owner == field.Id && x.Message.Contains("field, property or parameter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generated_exclusion_has_explicit_evidence()
    {
        const string source = "public static class Generated { public static void Run() { } }";
        var included = await Extract(source, path: "/fixture/Input.g.cs");
        Assert.True(Assert.Single(included.Callables).Declarations[0].IsGenerated);
        var excluded = await Extract(source, false, "/fixture/Input.g.cs");
        Assert.Empty(excluded.Callables);
        Assert.Equal(GapKind.ExcludedSource, Assert.Single(excluded.Gaps).Kind);
        Assert.Equal("/fixture/Input.g.cs", excluded.Gaps[0].Evidence?.Path);
    }

    [Fact]
    public async Task Operators_conversions_and_type_only_declarations_are_not_lost()
    {
        var snapshot = await Extract("""
            public class Empty { }
            public readonly struct Value {
                public static Value operator +(Value left, Value right) => left;
                public static implicit operator int(Value value) => 0;
            }
            public static class Sample { public static int Run(Value value) { Value sum = value + value; return sum; } }
            """);
        Assert.Contains(snapshot.Types, x => x.DisplayName == "Empty" && x.Declarations.Length == 1);
        var owner = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Run(Value)");
        var calls = snapshot.CallSites.Where(x => x.Caller == owner.Id).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, x => Assert.Equal(DispatchKind.Direct, x.Dispatch));
        Assert.Contains(calls, x => x.TargetDisplay == "Value.op_Addition(Value, Value)");
        Assert.Contains(calls, x => x.TargetDisplay == "Value.op_Implicit(Value)");
        Assert.All(calls, x => Assert.Equal(BodyStatus.Source, Assert.Single(snapshot.Callables, m => m.Id == x.BoundTarget).Body));
    }

    [Fact]
    public async Task Partial_methods_reduced_extensions_and_nested_generic_dependencies_keep_identity()
    {
        var snapshot = await Extract("""
            using System.Collections.Generic;
            public static partial class Sample {
                public static partial int Run(List<List<string>> values);
                public static partial int Run(List<List<string>> values) => values.Size();
            }
            public static class Extensions { public static int Size<T>(this List<T> values) => values.Count; }
            """);
        var run = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Run(System.Collections.Generic.List<System.Collections.Generic.List<string>>)");
        Assert.Equal(2, run.Declarations.Length);
        Assert.Equal(BodyStatus.Source, run.Body);
        var call = Assert.Single(snapshot.CallSites, x => x.Caller == run.Id);
        var target = Assert.Single(snapshot.Callables, x => x.Id == call.BoundTarget);
        Assert.Equal("Extensions.Size<T>(System.Collections.Generic.List<T>)", target.DisplaySignature);
        Assert.Equal(BodyStatus.Source, target.Body);
        Assert.Equal(DispatchKind.Direct, call.Dispatch);
        Assert.Equal("System.Collections.Generic.List<string>", Assert.Single(call.TypeArguments).DisplayName);
        Assert.Contains(snapshot.Dependencies, x => x.OwningCallable == run.Id && x.Kind == DependencyKind.Signature && x.ReferencedType.DisplayName == "string");
    }

    [Fact]
    public async Task Referenced_assemblies_outside_the_shared_framework_are_third_party()
    {
        // The test host references the shared runtime (shared/Microsoft.NETCore.App) and packages copied to its output.
        var snapshot = await Extract("public static class Use { public static void Run() { System.Console.WriteLine(); Xunit.Assert.True(true); } }");
        Assert.Contains(snapshot.ThirdPartyAssemblies, x => x.Value.StartsWith("metadata|xunit.assert,", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.ThirdPartyAssemblies, x => x.Value.StartsWith("metadata|System.Console,", StringComparison.Ordinal) || x.Value.StartsWith("metadata|System.Runtime,", StringComparison.Ordinal));
        var call = Assert.Single(snapshot.CallSites, x => x.TargetDisplay.StartsWith("Xunit.Assert.True", StringComparison.Ordinal));
        Assert.Contains(snapshot.Callables.Single(x => x.Id == call.BoundTarget).Compilation, snapshot.ThirdPartyAssemblies);
    }
    [Fact]
    public async Task Global_mvc_exception_filters_are_boundary_handling_and_other_filters_are_not()
    {
        var snapshot = await Extract("""
            using System; using System.Threading.Tasks;
            namespace Microsoft.AspNetCore.Mvc.Filters {
                public interface IFilterMetadata { }
                public interface IExceptionFilter : IFilterMetadata { } public interface IAsyncExceptionFilter : IFilterMetadata { }
                public interface IActionFilter : IFilterMetadata { }
                public class FilterCollection {
                    public void Add<T>() where T : IFilterMetadata { } public void Add(Type type) { } public void Add(IFilterMetadata filter) { }
                    public void AddService<T>() where T : IFilterMetadata { }
                }
            }
            public class Problems : Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter { }
            public class AsyncProblems : Microsoft.AspNetCore.Mvc.Filters.IAsyncExceptionFilter { }
            public class Timing : Microsoft.AspNetCore.Mvc.Filters.IActionFilter { }
            public static class Startup {
                public static void Configure(Microsoft.AspNetCore.Mvc.Filters.FilterCollection filters) {
                    filters.Add<Problems>(); filters.Add(typeof(AsyncProblems)); filters.Add(new Problems()); filters.AddService<AsyncProblems>();
                    filters.Add<Timing>(); filters.Add(new Timing());
                }
            }
            """);
        Assert.Equal(new[] { "Filters.Add(Problems)", "Filters.Add(AsyncProblems)", "Filters.Add(Problems)", "Filters.AddService(AsyncProblems)" },
            snapshot.BoundaryHandling.Select(x => x.Registration));
    }
    [Fact]
    public async Task Pragmas_and_SuppressMessage_record_where_SharpView_rules_are_suppressed()
    {
        var snapshot = await Extract("""
            public class C {
            #pragma warning disable SV0001
                void A() { throw new System.Exception(); }
            #pragma warning restore SV0001
                void B() { throw new System.Exception(); }
            #pragma warning disable CS0168, SV0003
                [System.Diagnostics.CodeAnalysis.SuppressMessage("SharpView", "SV0002:Handled by middleware")]
                void D() { throw new System.Exception(); }
                [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060")]
                void E() { }
            }
            #pragma warning disable
            """);
        // Sorted by position: the SV0001 region, SV0003 open to the end of the file, D's attribute, the bare pragma.
        Assert.Equal(["SV0001", "SV0003", "SV0002", null], snapshot.Suppressions.Select(x => x.Rule));
        var (a, b, d) = (Throw("C.A"), Throw("C.B"), Throw("C.D"));
        Assert.True(snapshot.Suppressions.Any(x => x.Covers("SV0001", a)));
        Assert.False(snapshot.Suppressions.Any(x => x.Covers("SV0002", a)));
        Assert.False(snapshot.Suppressions.Any(x => x.Covers("SV0001", b) || x.Covers("SV0003", b)));
        Assert.True(snapshot.Suppressions.Any(x => x.Covers("SV0002", d)));
        Assert.True(snapshot.Suppressions.Any(x => x.Covers("SV0003", d)));
        SourceEvidence Throw(string owner) => snapshot.ThrowSites.Single(x => snapshot.Callables.Single(c => c.Id == x.Owner).DisplaySignature.StartsWith(owner + "(", StringComparison.Ordinal)).Evidence;
    }
    [Fact]
    public async Task Controller_convention_excludes_abstract_generic_and_inherited_opt_outs()
    {
        var snapshot = await Extract("""
            using System;
            namespace Microsoft.AspNetCore.Mvc {
                public class ControllerBase { }
                [AttributeUsage(AttributeTargets.Class, Inherited = true)] public class NonControllerAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.Method, Inherited = true)] public class NonActionAttribute : Attribute { }
            }
            public abstract class AbstractController : Microsoft.AspNetCore.Mvc.ControllerBase { public void AbstractAction() { } }
            public class GenericController<T> : Microsoft.AspNetCore.Mvc.ControllerBase { public void GenericAction() { } }
            [Microsoft.AspNetCore.Mvc.NonController] public class Excluded : Microsoft.AspNetCore.Mvc.ControllerBase { }
            public class ExcludedChild : Excluded { public void Hidden() { } }
            public class BaseController : Microsoft.AspNetCore.Mvc.ControllerBase { [Microsoft.AspNetCore.Mvc.NonAction] public virtual void Helper() { } }
            public class Controller : BaseController { public override void Helper() { } public void Action() { } }
            """);
        var action = Assert.Single(AspNetConventions.ControllerActions(new ProgramIndex(snapshot)));
        Assert.Equal("Controller.Action()", Assert.Single(snapshot.Callables, x => x.Id == action.Method).DisplaySignature);
        Assert.Equal(AspNetConventions.ControllerActionConvention, action.Convention);
    }
    [Fact]
    public async Task Core_controller_classification_matches_extraction_on_edge_cases()
    {
        var snapshot = await Extract("""
            using System;
            namespace Microsoft.AspNetCore.Mvc {
                public class ControllerBase { }
                [AttributeUsage(AttributeTargets.Class, Inherited = true)] public class ControllerAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.Class, Inherited = true)] public class NonControllerAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.Method, Inherited = true)] public class NonActionAttribute : Attribute { }
            }
            [Microsoft.AspNetCore.Mvc.Controller] public class Poco { public void Act() { } public static void Static() { } public void Generic<T>() { } internal void Internal() { } }
            public class Outer<T> { public class NestedController : Microsoft.AspNetCore.Mvc.ControllerBase { public void InGeneric() { } } }
            public interface IThing { void Do(); }
            public class Api : Microsoft.AspNetCore.Mvc.ControllerBase, IThing, IDisposable
            {
                void IThing.Do() { }
                public void Dispose() { }
                public static Api operator +(Api a, Api b) => a;
                ~Api() { }
                public int Value { get; set; }
                public Api() { }
                public void Run() { void Local() { } Local(); Action a = () => { }; }
            }
            public class Plain { public void NotAnAction() { } }
            """);
        // Expected set was verified equal to the former extraction-time classification before it was removed.
        var actions = AspNetConventions.ControllerActions(new ProgramIndex(snapshot));
        Assert.Equal(new[] { "Api.Dispose()", "Api.Run()", "Poco.Act()" }, actions.Select(x => Assert.Single(snapshot.Callables, c => c.Id == x.Method).DisplaySignature).Order(StringComparer.Ordinal));
        Assert.Equal(actions.Select(x => x.Method.Value).Order(StringComparer.Ordinal), actions.Select(x => x.Method.Value));
    }

    [Fact]
    public async Task Type_facts_record_namespace_kind_modifiers_base_class_and_containing_type()
    {
        var snapshot = await Extract("""
            namespace Shop.Orders {
                public class Base { }
                public abstract class Shape : Base, System.IDisposable { public void Dispose() { } public class Nested { } }
                public static class Helpers { }
                public sealed record Order(int Id);
                public record struct Point(int X);
                public interface IStore { }
                public class Box<T> { }
                public struct Value { }
                public enum Color { Red }
                public delegate void Handler();
            }
            public class Global { }
            """);
        TypeFact Type(string name) => Assert.Single(snapshot.Types, x => x.DisplayName == name);
        void Expect(string name, DeclaredTypeKind kind, TypeModifiers modifiers)
        {
            var type = Type(name);
            Assert.Equal((kind, modifiers, "Shop.Orders"), (type.Kind, type.Modifiers, type.Namespace));
        }
        Expect("Shop.Orders.Base", DeclaredTypeKind.Class, TypeModifiers.None);
        Expect("Shop.Orders.Shape", DeclaredTypeKind.Class, TypeModifiers.Abstract);
        Expect("Shop.Orders.Shape.Nested", DeclaredTypeKind.Class, TypeModifiers.None);
        Expect("Shop.Orders.Helpers", DeclaredTypeKind.Class, TypeModifiers.Static);
        Expect("Shop.Orders.Order", DeclaredTypeKind.Record, TypeModifiers.Sealed);
        Expect("Shop.Orders.Point", DeclaredTypeKind.RecordStruct, TypeModifiers.Sealed);
        Expect("Shop.Orders.IStore", DeclaredTypeKind.Interface, TypeModifiers.Abstract);
        Expect("Shop.Orders.Box<T>", DeclaredTypeKind.Class, TypeModifiers.Generic);
        Expect("Shop.Orders.Value", DeclaredTypeKind.Struct, TypeModifiers.Sealed);
        Expect("Shop.Orders.Color", DeclaredTypeKind.Enum, TypeModifiers.Sealed);
        Expect("Shop.Orders.Handler", DeclaredTypeKind.Delegate, TypeModifiers.Sealed);
        Assert.Equal("", Type("Global").Namespace);
        var shape = Type("Shop.Orders.Shape");
        Assert.Equal(Type("Shop.Orders.Base").Id, shape.BaseClass);
        Assert.Equal(["Shop.Orders.Base", "System.IDisposable"], shape.BaseTypes.Select(x => x.DisplayName));
        Assert.Equal(Type("object").Id, Type("Shop.Orders.Base").BaseClass);
        Assert.Null(Type("Shop.Orders.IStore").BaseClass);
        Assert.Equal(shape.Id, Type("Shop.Orders.Shape.Nested").ContainingType);
        Assert.Null(shape.ContainingType);
        Assert.Equal("Fixture|T:Shop.Orders.Shape.Nested", Type("Shop.Orders.Shape.Nested").Key);
    }

    [Fact]
    public async Task Callable_modifiers_and_overrides_link_to_the_directly_overridden_declaration()
    {
        var snapshot = await Extract("""
            public abstract class Animal { public abstract string Sound(); public virtual void Eat() { } }
            public class Dog : Animal { public override string Sound() => "woof"; public override void Eat() { } public override string ToString() => "dog"; }
            public class Puppy : Dog { public sealed override void Eat() { } }
            public interface IGreeter { void Greet(); }
            public class Greeter : IGreeter { void IGreeter.Greet() { } ~Greeter() { } }
            public static class Tools {
                public static T Echo<T>(T value) => value;
                public static int Twice(this int value) => value * 2;
            }
            public readonly struct Money {
                public static Money operator +(Money left, Money right) => left;
                public static explicit operator int(Money value) => 0;
            }
            """);
        CallableFact Method(string signature) => Assert.Single(snapshot.Callables, x => x.DisplaySignature == signature);
        Assert.Equal(CallableModifiers.Abstract, Method("Animal.Sound()").Modifiers);
        Assert.Equal(CallableModifiers.Virtual, Method("Animal.Eat()").Modifiers);
        Assert.Equal(CallableModifiers.Override, Method("Dog.Sound()").Modifiers);
        Assert.Equal(Method("Animal.Sound()").Id, Method("Dog.Sound()").Overrides);
        Assert.Equal(CallableModifiers.Override | CallableModifiers.Sealed, Method("Puppy.Eat()").Modifiers);
        Assert.Equal(Method("Dog.Eat()").Id, Method("Puppy.Eat()").Overrides);
        Assert.Equal(Method("Animal.Eat()").Id, Method("Dog.Eat()").Overrides);
        Assert.Null(Method("Animal.Eat()").Overrides);
        var objectToString = Assert.Single(snapshot.Callables, x => x.Id == Method("Dog.ToString()").Overrides);
        Assert.Equal(("object.ToString()", BodyStatus.MetadataOnly, CallableModifiers.Virtual), (objectToString.DisplaySignature, objectToString.Body, objectToString.Modifiers));
        Assert.Equal(CallableModifiers.ExplicitImplementation, Method("Greeter.IGreeter.Greet()").Modifiers);
        Assert.Null(Method("Greeter.IGreeter.Greet()").Overrides);
        Assert.Equal(CallableModifiers.Finalizer, Method("Greeter.Finalize()").Modifiers);
        // Roslyn reports destructors as neither override nor overriding object.Finalize(); modifiers mirror Roslyn's flags.
        Assert.Null(Method("Greeter.Finalize()").Overrides);
        Assert.Equal(CallableModifiers.Static | CallableModifiers.Generic, Method("Tools.Echo<T>(T)").Modifiers);
        Assert.Equal(CallableModifiers.Static | CallableModifiers.Extension, Method("Tools.Twice(int)").Modifiers);
        Assert.Equal(CallableModifiers.Static | CallableModifiers.Operator, Method("Money.op_Addition(Money, Money)").Modifiers);
        Assert.Equal(CallableModifiers.Static | CallableModifiers.Operator, Method("Money.op_Explicit(Money)").Modifiers);
    }

    [Fact]
    public async Task Attributes_keep_names_and_render_positional_and_named_arguments_in_order()
    {
        var snapshot = await Extract("""
            using System;
            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
            public sealed class RouteAttribute : Attribute {
                public RouteAttribute(string template) { }
                public RouteAttribute(string template, int order) { }
                public string Name { get; set; } = "";
                public int Order { get; set; }
            }
            [Route("orders", Name = "list", Order = 2)]
            [Obsolete]
            [Route("archive", 3)]
            public class OrdersController { [Route("get")] public void Get() { } }
            """);
        var controller = Assert.Single(snapshot.Types, x => x.DisplayName == "OrdersController");
        Assert.Equal(["RouteAttribute", "RouteAttribute", "System.ObsoleteAttribute"], controller.Attributes.Select(x => x.Name));
        Assert.Equal(new[] { "\"archive\"", "3" }, controller.Attributes[0].Arguments);
        Assert.Empty(controller.Attributes[0].NamedArguments);
        Assert.Equal(new[] { "\"orders\"" }, controller.Attributes[1].Arguments);
        Assert.Equal(new[] { new NamedArgumentFact("Name", "\"list\""), new NamedArgumentFact("Order", "2") }, controller.Attributes[1].NamedArguments);
        Assert.Empty(controller.Attributes[2].Arguments);
        var get = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "OrdersController.Get()");
        Assert.Equal(new[] { "\"get\"" }, Assert.Single(get.Attributes).Arguments);
    }

    [Fact]
    public async Task Keys_are_portable_unique_and_stable_across_locations()
    {
        const string source = """
            namespace Shop {
                public static class Sample {
                    public static void Parent() {
                        void Local() { System.Console.WriteLine(); }
                        System.Action first = () => Local(); System.Func<int, int> second = x => x;
                    }
                }
            }
            """;
        var snapshot = await Extract(source);
        var moved = await Extract(source, path: "/elsewhere/deep/src/Input.cs", projectPath: "/elsewhere/deep/Fixture.csproj");
        CallableFact Single(CallableKind kind, int line = 0, int column = 0) => Assert.Single(snapshot.Callables,
            x => x.Kind == kind && (line == 0 || x.Declarations[0].StartLine == line && x.Declarations[0].StartColumn == column));
        Assert.Equal("Fixture|M:Shop.Sample.Parent", Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Shop.Sample.Parent()").Key);
        Assert.Equal("Fixture|M:Shop.Sample.Parent|local:Local@Input.cs:4:13", Single(CallableKind.LocalFunction).Key);
        Assert.Equal("Fixture|M:Shop.Sample.Parent|lambda:0@Input.cs:5:35", Single(CallableKind.Lambda, 5, 35).Key);
        Assert.Equal("Fixture|M:Shop.Sample.Parent|lambda:1@Input.cs:5:81", Single(CallableKind.Lambda, 5, 81).Key);
        Assert.Equal("System.Console|M:System.Console.WriteLine", Assert.Single(snapshot.Callables, x => x.DisplaySignature == "System.Console.WriteLine()").Key);
        Assert.Equal("Fixture|T:Shop.Sample", Assert.Single(snapshot.Types, x => x.DisplayName == "Shop.Sample").Key);
        var keys = snapshot.Types.Select(x => x.Key).Concat(snapshot.Callables.Select(x => x.Key)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key =>
        {
            Assert.DoesNotContain("/fixture", key, StringComparison.Ordinal);
            Assert.DoesNotContain("Version=", key, StringComparison.Ordinal);
            Assert.DoesNotContain("net10.0", key, StringComparison.Ordinal);
            Assert.DoesNotContain("|Debug", key, StringComparison.Ordinal);
        });
        Assert.Equal(keys.Order(StringComparer.Ordinal).Select(x => x.Replace("@Input.cs", "@src/Input.cs", StringComparison.Ordinal)),
            moved.Types.Select(x => x.Key).Concat(moved.Callables.Select(x => x.Key)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Same_signature_lambdas_in_different_methods_of_one_type_are_distinct_callables()
    {
        var snapshot = await Extract("""
            using System;
            namespace Shop {
                public static class Sample {
                    public static void One() { Action a = () => First(); a(); }
                    public static void Two() { Action b = () => Second(); b(); }
                    public static void First() { }
                    public static void Second() { }
                }
            }
            """);
        CallableFact Method(string signature) => Assert.Single(snapshot.Callables, x => x.DisplaySignature == signature);
        var lambdas = snapshot.Callables.Where(x => x.Kind == CallableKind.Lambda).ToArray();
        Assert.Equal(2, lambdas.Select(x => x.Id).Distinct().Count());
        var first = Assert.Single(lambdas, x => x.ContainingCallable == Method("Shop.Sample.One()").Id);
        var second = Assert.Single(lambdas, x => x.ContainingCallable == Method("Shop.Sample.Two()").Id);
        Assert.Equal(Method("Shop.Sample.First()").Id, Assert.Single(snapshot.CallSites, x => x.Caller == first.Id).BoundTarget);
        Assert.Equal(Method("Shop.Sample.Second()").Id, Assert.Single(snapshot.CallSites, x => x.Caller == second.Id).BoundTarget);
        Assert.Equal("Fixture|M:Shop.Sample.One|lambda:0@Input.cs:4:47", first.Key);
        Assert.Equal("Fixture|M:Shop.Sample.Two|lambda:0@Input.cs:5:47", second.Key);
        Assert.Equal((4, 47), (first.Declarations[0].StartLine, first.Declarations[0].StartColumn));
        Assert.Equal((5, 47), (second.Declarations[0].StartLine, second.Declarations[0].StartColumn));
    }

    [Fact]
    public async Task Same_named_local_functions_in_different_methods_of_one_type_are_distinct_callables()
    {
        var snapshot = await Extract("""
            namespace Shop {
                public static class Sample {
                    public static void One() { Helper(); void Helper() => LocalA(); }
                    public static void Two() { Helper(); void Helper() => LocalB(); }
                    public static void LocalA() { }
                    public static void LocalB() { }
                }
            }
            """);
        CallableFact Method(string signature) => Assert.Single(snapshot.Callables, x => x.DisplaySignature == signature);
        var locals = snapshot.Callables.Where(x => x.Kind == CallableKind.LocalFunction).ToArray();
        Assert.Equal(2, locals.Select(x => x.Id).Distinct().Count());
        var one = Assert.Single(locals, x => x.ContainingCallable == Method("Shop.Sample.One()").Id);
        var two = Assert.Single(locals, x => x.ContainingCallable == Method("Shop.Sample.Two()").Id);
        Assert.Equal(Method("Shop.Sample.LocalA()").Id, Assert.Single(snapshot.CallSites, x => x.Caller == one.Id).BoundTarget);
        Assert.Equal(Method("Shop.Sample.LocalB()").Id, Assert.Single(snapshot.CallSites, x => x.Caller == two.Id).BoundTarget);
        Assert.Equal(one.Id, Assert.Single(snapshot.CallSites, x => x.Caller == Method("Shop.Sample.One()").Id).BoundTarget);
        Assert.Equal(two.Id, Assert.Single(snapshot.CallSites, x => x.Caller == Method("Shop.Sample.Two()").Id).BoundTarget);
        Assert.Equal("Fixture|M:Shop.Sample.One|local:Helper@Input.cs:3:46", one.Key);
        Assert.Equal("Fixture|M:Shop.Sample.Two|local:Helper@Input.cs:4:46", two.Key);
        Assert.Equal(BodyStatus.Source, one.Body);
        Assert.Equal(BodyStatus.Source, two.Body);
    }

    [Fact]
    public async Task Lambdas_nested_in_local_functions_and_lambdas_are_keyed_by_the_enclosing_member()
    {
        var snapshot = await Extract("""
            using System;
            namespace Shop {
                public static class Sample {
                    public static void Outer() {
                        void Local() { Action a = () => Leaf(1); Action b = () => Leaf(2); }
                        Func<Action> c = () => () => Leaf(3);
                    }
                    public static void Leaf(int value) { }
                }
            }
            """);
        var local = Assert.Single(snapshot.Callables, x => x.Kind == CallableKind.LocalFunction);
        var lambdas = snapshot.Callables.Where(x => x.Kind == CallableKind.Lambda).OrderBy(x => x.Declarations[0].SpanStart).ToArray();
        Assert.Equal(
            ["Fixture|M:Shop.Sample.Outer|lambda:0@Input.cs:5:39", "Fixture|M:Shop.Sample.Outer|lambda:1@Input.cs:5:65",
             "Fixture|M:Shop.Sample.Outer|lambda:2@Input.cs:6:30", "Fixture|M:Shop.Sample.Outer|lambda:3@Input.cs:6:36"],
            lambdas.Select(x => x.Key));
        Assert.Equal(4, lambdas.Select(x => x.Id).Distinct().Count());
        Assert.Equal(local.Id, lambdas[0].ContainingCallable);
        Assert.Equal(local.Id, lambdas[1].ContainingCallable);
        Assert.Equal(lambdas[2].Id, lambdas[3].ContainingCallable);
        Assert.Equal("Fixture|M:Shop.Sample.Outer|local:Local@Input.cs:5:13", local.Key);
        // Each lambda owns exactly its own Leaf(n) call; the outer lambda of c only returns the inner one.
        Assert.Equal(new[] { (5, 45), (5, 71), (6, 42) }, new[] { lambdas[0], lambdas[1], lambdas[3] }
            .Select(lambda => Assert.Single(snapshot.CallSites, x => x.Caller == lambda.Id).Evidence).Select(x => (x.StartLine, x.StartColumn)));
        Assert.DoesNotContain(snapshot.CallSites, x => x.Caller == lambdas[2].Id);
    }

    [Fact]
    public async Task Colliding_keys_from_projects_sharing_a_file_name_get_deterministic_suffixes()
    {
        using var workspace = new AdhocWorkspace();
        var projects = new List<(Project Project, Compilation Compilation, CompilationFact Fact)>();
        foreach (var (name, directory) in new[] { ("One", "/a"), ("Two", "/b") })
        {
            var (project, compilation) = await Compile(workspace, name, "public class Shared { public void Run() { } }", directory + "/Input.cs");
            projects.Add((project, compilation, new CompilationFact(new(name), directory + "/Shared.csproj", compilation.Assembly.Identity.ToString(), "net10.0", "Debug", "Preview", "Disable", [], [])));
        }
        var extractor = new FactExtractor([.. projects.Select(x => x.Fact)], []);
        foreach (var (project, compilation, fact) in projects) await extractor.RegisterAsync(project, compilation, fact, true, CancellationToken.None);
        extractor.Extract(CancellationToken.None);
        var snapshot = extractor.Snapshot();
        Assert.Equal(["Shared|T:Shared", "Shared|T:Shared#2"], snapshot.Types.Where(x => x.DisplayName == "Shared").OrderBy(x => x.Compilation.Value, StringComparer.Ordinal).Select(x => x.Key));
        Assert.Equal(["Shared|M:Shared.Run", "Shared|M:Shared.Run#2"], snapshot.Callables.Where(x => x.DisplaySignature == "Shared.Run()").OrderBy(x => x.Compilation.Value, StringComparer.Ordinal).Select(x => x.Key));
        var keys = snapshot.Types.Select(x => x.Key).Concat(snapshot.Callables.Select(x => x.Key)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Framework_selection_uses_conditional_assets_and_rejects_ambiguity()
    {
        MSBuildBootstrap.RegisterSdk();
        var directory = Path.Combine(Path.GetTempPath(), "sharpview-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Fixture.csproj");
        try
        {
            File.WriteAllText(path, """
                <Project><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                <ProjectAssetsFile Condition="'$(TargetFramework)' == 'net10.0'">net10.assets.json</ProjectAssetsFile>
                <Nullable>enable</Nullable><TreatWarningsAsErrors>True</TreatWarningsAsErrors><WarningsAsErrors>NU1605; nullable</WarningsAsErrors>
                </PropertyGroup><ItemGroup><ProjectReference Include="../Other/Other.csproj" /></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(directory, "net10.assets.json"), "{}");
            var settings = ProjectSelection.Read(path, new(path, TargetFramework: "net10.0"));
            Assert.Equal("net10.0", settings.Framework);
            Assert.Equal("enable", settings.Nullable);
            Assert.True(settings.TreatWarningsAsErrors);
            Assert.Equal(["NU1605", "nullable"], settings.WarningsAsErrors);
            Assert.Equal([Path.GetFullPath(Path.Combine(directory, "..", "Other", "Other.csproj"))], settings.ProjectReferences);
            Assert.Contains("choices: net9.0, net10.0", Assert.Throws<ArgumentException>(() => ProjectSelection.Read(path, new(path))).Message);
            Assert.Contains("not declared", Assert.Throws<ArgumentException>(() => ProjectSelection.Read(path, new(path, TargetFramework: "net8.0"))).Message);
            Assert.Contains("not restored", Assert.Throws<ArgumentException>(() => ProjectSelection.Read(path, new(path, TargetFramework: "net9.0"))).Message);
            Assert.False(Directory.Exists(Path.Combine(directory, "obj")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Loader_rejects_missing_input_and_observes_precancelled_requests()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => RoslynProgramLoader.LoadAsync(new("/fixture/missing.csproj")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => RoslynProgramLoader.LoadAsync(new("/fixture/missing.csproj"), new CancellationToken(true)));
    }

    [Fact]
    public async Task Implementation_candidates_cover_interfaces_overrides_and_accessors()
    {
        var snapshot = await Extract("""
            public interface IShape { double Area(); int Sides { get; } }
            public class Square : IShape { public double Area() => 1; public int Sides => 4; }
            public class Circle : IShape { public double Area() => 3; public int Sides { get { return 0; } } }
            public interface IGreeter { void Greet(); }
            public class Explicit : IGreeter { void IGreeter.Greet() { } }
            public abstract class Animal { public abstract void Sound(); public virtual void Eat() { } }
            public class Dog : Animal { public override void Sound() { } public override void Eat() { } }
            public class Puppy : Dog { public override void Eat() { } }
            """);
        string Name(CallableId id) => Assert.Single(snapshot.Callables, x => x.Id == id).DisplaySignature;
        var pairs = snapshot.Implementations.Select(x => (Name(x.Declared), Name(x.Implementation), x.Kind)).ToList();
        Assert.Equal(8, pairs.Count);
        Assert.Contains(("IShape.Area()", "Square.Area()", ImplementationKind.Interface), pairs);
        Assert.Contains(("IShape.Area()", "Circle.Area()", ImplementationKind.Interface), pairs);
        Assert.Contains(("IShape.get_Sides()", "Square.get_Sides()", ImplementationKind.Interface), pairs);
        Assert.Contains(("IShape.get_Sides()", "Circle.get_Sides()", ImplementationKind.Interface), pairs);
        Assert.Contains(("IGreeter.Greet()", "Explicit.IGreeter.Greet()", ImplementationKind.Interface), pairs);
        Assert.Contains(("Animal.Sound()", "Dog.Sound()", ImplementationKind.Override), pairs);
        // Each override step is its own fact; Animal.Eat() -> Puppy.Eat() is not flattened.
        Assert.Contains(("Animal.Eat()", "Dog.Eat()", ImplementationKind.Override), pairs);
        Assert.Contains(("Dog.Eat()", "Puppy.Eat()", ImplementationKind.Override), pairs);
        Assert.Equal(snapshot.Implementations.OrderBy(x => x.Declared.Value, StringComparer.Ordinal).ThenBy(x => x.Implementation.Value, StringComparer.Ordinal), snapshot.Implementations);
    }

    [Fact]
    public async Task Field_and_property_dependencies_carry_kind_and_declaration_evidence_without_backing_fields()
    {
        var snapshot = await Extract("""
            public class Holder
            {
                private System.Collections.Generic.List<string> items = new();
                public int Count { get; set; }
                public string Name => "x";
            }
            """);
        var holder = Assert.Single(snapshot.Types, x => x.DisplayName == "Holder");
        var own = snapshot.Dependencies.Where(x => x.OwningType == holder.Id && x.OwningCallable is null && x.Kind is DependencyKind.Field or DependencyKind.Property).ToList();
        var field = Assert.Single(own, x => x.Kind == DependencyKind.Field && x.ReferencedType.DisplayName == "System.Collections.Generic.List<string>");
        Assert.Contains(own, x => x.Kind == DependencyKind.Field && x.ReferencedType.DisplayName == "string");
        Assert.Equal(2, own.Count(x => x.Kind == DependencyKind.Field));
        Assert.Equal(1, own.Count(x => x.Kind == DependencyKind.Property && x.ReferencedType.DisplayName == "int"));
        Assert.Equal(1, own.Count(x => x.Kind == DependencyKind.Property && x.ReferencedType.DisplayName == "string"));
        Assert.Equal((3, "items = new()".Length), (field.Evidence!.StartLine, field.Evidence.SpanLength));
        var count = Assert.Single(own, x => x.Kind == DependencyKind.Property && x.ReferencedType.DisplayName == "int");
        Assert.Equal((4, "public int Count { get; set; }".Length), (count.Evidence!.StartLine, count.Evidence.SpanLength));
        // The expression-bodied property keeps its getter Signature dependency in addition to the property-level one.
        var getter = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Holder.get_Name()");
        Assert.Contains(snapshot.Dependencies, x => x.OwningCallable == getter.Id && x.Kind == DependencyKind.Signature && x.ReferencedType.DisplayName == "string");
        Assert.DoesNotContain(snapshot.Dependencies, x => x.Evidence is { } e && e.StartLine == 5 && x.Kind == DependencyKind.Field);
    }

    [Fact]
    public async Task Compiler_provided_members_are_synthesized_while_written_bodies_and_declarations_keep_their_status()
    {
        var snapshot = await Extract("""
            public record Error(string Code, string Message);
            public class Item
            {
                public string Name { get; set; } = "";
                public int Count { get; init; }
                public event System.EventHandler? Changed;
                public Item() { }
            }
            public class Plain { }
            public class Primary(string name) { public string Hello() => name; }
            public abstract class Shape { public abstract int Sides { get; } }
            public interface IThing { int Value { get; } }
            public class Use
            {
                public object Make()
                {
                    _ = new Plain(); _ = new Primary("x"); _ = new Error("a", "b");
                    var item = new Item { Name = "n", Count = 1 };
                    item.Changed += (_, _) => { };
                    return item;
                }
            }
            """);
        BodyStatus Body(Func<CallableFact, bool> match) => Assert.Single(snapshot.Callables, x => match(x)).Body;
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.StartsWith("Error..ctor(", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.StartsWith("Plain..ctor(", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.StartsWith("Primary..ctor(", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.Contains("Item.get_Name", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.Contains("Item.set_Name", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.Contains("Item.set_Count", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Synthesized, Body(x => x.DisplaySignature.Contains("Item.add_Changed", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Source, Body(x => x.DisplaySignature.StartsWith("Item..ctor()", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.Source, Body(x => x.DisplaySignature.StartsWith("Primary.Hello(", StringComparison.Ordinal)));
        // A declaration without code that is not compiler-provided stays unknown.
        Assert.Equal(BodyStatus.NoBody, Body(x => x.DisplaySignature.Contains("Shape.get_Sides", StringComparison.Ordinal)));
        Assert.Equal(BodyStatus.NoBody, Body(x => x.DisplaySignature.Contains("IThing.get_Value", StringComparison.Ordinal)));
        Assert.All(snapshot.Callables.Where(x => x.Body == BodyStatus.Synthesized), x => Assert.True(x.HasBody));
        // Library members stay library members.
        Assert.All(snapshot.Callables.Where(x => x.DisplaySignature.StartsWith("object.", StringComparison.Ordinal)), x => Assert.Equal(BodyStatus.MetadataOnly, x.Body));
    }

    [Fact]
    public async Task Foreach_and_using_record_their_implicit_calls_awaited_in_the_async_forms()
    {
        var snapshot = await Extract("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            public class Res : System.IDisposable { public void Dispose() { } }
            public class Use
            {
                public void Loop(List<int> items, int[] numbers, IEnumerable<int> sequence)
                {
                    foreach (var x in items) { }
                    foreach (var x in numbers) { }
                    foreach (var x in sequence) { }
                }
                public void Scoped() { using var first = new Res(); using (new Res()) { } }
                public void ViaInterface(System.IDisposable resource) { using (resource) { } }
                public async Task Later(IAsyncEnumerable<int> stream) { await foreach (var x in stream) { } }
                public async Task Disposing(System.IAsyncDisposable resource) { await using (resource) { } }
            }
            """);
        CallableFact Find(string prefix) => Assert.Single(snapshot.Callables, x => x.DisplaySignature.StartsWith(prefix, StringComparison.Ordinal));
        CallSiteFact[] Implicit(CallableFact owner) => [.. snapshot.CallSites.Where(x => x.Caller == owner.Id && x.IsImplicit)];
        var loop = Find("Use.Loop(");
        var loopCalls = Implicit(loop);
        // Arrays are enumerated without calls: the list and the sequence each contribute one of each.
        Assert.Equal(2, loopCalls.Count(x => x.TargetDisplay.EndsWith("GetEnumerator()", StringComparison.Ordinal)));
        Assert.Equal(2, loopCalls.Count(x => x.TargetDisplay.EndsWith("MoveNext()", StringComparison.Ordinal)));
        Assert.DoesNotContain(snapshot.Gaps, x => x.Owner == loop.Id);
        // A list's enumerator is a concrete library type: its calls are direct, not interface calls with candidates.
        Assert.All(loopCalls.Where(x => x.TargetDisplay.Contains("List<T>", StringComparison.Ordinal)), x => Assert.Equal(DispatchKind.Direct, x.Dispatch));
        Assert.Contains(loopCalls, x => x.TargetDisplay == "System.Collections.Generic.List<T>.Enumerator.Dispose()" && x.Dispatch == DispatchKind.Direct);
        // A sequence typed as an interface stays an interface call.
        Assert.Contains(loopCalls, x => x.TargetDisplay.Contains("IEnumerable<T>.GetEnumerator", StringComparison.Ordinal) && x.Dispatch == DispatchKind.Interface);

        // A resource typed as a source class binds to that class's own Dispose, exactly as an explicit call would.
        var scoped = Implicit(Find("Use.Scoped("));
        Assert.Equal(2, scoped.Length);
        Assert.All(scoped, x => { Assert.Equal(DispatchKind.Direct, x.Dispatch); Assert.StartsWith("Res.Dispose(", x.TargetDisplay, StringComparison.Ordinal); });
        Assert.DoesNotContain(snapshot.Gaps, x => x.Owner == Find("Use.Scoped(").Id);
        // A resource typed as the interface is an interface call whose candidate is the source implementation.
        var viaInterface = Assert.Single(Implicit(Find("Use.ViaInterface(")));
        Assert.Equal(DispatchKind.Interface, viaInterface.Dispatch);
        Assert.Contains(snapshot.Implementations, x => x.Declared == viaInterface.BoundTarget && snapshot.Callables.Single(c => c.Id == x.Implementation).DisplaySignature.StartsWith("Res.Dispose(", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Gaps, x => x.Owner == Find("Use.Later(").Id || x.Owner == Find("Use.Disposing(").Id);
        Assert.Contains(Implicit(Find("Use.Later(")), x => x.TargetDisplay.Contains("MoveNextAsync", StringComparison.Ordinal) && x.IsAwaited);
        Assert.Contains(Implicit(Find("Use.Disposing(")), x => x.TargetDisplay.Contains("DisposeAsync", StringComparison.Ordinal) && x.IsAwaited);
    }

    [Fact]
    public async Task Handler_outcome_follows_control_flow_and_a_finally_matters_only_when_it_can_throw()
    {
        var snapshot = await Extract("""
            using System;
            public class Res : IDisposable { public void Dispose() { } }
            public static class H
            {
                static void Log(Exception e) => Console.WriteLine(e);
                static void Cleanup() { }
                public static int Run(bool b)
                {
                    try { } catch (InvalidOperationException e) { Log(e); throw; }
                    try { } catch (ArgumentException e) { Console.WriteLine(e); return 0; }
                    try { } catch (FormatException e) { if (b) return 1; throw; }
                    try { } catch (TimeoutException e) { throw e; }
                    try { } catch (NotSupportedException e) { Log(e); throw new InvalidOperationException("x", e); }
                    try { } catch (Exception) { }
                    try { } finally { Console.WriteLine(); System.IO.Stream.Null.Dispose(); }
                    try { } finally { Cleanup(); }
                    return 2;
                }
            }
            """);
        HandlerOutcome Outcome(string type) => Assert.Single(snapshot.Catches, x => x.CaughtType?.DisplayName == type).Outcome;
        Assert.Equal(HandlerOutcome.Rethrows, Outcome("System.InvalidOperationException"));
        Assert.Equal(HandlerOutcome.Intercepts, Outcome("System.ArgumentException"));
        Assert.Equal(HandlerOutcome.MayRethrow, Outcome("System.FormatException"));
        Assert.Equal(HandlerOutcome.Rethrows, Outcome("System.TimeoutException"));
        Assert.Equal(HandlerOutcome.Replaces, Outcome("System.NotSupportedException"));
        Assert.Equal(HandlerOutcome.Intercepts, Outcome("System.Exception"));
        // `throw e` of the catch variable is a rethrow, not a new origin; the replacement is the only explicit throw.
        Assert.Equal("System.InvalidOperationException", Assert.Single(snapshot.ThrowSites, x => x.Kind == ThrowKind.Explicit).StaticExceptionType?.DisplayName);
        var finallies = snapshot.ExceptionRegions.Where(x => x.Catches.IsEmpty).OrderBy(x => x.ProtectedBlock.SpanStart).ToArray();
        Assert.Equal([false, true], finallies.Select(x => x.FinallyMayThrow));
    }
}
