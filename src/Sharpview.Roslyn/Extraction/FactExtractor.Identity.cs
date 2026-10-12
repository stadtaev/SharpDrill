using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sharpview.Core;

namespace Sharpview.Roslyn;

// Callable/type identity: canonical symbols, local IDs, display signatures and portable keys.
internal sealed partial class FactExtractor
{
    internal static IMethodSymbol Canonical(IMethodSymbol s) => (s.ReducedFrom ?? s).OriginalDefinition.PartialImplementationPart ?? (s.ReducedFrom ?? s).OriginalDefinition;
    private static string Key(IMethodSymbol symbol)
    {
        var s = Canonical(symbol);
        // Roslyn's documentation IDs for lambdas ("M:Ns.Type." + parameters) and local functions ("M:Ns.Type.Name") are only
        // unique per type, so same-shaped ones in different members would share one fact; identify them by position instead.
        var doc = s.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction ? null : s.GetDocumentationCommentId();
        if (doc is null)
        {
            var syntax = s.DeclaringSyntaxReferences.FirstOrDefault();
            doc = $"{s.ContainingSymbol}|{syntax?.SyntaxTree.FilePath}:{syntax?.Span.Start}|{s.MetadataName}";
        }
        return $"{s.ContainingAssembly.Identity}|{doc}";
    }
    internal static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat).Replace("global::", "", StringComparison.Ordinal);
    private static string Signature(IMethodSymbol symbol)
    {
        var s = Canonical(symbol);
        var name = s.MethodKind == MethodKind.Constructor ? ".ctor" : s.MethodKind == MethodKind.StaticConstructor ? ".cctor" : s.Name;
        if (s.TypeParameters.Length > 0) name += "<" + string.Join(", ", s.TypeParameters.Select(x => x.Name)) + ">";
        var prefix = s.ContainingType is null ? "" : TypeName(s.ContainingType) + ".";
        if (s.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction)
            prefix = s.ContainingSymbol.ToDisplayString() + ".";
        return prefix + name + "(" + string.Join(", ", s.Parameters.Select(x => (x.RefKind == RefKind.None ? "" : x.RefKind.ToString().ToLowerInvariant() + " ") + TypeName(x.Type))) + ")";
    }
    // Portable keys: project file name (or assembly simple name for metadata) instead of paths, configuration or versions.
    private string ProjectName(CompilationId compilation, IAssemblySymbol assembly) =>
        sourceProjects.TryGetValue(compilation, out var project) ? Path.GetFileNameWithoutExtension(project.ProjectPath) : assembly.Identity.Name;
    private string KeyPath(CompilationId compilation, string path) =>
        sourceProjects.TryGetValue(compilation, out var project) && Path.GetDirectoryName(project.ProjectPath) is { } directory && Path.IsPathRooted(directory) && Path.IsPathRooted(path)
            ? Path.GetRelativePath(directory, path).Replace('\\', '/') : path.Replace('\\', '/');
    // Local functions and lambdas have no documentation ID: anchor them to the nearest member that has one, plus name/ordinal and position.
    private string CallableKey(IMethodSymbol s, CompilationId compilation)
    {
        var project = ProjectName(compilation, s.ContainingAssembly);
        var node = s.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction ? s.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() : null;
        if (node is null)
            return project + "|" + (s.GetDocumentationCommentId() ?? Signature(s));
        ISymbol member = s;
        while (member is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } && member.ContainingSymbol is { } parent) member = parent;
        var name = s.MethodKind == MethodKind.LocalFunction ? "local:" + s.Name
            : "lambda:" + member.DeclaringSyntaxReferences.Where(x => x.SyntaxTree == node.SyntaxTree).Sum(x => LambdasBefore(x, node.SpanStart));
        var position = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition;
        return $"{project}|{member.GetDocumentationCommentId() ?? member.ToDisplayString()}|{name}@{KeyPath(compilation, node.SyntaxTree.FilePath)}:{position.Line + 1}:{position.Character + 1}";
    }
    // Lambda ordinal within one member declaration: anonymous-function starts are scanned once per declaration (document order = ascending, distinct).
    private int LambdasBefore(SyntaxReference declaration, int position)
    {
        if (!lambdaStarts.TryGetValue((declaration.SyntaxTree, declaration.Span), out var starts))
            lambdaStarts[(declaration.SyntaxTree, declaration.Span)] = starts = declaration.GetSyntax().DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Select(x => x.SpanStart).ToArray();
        var index = Array.BinarySearch(starts, position);
        return index >= 0 ? index : ~index;
    }
}
