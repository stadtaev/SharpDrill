using Sharpview.Core;
using Sharpview.Roslyn;

namespace Sharpview.Cli;

internal static class Commands
{
    internal static ICliCommand Resolve(string name) => name switch
    {
        "reach" => new ReachCommand(),
        "paths" => new PathsCommand(),
        "exceptions" => new ExceptionsCommand(),
        "layers" => new LayersCommand(),
        "diagram" => new DiagramCommand(Console.In, Console.Out),
        "inspect" => new InspectCommand(),
        _ => throw new ArgumentException($"Unknown command: {name}. Use --help."),
    };

    internal static async Task<int> RunAsync(Arguments arguments, CancellationToken token)
    {
        var index = new ProgramIndex(await RoslynProgramLoader.LoadAsync(arguments.Request, token));
        return await arguments.Handler.RunAsync(index, arguments, token);
    }
}
