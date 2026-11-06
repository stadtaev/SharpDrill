using System.Collections.Immutable;
using SharpDrill.Core;
using SharpDrill.Roslyn;

namespace SharpDrill.Cli;

/// <summary>Which exceptions to list: every throw (default), or by fate along the call chains from roots.</summary>
internal enum ExceptionView { Throws, Uncaught, Caught, Miscaught }

internal sealed record Arguments(string Command, ICliCommand Handler, string Input)
{
    internal string Configuration { get; init; } = "Debug";
    internal string? Framework { get; init; }
    internal bool IncludeGenerated { get; init; } = true;
    internal bool IncludeTests { get; init; }
    internal bool Restore { get; init; }
    internal AnalysisRequest Request => new(Input, Configuration, Framework, IncludeGenerated, IncludeTests);
    internal string? Root { get; init; }
    internal int MaxDepth { get; init; } = 20;
    internal int Budget { get; init; } = 100000;
    internal int MaxNodes { get; init; } = 200;
    internal int MaxEdges { get; init; } = 1000;
    internal bool Locations { get; init; }
    internal bool DeclaredOnly { get; init; }
    internal bool Strict { get; init; }
    internal bool Verbose { get; init; }
    internal ExceptionView View { get; init; }
    internal bool Mermaid { get; init; }
    internal bool MSBuild { get; init; }
    internal string? Output { get; init; }
    internal ImmutableHashSet<string> Given { get; init; } = [];

    internal const string Help = """
        SharpDrill — source-backed analysis of ASP.NET Core Web APIs (.NET SDK; restored inputs, or --restore)
        Usage: sharpdrill <command> [input.csproj|input.sln|input.slnx|directory] [options]
               Without an input, the solution (or else the single project) in the current directory.

        Commands:
          projects    Project reference tree with each project's framework, nullable settings and compiler warning count.
                      [--mermaid [--output F]]
          exceptions  Every explicit throw with its type, method and location. Or, along call chains from each action:
                        --uncaught   may escape the action: no catch on the way (one example path per origin)
                        --caught     reaches a matching catch: where it is thrown and where it is caught
                        --miscaught  passes through a try whose catch clauses do not match its type
                      Findings covered by exception middleware or a global exception filter are listed last.
                      [--uncaught [--strict [--verbose]] | --caught | --miscaught] [--root R] [--budget N] [--declared-only] [--locations]
                      [--format msbuild]
          diagram     Mermaid call graph of one controller action, saved as Markdown (prompts for an action without --root).
                      [--root R] [--declared-only] [--max-depth N] [--max-nodes N] [--max-edges N] [--budget N] [--output F]
          skill       Install the agent skill for GitHub Copilot and Claude Code: .github/skills and .claude/skills (sharpdrill/SKILL.md)
                      at the repository root, or with --user in ~/.copilot/skills and ~/.claude/skills. No project load.
                      [--user]

        Options:
          --root R               One controller action, by its full signature (namespace, type, method, parameter types, e.g.
                                 Api.OrdersController.Get(int); diagram without --root lists them). Defaults to every action.
          --declared-only        exceptions/diagram: stop at interface/virtual calls instead of following them into their
                                 source implementations (followed by default; such steps are possibilities, never runtime facts).
          --strict               exceptions --uncaught: also list calls into third-party libraries (NuGet packages, referenced
                                 DLLs; not .NET itself) that no catch meets on the way from the action, in a section of their own.
          --max-depth N          diagram: call transitions (default 20), never runtime stack depth.
          --budget N             Examined call sites (default 100000). Raise it when the output says a limit was reached.
          --max-nodes N          diagram nodes, including boundaries (default 200).   --max-edges N  diagram edges (default 1000).
          --locations            exceptions: print call-site evidence for witnesses.
          --verbose              exceptions --strict: also list logging (Serilog, NLog, log4net) and Microsoft.Extensions calls;
                                 Microsoft.Extensions.Caching is always listed.
          --format msbuild       exceptions: one build warning per finding, "file(line,col): warning SD000N: ...", for MSBuild,
                                 Visual Studio and VS Code (what the SharpDrill.MSBuild package runs after each build). Without a view:
                                 uncaught and miscaught. SD0001 may escape an action, SD0002 may escape to a global exception
                                 handler, SD0003 miscaught, SD0004 third-party call with no catch on the way (--strict),
                                 SD0005 leaves an async void method (no caller can catch it; the process ends).
          --mermaid              projects: also save the tree as a Mermaid diagram in Markdown.
          --output F             New file for diagram or projects --mermaid; existing files are never overwritten.
                                 Diagrams default to ./diagrams_exports in the current directory.
          --configuration NAME   Default Debug.   --framework TFM  Required for ambiguous multi-target inputs.
          --exclude-generated    Skip generated sources (included by default).
          --restore              Run dotnet restore on the input first (never done otherwise); its output goes to stderr.
          --include-tests        Also load test projects (xUnit, NUnit, TUnit, MSTest, SpecFlow/Reqnroll, Pact...); skipped by default.
          --version              Print the version; no project load.
          --help                 This text; no project load.

        Exit: 0 complete, 1 usable but with gaps/Unknown/limits, 2 invalid request or fatal, 130 cancelled.
        """;

    internal static readonly ImmutableHashSet<string> LoadingOptions = ["--configuration", "--framework", "--exclude-generated", "--include-tests", "--restore"];

    internal static Arguments? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args is ["-h"]) return null;
        var handler = Commands.Resolve(args[0]);
        var input = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
        var parsed = new Arguments(args[0], handler, input ?? "");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = input is null ? 1 : 2; i < args.Length; i++)
        {
            var option = args[i];
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
                "--root" => parsed with { Root = Value() },
                "--declared-only" => parsed with { DeclaredOnly = true },
                "--strict" => parsed with { Strict = true },
                "--verbose" => parsed with { Verbose = true },
                "--max-depth" => parsed with { MaxDepth = Number() },
                "--budget" => parsed with { Budget = Number() },
                "--max-nodes" => parsed with { MaxNodes = Number() },
                "--max-edges" => parsed with { MaxEdges = Number() },
                "--locations" => parsed with { Locations = true },
                "--uncaught" => parsed with { View = ExceptionView.Uncaught },
                "--caught" => parsed with { View = ExceptionView.Caught },
                "--miscaught" => parsed with { View = ExceptionView.Miscaught },
                "--mermaid" => parsed with { Mermaid = true },
                "--format" => parsed with { MSBuild = Value() == "msbuild" ? true : throw new ArgumentException("--format accepts only msbuild.") },
                "--output" => parsed with { Output = Value() },
                "--configuration" => parsed with { Configuration = Value() },
                "--framework" => parsed with { Framework = Value() },
                "--exclude-generated" => parsed with { IncludeGenerated = false },
                "--include-tests" => parsed with { IncludeTests = true },
                "--restore" => parsed with { Restore = true },
                _ => throw new ArgumentException($"Unknown option: {option}"),
            };
        }
        if (string.IsNullOrWhiteSpace(parsed.Configuration) || parsed.Framework is not null && string.IsNullOrWhiteSpace(parsed.Framework))
            throw new ArgumentException("Configuration/framework cannot be empty.");
        parsed = parsed with { Given = [.. seen] };
        handler.Validate(parsed);
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

    private static readonly ImmutableHashSet<string> KnownOptions = ["--root", "--declared-only", "--strict", "--verbose", "--max-depth",
        "--budget", "--max-nodes", "--max-edges", "--locations", "--uncaught", "--caught", "--miscaught", "--mermaid", "--format", "--output"];
}
