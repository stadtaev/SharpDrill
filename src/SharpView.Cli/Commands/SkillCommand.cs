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
