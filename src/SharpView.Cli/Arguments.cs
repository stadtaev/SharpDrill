using System.Collections.Immutable;
using SharpView.Core;
using SharpView.Roslyn;

namespace SharpView.Cli;

/// <summary>Which exceptions to list: every throw (default), or by fate along the call chains from roots.</summary>
internal enum ExceptionView { Throws, Uncaught, Caught, Miscaught }

internal sealed record Arguments(string Command, ICliCommand Handler, string Input)
{
    internal string Configuration { get; init; } = "Debug";
    internal string? Framework { get; init; }
    internal bool IncludeGenerated { get; init; } = true;
    internal bool IncludeTests { get; init; }
    internal AnalysisRequest Request => new(Input, Configuration, Framework, IncludeGenerated, IncludeTests);
    internal string? Root { get; init; }
    internal int MaxDepth { get; init; } = 20;
    internal int MaxPaths { get; init; } = 1000;
    internal int Budget { get; init; } = 100000;
    internal int MaxNodes { get; init; } = 200;
    internal int MaxEdges { get; init; } = 1000;
    internal bool Locations { get; init; }
    internal bool FollowCandidates { get; init; }
    internal bool DeclaredOnly { get; init; }
    internal ExceptionView View { get; init; }
    internal bool Mermaid { get; init; }
    internal bool Methods { get; init; }
    internal bool Json { get; init; }
    internal string? Output { get; init; }
    internal ImmutableHashSet<string> Given { get; init; } = [];

    internal const string Help = """
        SharpView — source-backed analysis of ASP.NET Core Web APIs (.NET SDK; restored inputs required)
        Usage: sharpview <command> <input.csproj|input.sln|input.slnx> [options]

        Commands:
          projects    Project reference tree with each project's framework, nullable settings and compiler warning count.
                      [--mermaid [--output F]]
          exceptions  Every explicit throw with its method, type and project. Or, along call chains from each action:
                        --uncaught   may escape the action: no catch on the way (one example path per origin)
                        --caught     reaches a matching catch: where it is thrown and where it is caught
                        --miscaught  passes through a try whose catch clauses do not match its type
                      [--uncaught | --caught | --miscaught] [--root R] [--budget N] [--declared-only] [--locations]
                      Interface/virtual calls are followed into their source implementations (results are marked
                      as possibilities); --declared-only stops at the declaration instead.
          reach       What each controller action can reach: every method once, with distance and shortest path.
                      [--root R] [--follow-candidates] [--max-depth N] [--budget N] [--locations]
          paths       Every call path from each controller action (or from any --root), with project and transitions.
                      [--root R] [--max-depth N] [--max-paths N] [--budget N] [--locations]
          diagram     Mermaid call graph of one controller action, saved as Markdown (prompts for an action without --root).
                      [--root R] [--follow-candidates] [--max-depth N] [--max-nodes N] [--max-edges N] [--budget N] [--output F]
          inspect     Extraction summary; --methods lists callables and their IDs; --json writes the fact snapshot (schema v3).
                      [--methods | --json [--output F]]

        Options:
          --root R               One root: an ID (from inspect --methods) or an exact signature. Defaults to every
                                 controller action. reach/paths accept any source method; exceptions/diagram need an action.
          --follow-candidates    reach/diagram: also follow implementation candidates of interface/virtual calls (marked, never runtime facts).
          --declared-only        exceptions: do not follow interface/virtual calls into source implementations.
          --max-depth N          reach/paths/diagram: call transitions (default 20), never runtime stack depth.
          --max-paths N          paths: reported paths (default 1000).
          --budget N             Examined call sites (default 100000). Raise it when the output says a limit was reached.
          --max-nodes N          diagram nodes, including boundaries (default 200).   --max-edges N  diagram edges (default 1000).
          --locations            Print call-site evidence for paths and witnesses.
          --mermaid              projects: also save the tree as a Mermaid diagram in Markdown.
          --output F             New file for inspect --json, diagram or projects --mermaid; existing files are never overwritten.
                                 Diagrams default to ./diagrams_exports in the current directory.
          --configuration NAME   Default Debug.   --framework TFM  Required for ambiguous multi-target inputs.
          --exclude-generated    Skip generated sources (included by default).
          --include-tests        Also load test projects (xUnit, NUnit, TUnit, MSTest, SpecFlow/Reqnroll, Pact...); skipped by default.
          --version              Print the version; no project load.
          --help                 This text; no project load.

        Exit: 0 complete, 1 usable but with gaps/Unknown/limits, 2 invalid request or fatal, 130 cancelled.
        """;

    internal static readonly ImmutableHashSet<string> LoadingOptions = ["--configuration", "--framework", "--exclude-generated", "--include-tests"];

    internal static Arguments? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args is ["-h"]) return null;
        if (args.Length < 2) throw new ArgumentException("Provide a command and input. Use --help.");
        var handler = Commands.Resolve(args[0]);
        if (args[1].StartsWith('-')) throw new ArgumentException("Provide an input path before options.");
        var parsed = new Arguments(args[0], handler, args[1]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
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
                "--follow-candidates" => parsed with { FollowCandidates = true },
                "--declared-only" => parsed with { DeclaredOnly = true },
                "--max-depth" => parsed with { MaxDepth = Number() },
                "--max-paths" => parsed with { MaxPaths = Number() },
                "--budget" => parsed with { Budget = Number() },
                "--max-nodes" => parsed with { MaxNodes = Number() },
                "--max-edges" => parsed with { MaxEdges = Number() },
                "--locations" => parsed with { Locations = true },
                "--uncaught" => parsed with { View = ExceptionView.Uncaught },
                "--caught" => parsed with { View = ExceptionView.Caught },
                "--miscaught" => parsed with { View = ExceptionView.Miscaught },
                "--mermaid" => parsed with { Mermaid = true },
                "--methods" => parsed with { Methods = true },
                "--json" => parsed with { Json = true },
                "--output" => parsed with { Output = Value() },
                "--configuration" => parsed with { Configuration = Value() },
                "--framework" => parsed with { Framework = Value() },
                "--exclude-generated" => parsed with { IncludeGenerated = false },
                "--include-tests" => parsed with { IncludeTests = true },
                _ => throw new ArgumentException($"Unknown option: {option}"),
            };
        }
        if (string.IsNullOrWhiteSpace(parsed.Configuration) || parsed.Framework is not null && string.IsNullOrWhiteSpace(parsed.Framework))
            throw new ArgumentException("Configuration/framework cannot be empty.");
        parsed = parsed with { Given = [.. seen] };
        handler.Validate(parsed);
        if (parsed.Output is not null && File.Exists(parsed.Output)) throw new ArgumentException("Output already exists; refusing to overwrite an input or other existing file.");
        return parsed;
    }

    private static readonly ImmutableHashSet<string> KnownOptions = ["--root", "--follow-candidates", "--declared-only", "--max-depth", "--max-paths",
        "--budget", "--max-nodes", "--max-edges", "--locations", "--uncaught", "--caught", "--miscaught", "--mermaid", "--methods", "--json", "--output"];
}
