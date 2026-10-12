using Sharpview.Core;
using Sharpview.Roslyn;
using Xunit;

namespace Sharpview.Tests;

public sealed class LoaderTests
{
    [Fact]
    public async Task Project_input_includes_source_references_and_cross_project_bindings()
    {
        var input = Path.Combine(ResearchFixture.Root, "tests/Fixtures/ResearchSolution/Research.Api/Research.Api.csproj");
        var snapshot = await RoslynProgramLoader.LoadAsync(new(input));
        AssertResearchBindings(snapshot);
        Assert.Equal(3, snapshot.Compilations.Length);
        Assert.All(snapshot.Compilations, x => Assert.Equal("net10.0", x.TargetFramework));
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(input)), snapshot.SourceRoot);
        Assert.DoesNotContain(snapshot.Gaps, x => x.Kind is GapKind.ProjectLoad or GapKind.Compilation);
    }

    [Fact]
    public async Task Conventional_solution_input_preserves_cross_project_source_facts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpview-solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "Fixture.sln");
        try
        {
            var projects = new[]
            {
                (Name: "Research.Api", Id: "{10C01B48-A0C0-4000-A000-000000000001}"),
                (Name: "Research.Application", Id: "{10C01B48-A0C0-4000-A000-000000000002}"),
                (Name: "Research.Infrastructure", Id: "{10C01B48-A0C0-4000-A000-000000000003}")
            };
            var lines = new List<string> { "Microsoft Visual Studio Solution File, Format Version 12.00" };
            foreach (var project in projects)
            {
                var path = Path.Combine(ResearchFixture.Root, "tests/Fixtures/ResearchSolution", project.Name, project.Name + ".csproj");
                lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{project.Name}\", \"{path}\", \"{project.Id}\"");
                lines.Add("EndProject");
            }
            lines.Add("Global");
            lines.Add("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            lines.Add("\t\tDebug|Any CPU = Debug|Any CPU");
            lines.Add("\tEndGlobalSection");
            lines.Add("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach (var project in projects)
            {
                lines.Add($"\t\t{project.Id}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
                lines.Add($"\t\t{project.Id}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            }
            lines.Add("\tEndGlobalSection");
            lines.Add("EndGlobal");
            await File.WriteAllLinesAsync(input, lines);
            var snapshot = await RoslynProgramLoader.LoadAsync(new(input));
            AssertResearchBindings(snapshot);
            Assert.Equal(3, snapshot.Compilations.Length);
            Assert.DoesNotContain(snapshot.Gaps, x => x.Kind is GapKind.ProjectLoad or GapKind.Compilation);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertResearchBindings(ProgramSnapshot snapshot)
    {
        var action = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Research.Api.OrdersController.Get()");
        var service = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Research.Application.Service.Load()");
        var repository = Assert.Single(snapshot.Callables, x => x.DisplaySignature == "Research.Infrastructure.Repository.Read()");
        Assert.Equal(service.Id, Assert.Single(snapshot.CallSites, x => x.Caller == action.Id).BoundTarget);
        Assert.Equal(repository.Id, Assert.Single(snapshot.CallSites, x => x.Caller == service.Id).BoundTarget);
        Assert.Equal(BodyStatus.Source, repository.Body);
        Assert.NotEqual(action.Compilation, service.Compilation);
        Assert.NotEqual(service.Compilation, repository.Compilation);
        Assert.Contains(snapshot.ControllerActions, x => x.Method == action.Id);
        Assert.Contains(snapshot.ThrowSites, x => x.Owner == repository.Id && x.StaticExceptionType?.DisplayName == "Research.Infrastructure.StorageException");
    }
}
