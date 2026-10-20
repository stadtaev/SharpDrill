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
/// <param name="IncludeTests">Test projects (xUnit, NUnit, TUnit, MSTest, BDD, Pact...) are skipped unless true.</param>
public sealed record AnalysisRequest(string InputPath, string Configuration = "Debug", string? TargetFramework = null, bool IncludeGenerated = true, bool IncludeTests = false);
