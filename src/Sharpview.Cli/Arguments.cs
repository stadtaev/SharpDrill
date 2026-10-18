using System.Collections.Immutable;
using Sharpview.Core;
using Sharpview.Roslyn;

namespace Sharpview.Cli;

internal sealed record Arguments(string Command, ICliCommand Handler, string Input)
{
    internal string Configuration { get; init; } = "Debug";
    internal string? Framework { get; init; }
    internal bool IncludeGenerated { get; init; } = true;
    internal AnalysisRequest Request => new(Input, Configuration, Framework, IncludeGenerated);
    internal string? Root { get; init; }
    internal int MaxDepth { get; init; } = 20;
    internal int MaxPaths { get; init; } = 1000;
    internal int Budget { get; init; } = 100000;
    internal int MaxNodes { get; init; } = 200;
    internal int MaxEdges { get; init; } = 1000;
    internal bool Locations { get; init; }
    internal bool FollowCandidates { get; init; }
    internal bool Paths { get; init; }
    internal bool Methods { get; init; }
    internal bool Json { get; init; }
    internal string? Output { get; init; }
    internal ImmutableArray<LayerMapping> Layers { get; init; } = [];
    internal ImmutableArray<AllowedDependency> Allowed { get; init; } = [];
    internal ImmutableHashSet<string> Given { get; init; } = [];

    internal const string Help = """
        Sharpview — source-backed analysis of ASP.NET Core Web APIs (.NET SDK; restored inputs required)
        Usage: sharpview <command> <input.csproj|input.sln|input.slnx> [options]

        Commands:
          reach       What each controller action can reach: every method once, with distance, layer and shortest path.
                      [--root R] [--layer M]... [--follow-candidates] [--max-depth N] [--budget N] [--locations]
          paths       Every call path from each controller action (or from any --root), with project, layer, transitions.
                      [--root R] [--layer M]... [--max-depth N] [--max-paths N] [--budget N] [--locations]
          exceptions  Which explicit exceptions may escape each action, with one witness path per origin.
                      --paths lists each propagation path and its handlers instead.
                      [--root R] [--paths [--max-depth N] [--max-paths N]] [--budget N] [--locations]
          layers      Dependencies between your layers; with --allow, which ones break the rules.
                      --layer M [--layer M]... [--allow FROM=TO[,TO...]]...
          diagram     Mermaid call graph of one controller action, saved as Markdown (prompts for an action without --root).
                      [--root R] [--follow-candidates] [--max-depth N] [--max-nodes N] [--max-edges N] [--budget N] [--output F]
          inspect     Extraction summary; --methods lists callables and their IDs; --json writes the fact snapshot (schema v3).
                      [--methods | --json [--output F]]

        Options:
          --root R               One root: an ID (from inspect --methods) or an exact signature. Defaults to every
                                 controller action. reach/paths accept any source method; exceptions/diagram need an action.
          --layer M              project:<prefix>=<layer>, namespace:<prefix>=<layer> or type:<prefix>=<layer>.
                                 Longest prefix wins; project wins a tie.
          --allow FROM=TO,...    layers only. Allowed cross-layer dependencies; same-layer is always allowed.
          --follow-candidates    Also follow implementation candidates of interface/virtual calls (marked, never runtime facts).
          --max-depth N          Call transitions (default 20), never runtime stack depth.
          --max-paths N          Reported paths (default 1000).
          --budget N             Examined call sites (default 100000); also bounds retained exception-summary facts.
          --max-nodes N          diagram nodes, including boundaries (default 200).   --max-edges N  diagram edges (default 1000).
          --locations            Print call-site evidence for paths and witnesses.
          --output F             New file for inspect --json or diagram; existing files are never overwritten.
          --configuration NAME   Default Debug.   --framework TFM  Required for ambiguous multi-target inputs.
          --exclude-generated    Skip generated sources (included by default).
          --help                 This text; no project load.

        Exit: 0 complete, 1 usable but with gaps/Unknown/limits/violations, 2 invalid request or fatal, 130 cancelled.
        """;

    internal static readonly ImmutableHashSet<string> LoadingOptions = ["--configuration", "--framework", "--exclude-generated"];

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
            if (!seen.Add(option) && option is not ("--layer" or "--allow")) throw new ArgumentException($"Repeated option: {option}");
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {option}");
                return args[i];
            }
            int Number() => int.TryParse(Value(), out var n) ? n : throw new ArgumentException($"Invalid integer for {option}");
            parsed = option switch
            {
                "--root" => parsed with { Root = Value() },
                "--layer" => parsed with { Layers = parsed.Layers.Add(Layer(Value())) },
                "--allow" => parsed with { Allowed = parsed.Allowed.AddRange(Allow(Value())) },
                "--follow-candidates" => parsed with { FollowCandidates = true },
                "--max-depth" => parsed with { MaxDepth = Number() },
                "--max-paths" => parsed with { MaxPaths = Number() },
                "--budget" => parsed with { Budget = Number() },
                "--max-nodes" => parsed with { MaxNodes = Number() },
                "--max-edges" => parsed with { MaxEdges = Number() },
                "--locations" => parsed with { Locations = true },
                "--paths" => parsed with { Paths = true },
                "--methods" => parsed with { Methods = true },
                "--json" => parsed with { Json = true },
                "--output" => parsed with { Output = Value() },
                "--configuration" => parsed with { Configuration = Value() },
                "--framework" => parsed with { Framework = Value() },
                "--exclude-generated" => parsed with { IncludeGenerated = false },
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

    private static readonly ImmutableHashSet<string> KnownOptions = ["--root", "--layer", "--allow", "--follow-candidates", "--max-depth", "--max-paths",
        "--budget", "--max-nodes", "--max-edges", "--locations", "--paths", "--methods", "--json", "--output"];

    private static LayerMapping Layer(string value)
    {
        var colon = value.IndexOf(':'); var equals = value.IndexOf('=');
        if (colon < 1 || equals <= colon + 1 || equals == value.Length - 1 || value[..colon] is not ("project" or "namespace" or "type"))
            throw new ArgumentException("Layer must be project:<prefix>=<name>, namespace:<prefix>=<name> or type:<prefix>=<name>.");
        var scope = value[..colon] switch { "project" => LayerScope.Project, "namespace" => LayerScope.Namespace, _ => LayerScope.Type };
        return new(scope, value[(colon + 1)..equals], value[(equals + 1)..]);
    }

    private static IEnumerable<AllowedDependency> Allow(string rule)
    {
        var separator = rule.IndexOf('=');
        var targets = separator < 0 ? [] : rule[(separator + 1)..].Split(',');
        if (separator < 1 || targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Allow must be <from>=<to>[,<to>...].");
        return targets.Select(target => new AllowedDependency(rule[..separator], target));
    }
}
