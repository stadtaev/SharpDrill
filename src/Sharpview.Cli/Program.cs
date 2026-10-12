using Sharpview.Cli;
using Sharpview.Roslyn;

return await EntryPoint.RunAsync(args);

internal static class EntryPoint
{
    internal static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = Arguments.Parse(args);
            if (options is null) { Console.WriteLine(Arguments.Help); return 0; }
            MSBuildBootstrap.RegisterSdk();
            return await Commands.RunAsync(options, cancellation.Token);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Analysis cancelled."); return 130; }
        catch (Exception e) { Console.Error.WriteLine($"Sharpview: {e.Message}"); return 2; }
        finally { Console.CancelKeyPress -= handler; }
    }
}
