using SharpDrill.Core;
using SharpDrill.Roslyn;

namespace SharpDrill.Cli;

internal static class Commands
{
    internal static ICliCommand Resolve(string name) => name switch
    {
        "projects" => new ProjectsCommand(),
        "exceptions" => new ExceptionsCommand(),
        "diagram" => new DiagramCommand(Console.In, Console.Out),
        _ => throw new ArgumentException($"Unknown command: {name}. Use --help."),
    };

    internal static async Task<int> RunAsync(Arguments arguments, CancellationToken token)
    {
        var index = new ProgramIndex(await SnapshotAsync(arguments, token));
        var exit = await arguments.Handler.RunAsync(index, arguments, token);
        SkillCommand.Hint(arguments);
        return exit;
    }

    private static async Task<ProgramSnapshot> SnapshotAsync(Arguments arguments, CancellationToken token)
    {
        if (arguments.Restore) await Restore.RunAsync(arguments, token);
        if (!arguments.NoCache && SnapshotCache.Read(arguments.Request) is { } cached) return cached;
        var snapshot = await LoadRestoringAsync(arguments, token);
        SnapshotCache.Write(arguments.Request, snapshot);
        return snapshot;
    }

    // Restore only when the load finds it missing: it writes obj/ and may reach package feeds, and a restored input needs neither.
    private static async Task<ProgramSnapshot> LoadRestoringAsync(Arguments arguments, CancellationToken token)
    {
        try { return await RoslynProgramLoader.LoadAsync(arguments.Request, token); }
        catch (NotRestoredException) when (!arguments.Restore && !arguments.NoRestore)
        {
            Console.Error.WriteLine($"Restoring {Path.GetFileName(arguments.Input)}...");
            await Restore.RunAsync(arguments, token);
            return await RoslynProgramLoader.LoadAsync(arguments.Request, token);
        }
    }
}
