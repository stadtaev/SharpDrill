using Microsoft.Build.Evaluation;
namespace Sharpview.Roslyn;

internal static class ProjectSelection
{
    public static string Read(string path, AnalysisRequest request)
    {
        var properties = new Dictionary<string, string> { ["Configuration"] = request.Configuration };
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
        return selected;
    }
}
