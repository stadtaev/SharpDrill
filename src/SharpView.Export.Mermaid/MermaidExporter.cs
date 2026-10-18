using System.Globalization;
using System.Text;
using SharpView.Core;
using Mermaid.Flowcharts;
using Mermaid.Flowcharts.Links;
using Mermaid.Flowcharts.Nodes;
using Mermaid.Flowcharts.Nodes.NodeText;
using Mermaid.Flowcharts.Styling;
using Mermaid.Flowcharts.Styling.Attributes;
using Mermaid.Flowcharts.Styling.Attributes.Enums;
using Mermaid.Flowcharts.Subgraphs;

namespace SharpView.Export.Mermaid;

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
        output.AppendLine("```mermaid").Append(BuildFlowchart(diagram));
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

    public static string Markdown(ProjectMap map)
    {
        var output = new StringBuilder();
        output.AppendLine("# Project references").AppendLine();
        output.AppendLine($"Projects: {map.Projects.Count}; references: {map.Graph.EdgeCount}. An arrow points from a project to a project it references (<ProjectReference>).").AppendLine();
        var chart = Flowchart.Create(direction: FlowchartDirection.TB);
        var nodes = new Dictionary<CompilationId, Node>();
        var ordered = map.Graph.Nodes.Select(x => map.Projects[x]).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var node = Node.Create<MarkdownSafeText>("p" + (i + 1), $"{ordered[i].Name} ({ordered[i].Framework})");
            nodes.Add(ordered[i].Id, node);
            chart.AddNode(node);
        }
        foreach (var edge in map.Graph.Edges) chart.AddLink(Link.Create(nodes[edge.Source], nodes[edge.Target], LinkType.Create()));
        output.AppendLine("```mermaid").Append(chart.ToMermaidString()).AppendLine("```").AppendLine();
        output.AppendLine("| Project | Framework | Nullable | Nullable warnings as errors | All warnings as errors | Compiler warnings | Errors |");
        output.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var project in ordered)
            output.AppendLine($"| {Html(project.Name)} | {Html(project.Framework)} | {Html(project.Nullable)} | {(project.WarningsAsErrors != WarningsAsErrorsLevel.None ? "yes" : "no")} | {(project.WarningsAsErrors == WarningsAsErrorsLevel.All ? "yes" : "no")} | {project.Warnings} | {project.Errors} |");
        if (!map.Unloaded.IsEmpty)
        {
            output.AppendLine().AppendLine("## Unloaded references").AppendLine();
            foreach (var (source, path) in map.Unloaded) output.AppendLine($"- {Html(map.Projects[source].Name)} --> {Html(path)}");
        }
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string BuildFlowchart(CallDiagram diagram)
    {
        var chart = Flowchart.Create(direction: FlowchartDirection.TB);
        var rootStyle = new NodeStyle("root", new StyleClass(
            Fill: new Fill(Color.FromHex("#dbeafe")),
            Stroke: new Stroke(Color.FromHex("#2563eb")),
            StrokeWidth: StrokeWidth.Length(3, Unit.Px)));
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var group = 0;
        foreach (var project in diagram.Nodes.GroupBy(x => x.ProjectId))
        {
            var subgraph = Subgraph.Create<MarkdownSafeText>("p" + group++, project.First().Project);
            foreach (var node in project)
            {
                var suffix = node.Body is BodyStatus.MetadataOnly or BodyStatus.NoBody or BodyStatus.Unsupported ? $" [{node.Body}]" : "";
                var item = Node.Create<MarkdownSafeText>(node.Id, node.Label + suffix, nodeStyle: node.Id == diagram.Root ? rootStyle : null);
                nodes.Add(node.Id, item);
                subgraph.AddNode(item);
            }
            chart.AddNode(subgraph);
        }
        for (var i = 0; i < diagram.Edges.Length; i++)
        {
            var edge = diagram.Edges[i];
            var label = edge.Kind switch { CallEdgeKind.Direct => "direct", CallEdgeKind.Declared => "declared dispatch", CallEdgeKind.Candidate => "possible candidate", _ => "unresolved" };
            var type = LinkType.Create(thickness: edge.Kind == CallEdgeKind.Direct ? LinkThickness.Normal : LinkThickness.Dotted);
            chart.AddLink(Link.Create(nodes[edge.Source], nodes[edge.Target], type, $"e{i + 1}: {label}"));
        }
        return chart.ToMermaidString();
    }

    // The library owns Mermaid escaping. These extra rules protect the surrounding Markdown fence and retain
    // support for empty labels and line separators that the library's single-line text type rejects.
    private readonly record struct MarkdownSafeText(string Value) : INodeText<MarkdownSafeText>
    {
        public static MarkdownSafeText FromString(string text)
        {
            var singleLine = string.Concat(text.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c));
            var escaped = string.IsNullOrWhiteSpace(singleLine) ? singleLine : MermaidUnicodeText.FromString(singleLine).ToMermaidString();
            return new(escaped.Replace("`", "#96;", StringComparison.Ordinal));
        }

        public string ToMermaidString(int indentations = 0, string indentationText = "  ") => Value;
        public override string ToString() => Value;
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
