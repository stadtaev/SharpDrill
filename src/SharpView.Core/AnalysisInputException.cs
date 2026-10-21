namespace SharpView.Core;

/// <summary>The input cannot be analysed as given (no SDK, no loadable projects...): the user can act on the message.
/// Any other exception escaping the analysis is a SharpView bug.</summary>
public sealed class AnalysisInputException(string message, Exception? inner = null) : Exception(message, inner);
