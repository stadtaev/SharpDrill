using System.Collections.Immutable;
using SharpView.Core;

namespace SharpView.Cli;

internal interface ICliCommand
{
    /// <summary>Options this command accepts besides the project-loading options; anything else is rejected before loading.</summary>
    ImmutableHashSet<string> Options { get; }

    /// <summary>Cross-option rules and limit checks, run after parsing and before the project is loaded.</summary>
    void Validate(Arguments arguments) { }

    Task<int> RunAsync(ProgramIndex index, Arguments arguments, CancellationToken token);
}
