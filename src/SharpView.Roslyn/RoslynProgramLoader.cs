using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using SharpView.Core;

namespace SharpView.Roslyn;

public static class RoslynProgramLoader
{
    public static Task<ProgramSnapshot> LoadAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MSBuildBootstrap.RegisterSdk();
        return LoadRegisteredAsync(request, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ProgramSnapshot> LoadRegisteredAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(request.InputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("Analysis input not found.", input);
        if (Path.GetExtension(input) is not (".csproj" or ".sln" or ".slnx")) throw new ArgumentException("Input must be .csproj, .sln or .slnx.");
        var properties = new Dictionary<string, string> { ["Configuration"] = request.Configuration };
        if (request.TargetFramework is { } framework) properties["TargetFramework"] = framework;
        using var workspace = MSBuildWorkspace.Create(properties);
        var loadGaps = new List<AnalysisGap>();
        using var diagnosticsRegistration = workspace.RegisterWorkspaceFailedHandler(e =>
        {
            lock (loadGaps) loadGaps.Add(new(GapKind.ProjectLoad, null, null, e.Diagnostic.Message, null));
        });
        var solution = Path.GetExtension(input) == ".csproj"
            ? (await workspace.OpenProjectAsync(input, cancellationToken: cancellationToken)).Solution
            : await workspace.OpenSolutionAsync(input, cancellationToken: cancellationToken);
        var contexts = new List<(Project Project, Compilation Compilation, CompilationFact Fact)>();
        var excludedTests = new List<string>();
        foreach (var project in solution.Projects.OrderBy(x => x.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp) { loadGaps.Add(new(GapKind.Unsupported, null, null, $"Non-C# project excluded: {project.FilePath}", null)); continue; }
            var settings = ProjectSelection.Read(project.FilePath!, request);
            if (settings.ExcludedTest) { excludedTests.Add(project.FilePath!); continue; }
            var selection = settings.Framework;
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) { loadGaps.Add(new(GapKind.ProjectLoad, null, null, $"No compilation: {project.FilePath}", null)); continue; }
            var id = new CompilationId($"{Path.GetFullPath(project.FilePath!)}|{request.Configuration}|{selection}");
            var diagnostics = compilation.GetDiagnostics(cancellationToken);
            foreach (var error in diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error))
                loadGaps.Add(new(GapKind.Compilation, id, null, error.ToString(), null));
            var parse = (CSharpParseOptions)project.ParseOptions!;
            var fact = new CompilationFact(id, project.FilePath!, compilation.Assembly.Identity.ToString(), selection, request.Configuration,
                parse.LanguageVersion.ToString(), compilation.Options.NullableContextOptions.ToString(), parse.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToImmutableArray(),
                diagnostics.Select(x => x.ToString()).Order(StringComparer.Ordinal).ToImmutableArray())
            {
                ProjectReferences = [.. settings.ProjectReferences],
                NullableSetting = settings.Nullable,
                TreatWarningsAsErrors = settings.TreatWarningsAsErrors,
                WarningsAsErrors = [.. settings.WarningsAsErrors],
                WarningCount = diagnostics.Count(x => x.Severity == DiagnosticSeverity.Warning),
                ErrorCount = diagnostics.Count(x => x.Severity == DiagnosticSeverity.Error),
            };
            contexts.Add((project, compilation, fact));
        }
        if (contexts.Count == 0)
            throw new InvalidOperationException(excludedTests.Count > 0 ? "Only test projects were found; use --include-tests to analyse them." : "No C# projects could be loaded.");
        var extractor = new FactExtractor(contexts.Select(x => x.Fact).ToImmutableArray(), loadGaps);
        foreach (var context in contexts) await extractor.RegisterAsync(context.Project, context.Compilation, context.Fact, request.IncludeGenerated, cancellationToken);
        extractor.Extract(cancellationToken);
        return extractor.Snapshot() with { SourceRoot = Path.GetDirectoryName(input)!, ExcludedTestProjects = [.. excludedTests.Distinct().Order(StringComparer.Ordinal)] };
    }
}
