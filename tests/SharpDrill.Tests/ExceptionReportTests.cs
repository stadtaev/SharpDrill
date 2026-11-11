using Microsoft.CodeAnalysis;
using SharpDrill.Core;
using Xunit;

namespace SharpDrill.Tests;

// The consumer API: entry points of any .NET program and the exception report read from them.
public sealed class ExceptionReportTests
{
    [Fact]
    public async Task A_console_app_is_entered_through_its_top_level_statements()
    {
        var program = new ProgramIndex(await ExtractionTests.Extract("""
            try { Work.Parse(); } catch (System.FormatException) { }
            Work.Save();
            public static class Work
            {
                public static void Parse() => throw new System.ArgumentException();
                public static void Save() => throw new System.IO.IOException();
            }
            """, output: OutputKind.ConsoleApplication));
        var entry = Assert.Single(program.EntryPoints());
        Assert.Equal((EntryPointKind.Main, "Program.<Main>$(string[])"), (entry.Kind, entry.Method.DisplaySignature));
        var report = program.Exceptions();
        Assert.Equal(["System.ArgumentException", "System.IO.IOException"], report.Uncaught.Select(x => x.ExceptionType).Order(StringComparer.Ordinal));
        Assert.All(report.Uncaught, x => Assert.Equal(entry.Method, Assert.Single(x.EscapesFrom)));
        Assert.All(report.Uncaught, x => Assert.Empty(x.GlobalHandlers));
        var missed = Assert.Single(report.Miscaught);
        Assert.Equal("System.FormatException", Assert.Single(missed.Clauses).CaughtType!.DisplayName);
        Assert.Equal(ExceptionStatus.MayEscape, Assert.Single(missed.Then));
        Assert.Equal(["Program.<Main>$(string[])", "Work.Parse()"], missed.Example.Steps.Select(x => x.Method.DisplaySignature));
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task A_classic_Main_is_an_entry_point_and_a_library_beside_it_is_not()
    {
        var program = new ProgramIndex(await ExtractionTests.Extract("""
            public static class App { public static int Main(string[] args) { Lib.Run(); return 0; } }
            public class Lib { public static void Run() => throw new System.InvalidOperationException(); }
            """, output: OutputKind.ConsoleApplication));
        // One project: the program's own Main enters it, so its public methods are not entry points.
        Assert.Equal(["App.Main(string[])"], program.EntryPoints().Select(x => x.Method.DisplaySignature));
        Assert.Equal("App.Main(string[])", program.EntryPoint("app.main").Method.DisplaySignature);
        Assert.Equal("Lib.Run()", Assert.Single(program.Exceptions().Uncaught).Example.Steps[^1].Method.DisplaySignature);
    }

    [Fact]
    public async Task A_library_is_entered_through_its_public_methods()
    {
        var program = new ProgramIndex(await ExtractionTests.Extract("""
            public class Store
            {
                public void Save() => Write();
                void Write() => throw new System.IO.IOException();
                internal void Hidden() => throw new System.Exception();
            }
            internal class Inner { public void Run() => throw new System.Exception(); }
            """));
        Assert.Equal([(EntryPointKind.PublicApi, "Store.Save()")], program.EntryPoints().Select(x => (x.Kind, x.Method.DisplaySignature)));
        var uncaught = Assert.Single(program.Exceptions().Uncaught);
        Assert.Equal(["Store.Save()", "Store.Write()"], uncaught.Example.Steps.Select(x => x.Method.DisplaySignature));
        Assert.Contains("is not an entry point", Assert.Throws<ArgumentException>(() => program.Exceptions(new() { From = "Store.Write" })).Message);
    }
}
