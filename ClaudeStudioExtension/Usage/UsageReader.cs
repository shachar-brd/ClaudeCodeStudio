using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeStudioShared;

namespace ClaudeStudioExtension.Usage;

public static class UsageReader
{
    public static string GetProjectsRoot() => ClaudePaths.ProjectsDir;

    public static List<SessionUsage> ReadAll()
    {
        var root = GetProjectsRoot();
        if (!Directory.Exists(root)) return new();

        var result = new List<SessionUsage>();
        // Each API response counts once across all files (see UsageParser):
        // oldest file first, so a fork's copy of its parent's history leaves
        // the cost with the parent session.
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateDirectories(root)
            .SelectMany(projDir => Directory.EnumerateFiles(projDir, "*.jsonl"))
            .OrderBy(f => { try { return File.GetCreationTimeUtc(f); } catch { return DateTime.MaxValue; } });
        foreach (var jsonl in files)
        {
            try
            {
                var session = ParseFile(jsonl, counted);
                if (session != null) result.Add(session);
            }
            catch { /* skip malformed */ }
        }
        return result.OrderByDescending(s => s.LastTimestamp).ToList();
    }

    // #22a: line-parsing logic lives in ClaudeStudioShared.UsageParser now,
    // unit-tested there. FileShare.ReadWrite is deliberate — the CLI may
    // still be actively writing to this transcript while we read it.
    private static SessionUsage? ParseFile(string path, ISet<string> counted) =>
        UsageParser.ParseLines(ReadLinesShared(path), counted);

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }
}
