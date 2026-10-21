using SharpView.Cli;
using SharpView.Core;
using SharpView.Roslyn;

return await EntryPoint.RunAsync(args);

internal static class EntryPoint
{
    // The package version (set by the release pipeline), without the source-revision suffix the SDK appends.
    internal static string Version { get; } = (typeof(EntryPoint).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    internal static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args is ["--version"]) { Console.WriteLine(Version); return 0; }
            var arguments = Arguments.Parse(args);
            if (arguments is null) { Console.WriteLine(Arguments.Help); return 0; }
            MSBuildBootstrap.RegisterSdk();
            return await Commands.RunAsync(arguments, cancellation.Token);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Analysis cancelled."); return 130; }
        // A wrong request or an input that cannot be analysed: the message says what to do.
        catch (Exception e) when (e is ArgumentException or AnalysisInputException or FileNotFoundException or IOException) { Console.Error.WriteLine($"SharpView: {e.Message}"); return 2; }
        catch (Exception e)
        {
            BugReport.Write("The analysis stopped.", [$"command: {string.Join(' ', args.Take(1).Concat(args.Skip(2)))}", .. e.ToString().Split('\n').Select(x => x.TrimEnd())]);
            return 2;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
