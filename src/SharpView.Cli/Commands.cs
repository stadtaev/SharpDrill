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
        var exit = await arguments.Handler.RunAsync(index, arguments, token);
        SkillCommand.Hint(arguments);
        return exit;
    }
}
