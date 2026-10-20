using SharpView.Core;
using SharpView.Roslyn;

namespace SharpView.Cli;

internal static class Commands
{
    internal static ICliCommand Resolve(string name) => name switch
    {
        "projects" => new ProjectsCommand(),
        "exceptions" => new ExceptionsCommand(),
        "reach" => new ReachCommand(),
        "paths" => new PathsCommand(),
        "diagram" => new DiagramCommand(Console.In, Console.Out),
        "inspect" => new InspectCommand(),
        _ => throw new ArgumentException($"Unknown command: {name}. Use --help."),
    };

    internal static async Task<int> RunAsync(Arguments arguments, CancellationToken token)
    {
        var index = new ProgramIndex(await RoslynProgramLoader.LoadAsync(arguments.Request, token));
        if (index.Snapshot.ExcludedTestProjects is { Length: > 0 } tests)
            Console.Error.WriteLine($"Test projects excluded: {tests.Length} ({string.Join(", ", tests.Select(Path.GetFileNameWithoutExtension))}). Use --include-tests to analyse them.");
        return await arguments.Handler.RunAsync(index, arguments, token);
    }
}
