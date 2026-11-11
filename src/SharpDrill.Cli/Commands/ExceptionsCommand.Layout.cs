using System.Collections.Immutable;
using SharpDrill.Core;

namespace SharpDrill.Cli;

// Text layout shared by the views: numbered blocks, aligned labels and paths that refer back to earlier ones.
internal sealed partial class ExceptionsCommand
{
    // From the entry point down to the throw, on one line when it fits. A step through an interface/virtual call (~>) assumes
    // which implementation runs; when source has several, the step says so, since that is where the path is a guess. A
    // path that repeats most of an earlier finding's path refers to it and spells out only the rest (--locations keeps
    // every step, since each has its own location).
    private static void WritePath(ReportContext context, Report report, ExceptionPath example)
    {
        var display = context.Display;
        ImmutableArray<CallableId> path = [.. example.Steps.Select(x => x.Method.Id)];
        var steps = example.Steps.Select((x, i) => (Arrow: i == 0 ? "" : x.IsAssumed ? "~> " : "-> ",
            Name: display.Short(x.Method.Id) + (x.Implementations > 1 ? $" (1 of {x.Implementations} implementations)" : ""))).ToArray();
        if (steps.Length > 1)
        {
            var (earlier, shared, same) = context.Arguments.Locations ? default : report.Earlier(path);
            if (shared >= SharedSteps)
            {
                var tail = steps[shared..];
                var head = same ? $"same as {earlier}" : $"as in {earlier} up to {steps[shared - 1].Name}" + (tail.Length == 0 ? "" : ", then");
                var inline = string.Join(" ", [head, .. tail.Select(x => x.Arrow + x.Name)]);
                if (report.Fits(inline)) report.Line("Path", inline);
                else { report.Line("Path", head); foreach (var (arrow, name) in tail) report.Step(arrow, name); }
            }
            else
            {
                var inline = string.Join(" ", steps.Select(x => x.Arrow + x.Name));
                if (report.Fits(inline)) report.Line("Path", inline);
                else { report.Line("Path", steps[0].Name); foreach (var (arrow, name) in steps[1..]) report.Step(arrow, name); }
                report.Remember(path);
            }
        }
        if (!context.Arguments.Locations) return;
        var places = example.Locations.Select(display.Where).ToArray();
        for (var i = 0; i < places.Length; i++) if (i == 0) report.Line("Locations", places[i]); else report.More(places[i]);
    }

    // Fewer shared steps than this are cheaper to repeat than to look up.
    private const int SharedSteps = 3;

    // Numbered findings separated by blank lines, after an overview by exception type; labels share one column so the
    // values line up.
    private sealed class Report(TextWriter output)
    {
        private const int Column = 15, Width = 120;
        private readonly List<(int Number, ImmutableArray<CallableId> Path)> paths = [];
        private int number;
        private bool written;

        // Paths written in full, so a later finding can refer to the one sharing its longest prefix.
        public void Remember(ImmutableArray<CallableId> path) => paths.Add((number, path));
        public (int Number, int Shared, bool Same) Earlier(ImmutableArray<CallableId> path) => paths
            .Select(x => (x.Number, Shared: x.Path.Zip(path).TakeWhile(p => p.First == p.Second).Count(), Same: x.Path.SequenceEqual(path)))
            .OrderByDescending(x => x.Shared).ThenByDescending(x => x.Same).ThenBy(x => x.Number).FirstOrDefault();

        private void Separate() { if (written) output.WriteLine(); written = true; }

        // Left out for a single finding, where it would only repeat it.
        public void Overview(string headline, string[] types, int others = 0)
        {
            if (types.Length + others < 2) return;
            Separate();
            output.WriteLine($"Overview: {headline}");
            var byType = types.GroupBy(x => x).Select(x => (Type: x.Key, Count: x.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray();
            var width = byType.Select(x => x.Count.ToString().Length).DefaultIfEmpty().Max();
            foreach (var (type, count) in byType) output.WriteLine($"  {count.ToString().PadLeft(width)}  {type}");
        }

        public void Section(string title, IEnumerable<string> items)
        {
            Separate();
            output.WriteLine(title);
            foreach (var item in items) output.WriteLine($"  - {item}");
        }

        public void Begin(string title) { Separate(); output.WriteLine($"{++number}. {title}"); }
        // Comma-separated on the label's line when it fits, else one item per line.
        public void List(string label, IEnumerable<string> items)
        {
            var all = items.ToArray();
            if (Fits(string.Join(", ", all))) { Line(label, string.Join(", ", all)); return; }
            for (var i = 0; i < all.Length; i++) if (i == 0) Line(label, all[i]); else More(all[i]);
        }
        public void Line(string label, string text) => output.WriteLine($"   {label + ":",-11} {text}");
        public void More(string text) => output.WriteLine(new string(' ', Column) + text);
        public void Step(string arrow, string text) => output.WriteLine(new string(' ', Column - arrow.Length) + arrow + text);
        public bool Fits(string text) => Column + text.Length <= Width;
    }
}
