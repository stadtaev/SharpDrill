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
        using var collection = new ProjectCollection(properties);
        var project = collection.LoadProject(path);
        // Checked before framework selection and restore, so an excluded test project never fails the load.
        if (!request.IncludeTests && IsTest(project)) return new("", "", false, [], [], ExcludedTest: true);
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
            [.. project.GetItems("ProjectReference").Select(x => Path.GetFullPath(Path.Combine(directory, x.EvaluatedInclude.Replace('\\', Path.DirectorySeparatorChar)))).Order(StringComparer.Ordinal)]);
    }

    // Microsoft.NET.Test.Sdk, MSTest and TUnit set IsTestProject; package families catch the rest (xunit.v3, NUnit3TestAdapter, Reqnroll.xUnit...).
    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "TUnit", "MSTest", "SpecFlow", "Reqnroll", "LightBDD", "Cucumber", "PactNet"];
    private static bool IsTest(Project project) =>
        string.Equals(project.GetPropertyValue("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
        || project.GetItems("PackageReference").Any(x => TestPackages.Any(p => x.EvaluatedInclude.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
}
