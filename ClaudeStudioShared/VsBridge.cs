using System.Text.Json;

namespace ClaudeStudioShared;

// A build the agent asks the extension to run inside Visual Studio.
public class VsBuildSpec
{
    // build | rebuild | clean
    public string Action { get; set; } = "build";

    // Project names, project file paths or directories. Empty = whole solution.
    // A directory (or ".") resolves to the open solution when it holds the .sln,
    // otherwise to the project that lives in it.
    public List<string> Projects { get; set; } = new();

    // Solution configuration (e.g. "Release"). Null = the IDE's active one.
    public string? Configuration { get; set; }

    // Base for relative paths in Projects.
    public string? WorkingDirectory { get; set; }

    // Set when the request comes from a redirected shell build: if VS cannot map
    // the target to the open solution, the extension answers empty so the shell
    // command runs as approved instead of failing.
    public bool FromShell { get; set; }
}

// A test run the agent asks the extension to perform for the open solution.
public class VsTestSpec
{
    // Project names or paths. Empty = every test project in the solution.
    public List<string> Projects { get; set; } = new();

    // dotnet test --filter expression.
    public string? Filter { get; set; }

    // Build the projects in VS first (then run with --no-build).
    public bool Build { get; set; } = true;

    public string? WorkingDirectory { get; set; }
}

// Encodes bridge requests into diagnostics_request.Text as "<marker><json>".
public static class VsBridge
{
    public const string BuildMarker = "@build:";
    public const string TestMarker = "@test:";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Encode(VsBuildSpec spec) => BuildMarker + JsonSerializer.Serialize(spec, Options);

    public static string Encode(VsTestSpec spec) => TestMarker + JsonSerializer.Serialize(spec, Options);

    public static bool IsBridgeRequest(string? text) =>
        text != null && (text.StartsWith(BuildMarker, StringComparison.Ordinal) ||
                         text.StartsWith(TestMarker, StringComparison.Ordinal));

    public static bool TryDecode(string? text, out VsBuildSpec spec) => TryDecode(text, BuildMarker, out spec);

    public static bool TryDecode(string? text, out VsTestSpec spec) => TryDecode(text, TestMarker, out spec);

    private static bool TryDecode<T>(string? text, string marker, out T spec) where T : new()
    {
        spec = new T();
        if (text == null || !text.StartsWith(marker, StringComparison.Ordinal)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(text.Substring(marker.Length), Options);
            if (parsed == null) return false;
            spec = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
