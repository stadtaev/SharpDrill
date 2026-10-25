namespace SharpView.Cli;

/// <summary><c>sharpview skill [--user]</c>: writes the agent skill (SKILL.md, embedded and stamped with this version) where
/// GitHub Copilot and Claude Code find it. No project is loaded.</summary>
internal static class SkillCommand
{
    private const string Marker = "# Written by `sharpview skill`";

    internal static int Run(string[] options)
    {
        var user = options switch { [] => false, ["--user"] => true, _ => throw new ArgumentException("skill accepts only --user.") };
        // In a repository both agents read .claude/skills; per user each reads its own folder.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots = user ? [Path.Combine(home, ".copilot"), Path.Combine(home, ".claude")] : [Path.Combine(RepositoryRoot(Directory.GetCurrentDirectory()), ".claude")];
        var targets = roots.Select(x => Path.Combine(x, "skills", "sharpview", "SKILL.md")).ToArray();
        // Check every target before writing any, so a refusal leaves nothing half-installed.
        foreach (var target in targets)
            if (File.Exists(target) && !File.ReadLines(target).Any(x => x.StartsWith(Marker, StringComparison.Ordinal)))
                throw new IOException($"{target} exists and was not written by sharpview; move it away to install the skill.");
        var content = Content();
        foreach (var target in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
            Console.WriteLine(target);
        }
        Console.Error.WriteLine(user
            ? "Installed for GitHub Copilot and Claude Code in every repository. Rerun after updating the tool to refresh it."
            : "Installed for GitHub Copilot and Claude Code in this repository. Commit it so teammates and the Copilot cloud agent get it; rerun after updating the tool to refresh it.");
        return 0;
    }

    // After a report, for a person at a terminal: one line when no agent can find a skill, or when the one sharpview wrote is
    // from another version. Agents and CI (stderr redirected) and builds (--format msbuild) never see it; it never fails a run.
    internal static void Hint(Arguments arguments)
    {
        if (Console.IsErrorRedirected || arguments.MSBuild) return;
        try
        {
            if (HintFor(Directory.GetCurrentDirectory(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), EntryPoint.Version) is { } hint)
                Console.Error.WriteLine(hint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Every folder GitHub Copilot or Claude Code reads skills from, in the repository and per user. A SKILL.md that sharpview
    // did not write still counts as installed. Local builds (-dev) do not compare versions.
    internal static string? HintFor(string directory, string home, string version)
    {
        var repository = RepositoryRoot(directory);
        var found = new[] { (repository, ".claude"), (repository, ".github"), (repository, ".agents"), (home, ".copilot"), (home, ".claude"), (home, ".agents") }
            .Select(x => (User: x.Item1 == home && x.Item1 != repository, Path: Path.Combine(x.Item1, x.Item2, "skills", "sharpview", "SKILL.md")))
            .Where(x => File.Exists(x.Path)).ToArray();
        if (found.Length == 0) return "Tip: run `sharpview skill` so GitHub Copilot and Claude Code can use SharpView in this repository.";
        if (version.EndsWith("-dev", StringComparison.Ordinal)) return null;
        foreach (var (user, path) in found)
            if (File.ReadLines(path).FirstOrDefault(x => x.StartsWith(Marker, StringComparison.Ordinal)) is { } line
                && line[Marker.Length..].Trim().Split(' ')[0].TrimEnd('.') is var written && written != version
                // The file may come from a cloned repository: print only version-shaped text, never escape sequences.
                && System.Text.RegularExpressions.Regex.IsMatch(written, "^[0-9A-Za-z.+-]{1,64}$"))
                return $"Tip: the SharpView skill in {path} is from {written}; run `sharpview skill{(user ? " --user" : "")}` to update it.";
        return null;
    }

    internal static string Content()
    {
        using var stream = typeof(SkillCommand).Assembly.GetManifestResourceStream("SKILL.md")!;
        return new StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n").Replace("{{version}}", EntryPoint.Version, StringComparison.Ordinal);
    }

    // The nearest directory holding .git (a directory, or a file in worktrees and submodules); else the current directory.
    private static string RepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        return start;
    }
}
