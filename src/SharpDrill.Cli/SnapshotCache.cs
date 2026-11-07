using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDrill.Core;
using SharpDrill.Roslyn;

namespace SharpDrill.Cli;

/// <summary>Reuses the last analysis of an input while nothing it read has changed, so commands run one after another skip
/// MSBuild and Roslyn. One file per input and load options, in the user's cache folder, never next to the analysed code.
/// A stale hit would be a wrong answer, so validity errs towards loading again, and any cache failure is just a miss.</summary>
internal static class SnapshotCache
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 512 };
    private static readonly EnumerationOptions AllFiles = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
    // Files that can change the analysis by merely appearing, so they are not listed in what the last load read.
    private static readonly HashSet<string> SourceKinds = new([".cs", ".cshtml", ".razor", ".csproj", ".props", ".targets", ".editorconfig", ".globalconfig"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConfigKinds = new([".props", ".targets", ".editorconfig", ".globalconfig"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SkippedFolders = new(["bin", ".git", ".vs", "node_modules"], StringComparer.OrdinalIgnoreCase);

    private sealed record Header(string Fingerprint, LoadInputs Inputs);

    internal static ProgramSnapshot? Read(AnalysisRequest request)
    {
        if (EntryPath(request) is not { } path || !File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            var header = JsonSerializer.Deserialize<Header>(reader.ReadString(), Json);
            if (header is null || header.Fingerprint != Fingerprint(header.Inputs)) return null;
            using var data = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<ProgramSnapshot>(data, Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException) { return null; }
    }

    internal static void Write(AnalysisRequest request, ProgramSnapshot snapshot)
    {
        // A failed load or broken code may be fixed by what no input records (a build generating the missing file): always look again.
        if (snapshot.Gaps.Any(x => x.Kind is GapKind.ProjectLoad or GapKind.Compilation || x.Kind == GapKind.Internal) || EntryPath(request) is not { } path) return;
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = File.Create(temporary))
            {
                using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
                    writer.Write(JsonSerializer.Serialize(new Header(Fingerprint(snapshot.Inputs), snapshot.Inputs), Json));
                using var data = new GZipStream(file, CompressionLevel.Fastest);
                JsonSerializer.Serialize(data, snapshot with { Inputs = new() }, Json);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException) { try { File.Delete(temporary); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } }
    }

    // Input and options pick the file, so re-analysing replaces it and the cache cannot grow with every edit.
    private static string? EntryPath(AnalysisRequest r)
    {
        var folder = Environment.GetEnvironmentVariable("SHARPDRILL_CACHE_DIR");
        if (string.IsNullOrEmpty(folder))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return null;
            folder = Path.Combine(local, "sharpdrill", "cache");
        }
        var key = string.Join('\n', Path.GetFullPath(r.InputPath), r.Configuration, r.TargetFramework, r.IncludeGenerated, r.IncludeTests);
        return Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".snapshot");
    }

    // This exact tool, plus everything the load read: content for code and project files, size and time for referenced
    // assemblies (immutable and large), and the source files present now, so a new file counts.
    private static string Fingerprint(LoadInputs inputs)
    {
        var scanned = inputs.Folders.SelectMany(Scan);
        var contents = inputs.Files.Concat(scanned).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            .AsParallel().AsOrdered().Select(x => x + "|" + Content(x));
        var stamps = inputs.Stamped.Select(x => new FileInfo(x)).Select(x => x.FullName + "|" + (x.Exists ? $"{x.Length}|{x.LastWriteTimeUtc.Ticks}" : "missing"));
        var tool = new[] { typeof(SnapshotCache), typeof(ProgramSnapshot), typeof(AnalysisRequest) }.Select(x => x.Module.ModuleVersionId.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tool.Concat(contents).Concat(stamps)))));
    }

    private static string Content(string path)
    {
        try { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "missing"; }
    }

    // A project's folder tree, and configuration in the folders above it, which MSBuild and the compiler pick up by location.
    private static IEnumerable<string> Scan(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var current))
        {
            if (!Directory.Exists(current)) continue;
            foreach (var file in Directory.EnumerateFiles(current, "*", AllFiles).Where(x => SourceKinds.Contains(Path.GetExtension(x)))) yield return file;
            foreach (var child in Directory.EnumerateDirectories(current, "*", AllFiles).Where(x => !SkippedFolders.Contains(Path.GetFileName(x)))) pending.Push(child);
        }
        for (var above = Directory.GetParent(folder); above is not null; above = above.Parent)
        {
            if (!above.Exists) continue;
            foreach (var file in Directory.EnumerateFiles(above.FullName, "*", AllFiles).Where(x => ConfigKinds.Contains(Path.GetExtension(x)) || Path.GetFileName(x).Equals("global.json", StringComparison.OrdinalIgnoreCase))) yield return file;
        }
    }
}
