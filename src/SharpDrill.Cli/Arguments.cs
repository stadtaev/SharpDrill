using System.Collections.Immutable;
using SharpDrill.Core;
using SharpDrill.Roslyn;

namespace SharpDrill.Cli;

/// <summary>Which exceptions to list: by fate along the call chains from entry points, or every throw in source.</summary>
internal enum ExceptionView { Uncaught, Caught, Miscaught, Thrown }

internal sealed record Arguments(string Command, ICliCommand Handler, string Input)
{
    internal string Configuration { get; init; } = "Debug";
    internal string? Framework { get; init; }
    internal bool IncludeGenerated { get; init; } = true;
    internal bool IncludeTests { get; init; }
    internal bool Restore { get; init; }
    internal bool NoRestore { get; init; }
    internal bool NoCache { get; init; }
    internal AnalysisRequest Request => new(Input, Configuration, Framework, IncludeGenerated, IncludeTests);
    internal string? From { get; init; }
    internal int MaxDepth { get; init; } = 20;
    internal int Budget { get; init; } = 100000;
    internal int MaxNodes { get; init; } = 200;
    internal int MaxEdges { get; init; } = 1000;
    internal bool Locations { get; init; }
    internal bool DeclaredOnly { get; init; }
    internal bool ThirdParty { get; init; }
    // Null: the command's default view (exceptions: uncaught; with --format msbuild, uncaught and miscaught).
    internal ExceptionView? View { get; init; }
    internal bool Mermaid { get; init; }
    internal bool MSBuild { get; init; }
    internal string? Output { get; init; }
    internal ImmutableHashSet<string> Given { get; init; } = [];

    internal const string Help = """
        SharpDrill — source-backed analysis of .NET code: exception flow, call diagrams and the project map.
        Usage: sharpdrill <command> [view] [input.sln|input.slnx|input.csproj|directory] [options]
               Without an input, the solution (or else the only project) in the current directory.

        Commands:
          exceptions [view]  Exceptions along the call chains from each entry point: controller actions, Main or top-level
                             statements, and the public methods of a library project that nothing loaded references.
                               uncaught   (default) may leave an entry point: no catch on the way
                               caught     reaches a matching catch: where, and what the handler does
                               miscaught  passes through a try whose catch clauses do not match it
                               thrown     every explicit throw in source
                             [--from E] [--third-party] [--declared-only] [--locations] [--format msbuild] [--budget N]
          diagram            Mermaid call graph of one entry point, saved as Markdown (asks which one without --from).
                             [--from E] [--declared-only] [--max-depth N] [--max-nodes N] [--max-edges N] [--output F]
          projects           Project reference tree with each project's framework, nullable settings and warning count.
                             [--mermaid [--output F]]
          skill              Install the agent skill in .github/skills and .claude/skills at the repository root, or with
                             --user in ~/.copilot/skills and ~/.claude/skills. No project load.

        Options:
          --from E           One entry point: its ID, signature or a unique part of it, e.g. OrdersController.Get. Default: all.
          --third-party      exceptions uncaught: also list calls into NuGet packages and DLLs that no catch meets on the way
                             (not .NET itself, logging or Microsoft.Extensions).
          --declared-only    Stop at interface/virtual calls instead of following them into their source implementations
                             (followed steps are possibilities, never runtime facts).
          --locations        exceptions: file:line for every step of each path.
          --format msbuild   exceptions: one build warning per finding, uncaught and miscaught unless a view is given.
                             SD0001 may escape an entry point, SD0002 may escape to a global exception handler, SD0003 miscaught,
                             SD0004 third-party call with no catch on the way, SD0005 leaves an async void method.
          --budget N         Analysis work limit (default 100000); raise it when the output says the run stopped early.
          --max-depth N      diagram: call steps (default 20).  --max-nodes N (default 200).  --max-edges N (default 1000).
          --mermaid          projects: also save the tree as a Mermaid diagram in Markdown.
          --output F         New file for diagram or projects --mermaid; never overwrites. Default: ./diagrams_exports.

        Loading:
          --configuration NAME (default Debug), --framework TFM (for multi-target inputs), --exclude-generated,
          --include-tests (test projects are skipped by default), --restore (always restore first; by default only when
          packages are missing), --no-restore, --no-cache (analyse again even if nothing changed).
          --version, --help: no project load.

        Exit: 0 complete, 1 usable but with gaps/Unknown/limits, 2 invalid request or fatal, 130 cancelled.
        """;

    internal static readonly ImmutableHashSet<string> LoadingOptions = ["--configuration", "--framework", "--exclude-generated", "--include-tests", "--restore", "--no-restore", "--no-cache"];

    internal static Arguments? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args is ["-h"]) return null;
        var handler = Commands.Resolve(args[0]);
        string? input = null;
        var parsed = new Arguments(args[0], handler, "");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            // Words are a view the command knows, else the input; at most one of each.
            if (!option.StartsWith('-'))
            {
                if (handler.Views.TryGetValue(option, out var view))
                    parsed = parsed.View is null ? parsed with { View = view } : throw new ArgumentException($"Choose one view: {string.Join(", ", handler.Views.Keys.Order(StringComparer.Ordinal))}.");
                else input = input is null ? option : throw new ArgumentException($"Unexpected argument: {option}. Pass one input.");
                continue;
            }
            if (!handler.Options.Contains(option) && !LoadingOptions.Contains(option))
                throw new ArgumentException(KnownOptions.Contains(option) ? $"{args[0]} does not accept {option}." : $"Unknown option: {option}");
            if (!seen.Add(option)) throw new ArgumentException($"Repeated option: {option}");
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {option}");
                return args[i];
            }
            int Number() => int.TryParse(Value(), out var n) ? n : throw new ArgumentException($"Invalid integer for {option}");
            parsed = option switch
            {
                "--from" => parsed with { From = Value() },
                "--declared-only" => parsed with { DeclaredOnly = true },
                "--third-party" => parsed with { ThirdParty = true },
                "--max-depth" => parsed with { MaxDepth = Number() },
                "--budget" => parsed with { Budget = Number() },
                "--max-nodes" => parsed with { MaxNodes = Number() },
                "--max-edges" => parsed with { MaxEdges = Number() },
                "--locations" => parsed with { Locations = true },
                "--mermaid" => parsed with { Mermaid = true },
                "--format" => parsed with { MSBuild = Value() == "msbuild" ? true : throw new ArgumentException("--format accepts only msbuild.") },
                "--output" => parsed with { Output = Value() },
                "--configuration" => parsed with { Configuration = Value() },
                "--framework" => parsed with { Framework = Value() },
                "--exclude-generated" => parsed with { IncludeGenerated = false },
                "--include-tests" => parsed with { IncludeTests = true },
                "--restore" => parsed with { Restore = true },
                "--no-restore" => parsed with { NoRestore = true },
                "--no-cache" => parsed with { NoCache = true },
                _ => throw new ArgumentException($"Unknown option: {option}"),
            };
        }
        if (string.IsNullOrWhiteSpace(parsed.Configuration) || parsed.Framework is not null && string.IsNullOrWhiteSpace(parsed.Framework))
            throw new ArgumentException("Configuration/framework cannot be empty.");
        parsed = parsed with { Given = [.. seen] };
        handler.Validate(parsed);
        if (parsed.Restore && parsed.NoRestore) throw new ArgumentException("--restore and --no-restore cannot be combined.");
        if (parsed.Output is not null && File.Exists(parsed.Output)) throw new ArgumentException("Output already exists; refusing to overwrite an input or other existing file.");
        return parsed with { Input = ResolveInput(input ?? Directory.GetCurrentDirectory()) };
    }

    /// <summary>A directory stands for its one solution, or else its one project; anything else is left to the loader to judge.</summary>
    internal static string ResolveInput(string path)
    {
        if (!Directory.Exists(path)) return path;
        // Filter by extension: on Windows the pattern *.sln also matches .slnx.
        string[] Find(params string[] extensions) => [.. Directory.EnumerateFiles(path).Where(x => extensions.Contains(Path.GetExtension(x))).Order(StringComparer.Ordinal)];
        var candidates = Find(".sln", ".slnx") is { Length: > 0 } solutions ? solutions : Find(".csproj");
        return candidates switch
        {
            [var single] => single,
            [] => throw new ArgumentException($"No .sln, .slnx or .csproj in {Path.GetFullPath(path)}. Pass the input path."),
            _ => throw new ArgumentException($"Several inputs in {Path.GetFullPath(path)}: {string.Join(", ", candidates.Select(Path.GetFileName))}. Pass one."),
        };
    }

    private static readonly ImmutableHashSet<string> KnownOptions = ["--from", "--declared-only", "--third-party", "--max-depth",
        "--budget", "--max-nodes", "--max-edges", "--locations", "--mermaid", "--format", "--output"];
}
