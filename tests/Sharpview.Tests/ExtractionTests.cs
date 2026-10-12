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
    private static async Task<ProgramSnapshot> Extract(string source, bool includeGenerated = true, string path = "/fixture/Input.cs")
    {
        using var workspace = new AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x));
        var project = workspace.AddProject("ExtractionFixture", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .AddMetadataReferences(references)
            .AddDocument("Input.cs", SourceText.From(source), filePath: path).Project;
        var compilation = (await project.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), x => x.Severity == DiagnosticSeverity.Error);
        var fact = new CompilationFact(new("fixture"), "/fixture/Fixture.csproj", compilation.Assembly.Identity.ToString(), "net10.0", "Debug", "Preview", "Disable", [], []);
        var extractor = new FactExtractor([fact], []);
        await extractor.RegisterAsync(project, compilation, fact, includeGenerated, CancellationToken.None);
        extractor.Extract(CancellationToken.None);
        return extractor.Snapshot();
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
