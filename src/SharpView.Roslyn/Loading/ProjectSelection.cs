using Microsoft.Build.Evaluation;
namespace SharpView.Roslyn;

// Evaluated MSBuild values for the selected framework; these are the project's real settings, not inferred ones.
internal sealed record ProjectSettings(string Framework, string Nullable, bool TreatWarningsAsErrors,
    IReadOnlyList<string> WarningsAsErrors, IReadOnlyList<string> ProjectReferences, bool ExcludedTest = false);

internal static class ProjectSelection
{
    public static ProjectSettings Read(string path, AnalysisRequest request)
    {
        var properties = new Dictionary<string, string> { ["Configuration"] = request.Configuration };
        // Checked before framework selection and restore, so an excluded test project never fails the load.
        if (!request.IncludeTests)
        {
            using var probe = new ProjectCollection(properties);
            if (IsTest(probe, path, [])) return new("", "", false, [], [], ExcludedTest: true);
        }
        using var collection = new ProjectCollection(properties);
        var project = collection.LoadProject(path);
        var multiple = project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var single = project.GetPropertyValue("TargetFramework");
        if (multiple.Length > 1 && request.TargetFramework is null)
            throw new ArgumentException($"Select --framework for {path}; choices: {string.Join(", ", multiple)}");
        var selected = request.TargetFramework ?? (multiple.FirstOrDefault() ?? single);
        if (request.TargetFramework is not null && !(multiple.Length > 0 ? multiple.Contains(selected) : single == selected))
            throw new ArgumentException($"Framework {selected} is not declared by {path}.");
        project.SetGlobalProperty("TargetFramework", selected);
        project.ReevaluateIfNecessary();
        // Analysis never restores input dependencies.
        var assets = project.GetPropertyValue("ProjectAssetsFile");
        if (string.IsNullOrEmpty(assets)) assets = Path.Combine(Path.GetDirectoryName(path)!, "obj", "project.assets.json");
        if (!Path.IsPathRooted(assets)) assets = Path.Combine(Path.GetDirectoryName(path)!, assets);
        if (!File.Exists(assets)) throw new ArgumentException($"Dependencies are not restored for {path}. Restore the input explicitly before analysis.");
        var directory = Path.GetDirectoryName(path)!;
        return new(selected, project.GetPropertyValue("Nullable"),
            string.Equals(project.GetPropertyValue("TreatWarningsAsErrors"), "true", StringComparison.OrdinalIgnoreCase),
            [.. project.GetPropertyValue("WarningsAsErrors").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            [.. project.GetItems("ProjectReference").Select(x => Resolve(directory, x.EvaluatedInclude)).Order(StringComparer.Ordinal)]);
    }

    private static string Resolve(string directory, string include) => Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));

    // A project is a test when it or anything it references looks like one: production code never references tests, and a
    // shared fixture library (Pact, say) brings the test packages in transitively.
    private static bool IsTest(ProjectCollection collection, string path, HashSet<string> seen) =>
        seen.Add(path) && Path.GetExtension(path) == ".csproj" && File.Exists(path) && Evaluations(collection, path).Any(p => LooksLikeTest(p)
            || p.GetItems("ProjectReference").Any(x => IsTest(collection, Resolve(p.DirectoryPath, x.EvaluatedInclude), seen)));

    // The outer evaluation of a multi-targeted project misses references conditioned on TargetFramework.
    private static IEnumerable<Project> Evaluations(ProjectCollection collection, string path)
    {
        var outer = collection.LoadProject(path);
        yield return outer;
        foreach (var framework in outer.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return collection.LoadProject(path, new Dictionary<string, string> { ["TargetFramework"] = framework }, null);
    }

    // Microsoft.NET.Test.Sdk, MSTest and TUnit set IsTestProject; package families catch the rest (xunit.v3, NUnit3TestAdapter, Reqnroll.xUnit...).
    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "TUnit", "MSTest", "SpecFlow", "Reqnroll", "LightBDD", "Cucumber", "PactNet"];
    private static bool LooksLikeTest(Project project) =>
        string.Equals(project.GetPropertyValue("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
        || project.GetItems("PackageReference").Any(x => TestPackages.Any(p => x.EvaluatedInclude.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
}
