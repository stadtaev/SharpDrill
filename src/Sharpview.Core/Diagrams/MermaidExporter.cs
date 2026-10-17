using System.Globalization;
using System.Text;

namespace Sharpview.Core;

// Pure presentation over a projection: never walks the program graph or infers architectural layers.
public static class MermaidExporter
{
    public static string Markdown(CallDiagram diagram)
    {
        var output = new StringBuilder();
        var root = diagram.Nodes.Single(x => x.Id == diagram.Root);
        output.AppendLine("# Call diagram").AppendLine().AppendLine(Html(root.Label)).AppendLine();
        output.AppendLine($"Nodes: {diagram.Nodes.Length}; edges: {diagram.Edges.Length}; examined call sites: {diagram.ExaminedSites}.");
        output.AppendLine("Solid arrows: direct calls. Dotted arrows: declared dispatch, possible implementation candidates, or unresolved calls.");
        output.AppendLine("Branches show possible static calls, not execution order. Shared methods appear once; cycles represent recursion.");
        output.AppendLine($"Implementation candidates followed: {diagram.FollowCandidates}. Completeness: {(diagram.IsComplete ? "complete within the extracted call model" : "partial (see boundaries, limits and gaps below)")}.").AppendLine();
        if (!diagram.Limits.IsEmpty)
            output.AppendLine($"**Truncated: {string.Join(", ", diagram.Limits)} limit reached. Omitted calls may exist.**").AppendLine();
        output.AppendLine("```mermaid").AppendLine("flowchart TB");
        var group = 0;
        foreach (var project in diagram.Nodes.GroupBy(x => x.ProjectId))
        {
            output.AppendLine($"  subgraph p{group++}[\"{Label(project.First().Project)}\"]");
            foreach (var node in project)
            {
                var suffix = node.Body is BodyStatus.MetadataOnly or BodyStatus.NoBody or BodyStatus.Unsupported ? $" [{node.Body}]" : "";
                output.AppendLine($"    {node.Id}[\"{Label(node.Label + suffix)}\"]");
            }
            output.AppendLine("  end");
        }
        for (var i = 0; i < diagram.Edges.Length; i++)
        {
            var edge = diagram.Edges[i];
            var label = edge.Kind switch { CallEdgeKind.Direct => "direct", CallEdgeKind.Declared => "declared dispatch", CallEdgeKind.Candidate => "possible candidate", _ => "unresolved" };
            var arrow = edge.Kind == CallEdgeKind.Direct ? "-->" : "-.->";
            output.AppendLine($"  {edge.Source} {arrow}|\"e{i + 1}: {label}\"| {edge.Target}");
        }
        output.AppendLine("  classDef root fill:#dbeafe,stroke:#2563eb,stroke-width:3px");
        output.AppendLine($"  class {diagram.Root} root");
        output.AppendLine("```").AppendLine();
        output.AppendLine("## Call-site evidence").AppendLine();
        output.AppendLine("| Edge | Kind | Source location | Call-site ID |");
        output.AppendLine("| --- | --- | --- | --- |");
        for (var i = 0; i < diagram.Edges.Length; i++)
        {
            var edge = diagram.Edges[i]; var evidence = edge.Evidence;
            output.AppendLine($"| e{i + 1} | {edge.Kind?.ToString() ?? "Unresolved"} | {Html(evidence.Path)}:{evidence.StartLine}:{evidence.StartColumn} | {Html(edge.Site.Value)} |");
        }
        if (!diagram.Boundaries.IsEmpty)
        {
            var byId = diagram.Nodes.ToDictionary(x => x.Id);
            output.AppendLine().AppendLine("## Analysis boundaries").AppendLine();
            foreach (var boundary in diagram.Boundaries)
                output.AppendLine($"- {boundary.Kind}: {Html(byId[boundary.Node].Label)}" +
                    (boundary.Site is { } site ? $" (site {Html(site.Value)})" : ""));
        }
        output.AppendLine().AppendLine("## Coverage").AppendLine();
        output.AppendLine("Source-backed call relationships only. No runtime receiver resolution, execution order, implicit framework/database behavior, or proof that exceptions are unhandled. Minimal APIs and inherited action enumeration are outside the current extractor's scope.");
        output.AppendLine($"Relevant extraction gaps: {diagram.Gaps.Length}. Ownerless gaps are included because their affected calls cannot be localized.");
        foreach (var gap in diagram.Gaps.Take(20))
            output.AppendLine($"- {gap.Kind}: {Html(gap.Message)}" + (gap.Evidence is { } e ? $" ({Html(e.Path)}:{e.StartLine}:{e.StartColumn})" : ""));
        if (diagram.Gaps.Length > 20) output.AppendLine($"- {diagram.Gaps.Length - 20} further gaps; use inspect JSON for full evidence.");
        // Portable deterministic output, irrespective of the host operating system's newline convention.
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // Mermaid uses #...; entity codes rather than HTML's &...;. Encode syntax/HTML/Markdown delimiters before output.
    private static string Label(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsControl(c)) { result.Append(' '); continue; }
            if (c == '"') result.Append("#quot;");
            else if (c is '#' or '&' or '<' or '>' or '[' or ']' or '{' or '}' or '|' or '`' or '\\')
                result.Append('#').Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
            else result.Append(c);
        }
        return result.ToString();
    }

    private static string Html(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsControl(c)) result.Append(' ');
            else if (c is '&' or '<' or '>' or '"' or '\'' or '|' or '`' or '*' or '_' or '[' or ']' or '\\')
                result.Append("&#").Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
            else result.Append(c);
        }
        return result.ToString();
    }
}
