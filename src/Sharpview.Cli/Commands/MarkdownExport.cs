using System.Text;

namespace Sharpview.Cli;

internal static class MarkdownExport
{
    /// <summary>Writes a new file (never overwrites) at output, or in diagrams_exports next to the binary; returns its full path.</summary>
    internal static async Task<string> SaveAsync(string document, string? output, string name, CancellationToken token)
    {
        var path = output is null ? DefaultPath(name) : Path.GetFullPath(output);
        token.ThrowIfCancellationRequested();
        // CreateNew protects against races as well as accidental input-file overwrites.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        {
            try
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(document), token);
                await file.FlushAsync(token);
            }
            catch
            {
                await file.DisposeAsync();
                File.Delete(path);
                throw;
            }
        }
        return path;
    }

    private static string DefaultPath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagrams_exports");
        Directory.CreateDirectory(directory);
        var slug = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "diagram";
        if (slug.Length > 80) slug = slug[..80];
        return Path.GetFullPath(Path.Combine(directory, $"{slug}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md"));
    }
}
