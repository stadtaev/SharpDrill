using Microsoft.Build.Locator;
namespace SharpView.Roslyn;

public static class MSBuildBootstrap
{
    private static readonly object Gate = new();
    public static void RegisterSdk()
    {
        lock (Gate)
        {
            if (MSBuildLocator.IsRegistered) return;
            if (!MSBuildLocator.CanRegister) throw new InvalidOperationException("MSBuild was already loaded. Call MSBuildBootstrap.RegisterSdk before loading MSBuild types.");
            MSBuildLocator.RegisterDefaults();
        }
    }
}
public sealed record AnalysisRequest(string InputPath, string Configuration = "Debug", string? TargetFramework = null, bool IncludeGenerated = true);
