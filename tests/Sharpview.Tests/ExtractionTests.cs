using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Sharpview.Core;
using Sharpview.Roslyn;
using Xunit;

namespace Sharpview.Tests;

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
    public async Task Direct_await_observation_and_aliased_task_unknown_are_distinguished()
    {
        var snapshot = await Extract("""
            using System.Threading.Tasks;
            public static class Sample {
                public static Task Produce() => Task.CompletedTask;
                public static async Task Consume() { await Produce(); var task = Produce(); await task; }
            }
            """);
        var consume = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Sample.Consume()");
        var calls = snapshot.CallSites.Where(x => x.Caller == consume.Id && x.TargetDisplay == "Sample.Produce()").OrderBy(x => x.Evidence.SpanStart).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True(calls[0].IsAwaited);
        Assert.False(calls[1].IsAwaited);
        Assert.Contains(snapshot.Gaps, x => x.Owner == consume.Id && x.Message.Contains("aliased task", StringComparison.Ordinal));
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
        var action = Assert.Single(snapshot.ControllerActions);
        Assert.Equal("Controller.Action()", Assert.Single(snapshot.Callables, x => x.Id == action.Method).DisplaySignature);
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
        Assert.True(Method("Greeter.Finalize()").Modifiers.HasFlag(CallableModifiers.Finalizer));
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
                </PropertyGroup></Project>
                """);
            File.WriteAllText(Path.Combine(directory, "net10.assets.json"), "{}");
            Assert.Equal("net10.0", ProjectSelection.Read(path, new(path, TargetFramework: "net10.0")));
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
}
