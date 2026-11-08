using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeStudioAgent;

// Remembers, per claude executable (path + size + timestamp), its version and the
// flag sets it has started with. A spawn with a known exe and flag set skips the
// separate `claude --version` probe: that probe is a second start of the large
// claude binary (expensive in a sandbox), and the wait it gave was only there to
// catch a launch-time flag rejection, which this exe already passed with these
// flags. Kept in %LOCALAPPDATA%\ClaudeStudio\claude-launch-cache.json; a new or
// updated CLI has a new key and is probed again.
internal static class ClaudeLaunchCache
{
    private sealed class Entry
    {
        public string? Version { get; set; }
        public List<string> FlagSets { get; set; } = new();
    }

    private static readonly object Lock = new();
    private static Dictionary<string, Entry>? _entries;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStudio", "claude-launch-cache.json");

    private static string? ExeKey(string exePath)
    {
        try
        {
            var fi = new FileInfo(exePath);
            if (!fi.Exists) return null;
            return $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }
    }

    // Only flag names matter (values like the model or session id do not change
    // whether claude accepts them).
    private static string FlagSetKey(IEnumerable<string> args)
    {
        var flags = string.Join(" ", args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Distinct().OrderBy(a => a, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flags)))[..16];
    }

    private static Dictionary<string, Entry> Entries()
    {
        if (_entries != null) return _entries;
        try
        {
            if (File.Exists(FilePath))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
        }
        catch { }
        return _entries ??= new Dictionary<string, Entry>();
    }

    // The cached version when this exe has started with this flag set before.
    public static bool TryGetKnownLaunch(string exePath, IEnumerable<string> args, out string? version)
    {
        version = null;
        var key = ExeKey(exePath);
        if (key == null) return false;
        lock (Lock)
        {
            if (!Entries().TryGetValue(key, out var e) || !e.FlagSets.Contains(FlagSetKey(args))) return false;
            version = e.Version;
            return true;
        }
    }

    public static string? GetVersion(string exePath)
    {
        var key = ExeKey(exePath);
        if (key == null) return null;
        lock (Lock) return Entries().TryGetValue(key, out var e) ? e.Version : null;
    }

    // After a spawn that survived the probe: exe + flags are known good.
    public static void RecordLaunch(string exePath, IEnumerable<string> args, string? version)
    {
        var key = ExeKey(exePath);
        if (key == null) return;
        lock (Lock)
        {
            var entries = Entries();
            // Old keys of the same path are stale versions; drop them.
            var path = key[..key.IndexOf('|')];
            foreach (var stale in entries.Keys.Where(k => k != key && k.StartsWith(path + "|", StringComparison.Ordinal)).ToList())
                entries.Remove(stale);
            if (!entries.TryGetValue(key, out var e)) entries[key] = e = new Entry();
            if (version != null) e.Version = version;
            var flags = FlagSetKey(args);
            if (!e.FlagSets.Contains(flags)) e.FlagSets.Add(flags);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
            }
            catch { }
        }
    }
}
