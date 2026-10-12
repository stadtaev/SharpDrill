using System.Collections.Immutable;
using Sharpview.Core;
using Sharpview.Roslyn;

namespace Sharpview.Cli;

internal sealed record Arguments(string Command, AnalysisRequest Request, ChainOptions Traversal,
    string? Root, string? RootId, string Format, string? Output, bool Locations, ImmutableArray<LayerMapping> Layers,
    ImmutableArray<AllowedDependency> Allowed, bool FollowCandidates = false)
{
    internal const string Help = """
        Sharpview — source-backed C# research (.NET SDK; restored inputs required)
        Usage: sharpview <methods|chains|controllers|exceptions|reach|layers|inspect> <input.csproj|input.sln|input.slnx> [options]
          --root <exact signature> | --root-id <id>  One source callable; defaults to all source callables
                                                     (controllers/exceptions/reach default to controller actions).
          --dispatch declared|direct                 Default declared; uncertain dispatch ends a path.
          --max-depth <n>                            Call transitions, default 20; never runtime stack depth.
          --max-chains <n>                           Global result budget, default 1000.
          --max-expansions <n>                       Global call-site budget, default 100000.
          --layer project:<prefix>=<layer>           Repeatable explicit architectural mapping.
          --layer namespace:<prefix>=<layer>         Longest prefix wins; project wins a tie. Namespace prefixes
                                                     match that namespace and nested namespaces.
          --layer type:<prefix>=<layer>              Fully qualified type (and its nested types).
          --allow <from>=<to>[,<to>...]              layers only; repeatable allowed cross-layer dependencies.
                                                     Without --allow, layers prints the overview only.
          --configuration <name>                    Default Debug.
          --framework <tfm>                         Required for ambiguous multi-target inputs.
          --exclude-generated                       Include generated sources by default.
          --locations                               Print call-site evidence alongside chains and reach witnesses.
          --follow-candidates                       reach only; also traverse implementation candidates of
                                                     interface/virtual calls (marked candidate, never runtime facts).
          --format text|json                        inspect only; default text, JSON schema version 3.
          --output <new path>                       inspect only; refuses to overwrite existing files.
          --help                                    No project load.
        Static paths preserve declared bindings; no interface/virtual implementation guesses.
        Explicit roots may inspect a virtual declaration's own body. Traversed virtual calls stop.
        Exception results concern controller actions; middleware evidence is reported separately.
        reach lists each callable reachable from a root once, with one shortest witness path.
        layers aggregates call and type dependencies between configured layers; implementation candidates
        are reported separately and never treated as runtime targets.
        Minimal APIs and inherited action enumeration are outside this initial increment.
        Exit: 0 complete, 1 usable but incomplete/gaps/limits, 2 invalid/fatal, 130 cancelled.
        """;

    internal static Arguments? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args is ["-h"]) return null;
        if (args.Length < 2) throw new ArgumentException("Provide a command and input. Use --help.");
        var command = args[0];
        if (command is not ("methods" or "chains" or "controllers" or "exceptions" or "reach" or "layers" or "inspect"))
            throw new ArgumentException($"Unknown command: {command}");
        var input = args[1];
        if (input.StartsWith('-')) throw new ArgumentException("Provide an input path before options.");
        string? root = null, rootId = null, framework = null, output = null;
        var configuration = "Debug"; var format = "text"; var generated = true; var locations = false; var candidates = false;
        var dispatch = ChainDispatchPolicy.Declared; var depth = 20; var chains = 1000; var expansions = 100000;
        var layers = ImmutableArray.CreateBuilder<LayerMapping>();
        var allowed = ImmutableArray.CreateBuilder<AllowedDependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--layer" or "--allow") && !seen.Add(option)) throw new ArgumentException($"Repeated option: {option}");
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {option}");
                return args[i];
            }
            int Number() => int.TryParse(Value(), out var n) ? n : throw new ArgumentException($"Invalid integer for {option}");
            switch (option)
            {
                case "--root": root = Value(); break;
                case "--root-id": rootId = Value(); break;
                case "--configuration": configuration = Value(); break;
                case "--framework": framework = Value(); break;
                case "--exclude-generated": generated = false; break;
                case "--locations": locations = true; break;
                case "--follow-candidates": candidates = true; break;
                case "--output": output = Value(); break;
                case "--format": format = Value(); if (format is not ("text" or "json")) throw new ArgumentException("Format must be text or json."); break;
                case "--dispatch": dispatch = Value() switch { "declared" => ChainDispatchPolicy.Declared, "direct" => ChainDispatchPolicy.Direct, _ => throw new ArgumentException("Dispatch must be declared or direct.") }; break;
                case "--max-depth": depth = Number(); break;
                case "--max-chains": chains = Number(); break;
                case "--max-expansions": expansions = Number(); break;
                case "--layer":
                    var value = Value(); var colon = value.IndexOf(':'); var equals = value.IndexOf('=');
                    if (colon < 1 || equals <= colon + 1 || equals == value.Length - 1 || value[..colon] is not ("project" or "namespace" or "type"))
                        throw new ArgumentException("Layer must be project:<prefix>=<name>, namespace:<prefix>=<name> or type:<prefix>=<name>.");
                    layers.Add(new(value[..colon] switch { "project" => LayerScope.Project, "namespace" => LayerScope.Namespace, _ => LayerScope.Type }, value[(colon + 1)..equals], value[(equals + 1)..])); break;
                case "--allow":
                    var rule = Value(); var separator = rule.IndexOf('=');
                    var targets = separator < 0 ? [] : rule[(separator + 1)..].Split(',');
                    if (separator < 1 || targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException("Allow must be <from>=<to>[,<to>...].");
                    foreach (var target in targets) allowed.Add(new(rule[..separator], target)); break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }
        }
        if (root is not null && rootId is not null) throw new ArgumentException("--root and --root-id are mutually exclusive.");
        if (command is "methods" or "inspect" && (root is not null || rootId is not null || locations || layers.Count > 0 || seen.Overlaps(["--dispatch", "--max-depth", "--max-chains", "--max-expansions"])))
            throw new ArgumentException("Root, traversal, layer and location options require chains, controllers or exceptions.");
        if (command != "inspect" && (seen.Contains("--format") || output is not null)) throw new ArgumentException("--format and --output require inspect.");
        if (command == "layers" && (root is not null || rootId is not null || locations || seen.Overlaps(["--dispatch", "--max-depth", "--max-chains", "--max-expansions"])))
            throw new ArgumentException("layers accepts only --layer, --allow and project-loading options.");
        if (command == "layers" && layers.Count == 0) throw new ArgumentException("layers requires at least one --layer mapping.");
        if (command != "layers" && allowed.Count > 0) throw new ArgumentException("--allow requires layers.");
        if (command == "reach" && seen.Overlaps(["--dispatch", "--max-chains"])) throw new ArgumentException("reach accepts --max-depth and --max-expansions but not --dispatch or --max-chains.");
        if (command != "reach" && candidates) throw new ArgumentException("--follow-candidates requires reach.");
        if (string.IsNullOrWhiteSpace(configuration) || framework is not null && string.IsNullOrWhiteSpace(framework)) throw new ArgumentException("Configuration/framework cannot be empty.");
        var traversal = new ChainOptions(dispatch, depth, chains, expansions); traversal.Validate();
        if (output is not null && File.Exists(output)) throw new ArgumentException("Output already exists; refusing to overwrite an input or other existing file.");
        return new(command, new(input, configuration, framework, generated), traversal, root, rootId, format, output, locations, layers.ToImmutable(), allowed.ToImmutable(), candidates);
    }
}
