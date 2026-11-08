using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeStudioShared;

// Recognises a plain shell build (dotnet build/clean/msbuild, msbuild.exe) that
// Visual Studio can run instead, and maps it to a VsBuildSpec. Anything it does
// not fully understand returns false so the shell command runs unchanged:
// chained or piped commands, publish-style options (-o, -r, properties), or
// targets other than Build/Rebuild/Clean.
public static class BuildCommandParser
{
    // Characters that make it more than one plain command (bash or PowerShell).
    private static readonly char[] ShellOperators = { '&', '|', ';', '\n', '\r', '>', '<', '`', '$', '(', ')' };

    // Options that do not change what gets built. Value options consume the next
    // token unless written as -x:value / -x=value.
    private static readonly HashSet<string> NeutralFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nologo", "-nologo", "restore", "-no-restore", "m", "maxcpucount", "nr", "nodereuse",
        "tl", "-tl", "bl", "binarylogger", "-no-dependencies", "noautorsp", "noautoresponse",
        "clp", "consoleloggerparameters", "flp", "fileloggerparameters", "fl", "filelogger",
        "warnaserror", "-warnaserror", "-interactive", "interactive", "ds", "detailedsummary"
    };

    private static readonly HashSet<string> NeutralValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "v", "verbosity", "-verbosity", "-v"
    };

    private static readonly HashSet<string> ConfigurationOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "-configuration", "-c"
    };

    private static readonly HashSet<string> TargetOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "t", "target", "-target", "-t"
    };

    private static readonly Regex MsbuildSlashOption = new(@"^/[A-Za-z][\w-]*([:=].*)?$");

    public static bool TryParse(string? command, string? workingDirectory, out VsBuildSpec spec)
    {
        spec = new VsBuildSpec { WorkingDirectory = workingDirectory, FromShell = true };
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (command!.IndexOfAny(ShellOperators) >= 0) return false;

        var tokens = Tokenize(command);
        if (tokens == null || tokens.Count == 0) return false;

        var exe = ExeName(tokens[0]);
        int i;
        bool dotnetStyle;
        if (exe == "dotnet")
        {
            if (tokens.Count < 2) return false;
            var verb = tokens[1].ToLowerInvariant();
            switch (verb)
            {
                case "build": spec.Action = "build"; dotnetStyle = true; break;
                case "clean": spec.Action = "clean"; dotnetStyle = true; break;
                case "msbuild": spec.Action = "build"; dotnetStyle = false; break;
                default: return false;
            }
            i = 2;
        }
        else if (exe == "msbuild")
        {
            spec.Action = "build";
            dotnetStyle = false;
            i = 1;
        }
        else
        {
            return false;
        }

        string? target = null;
        for (; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (!IsOption(tok))
            {
                if (target != null) return false; // two positional targets
                target = tok;
                continue;
            }

            // "-c:Release" / "/p:X=Y" / "--configuration=Release" → name + inline value.
            var body = tok.Substring(1);
            string? inlineValue = null;
            var sep = body.IndexOfAny(new[] { ':', '=' });
            if (sep >= 0)
            {
                inlineValue = body.Substring(sep + 1);
                body = body.Substring(0, sep);
            }

            if (NeutralFlags.Contains(body))
                continue;

            if (NeutralValueOptions.Contains(body))
            {
                if (inlineValue == null) i++;
                continue;
            }

            if (ConfigurationOptions.Contains(body) && dotnetStyle)
            {
                var value = inlineValue ?? (i + 1 < tokens.Count ? tokens[++i] : null);
                if (string.IsNullOrEmpty(value)) return false;
                spec.Configuration = value;
                continue;
            }

            if (body.Equals("-no-incremental", StringComparison.OrdinalIgnoreCase) && dotnetStyle)
            {
                if (spec.Action == "build") spec.Action = "rebuild";
                continue;
            }

            // -p:Configuration=X is the one property we can honour.
            if (body.Equals("p", StringComparison.OrdinalIgnoreCase) ||
                body.Equals("property", StringComparison.OrdinalIgnoreCase) ||
                body.Equals("-property", StringComparison.OrdinalIgnoreCase))
            {
                if (inlineValue == null) return false;
                var kv = inlineValue.Split('=');
                if (kv.Length != 2 || !kv[0].Equals("Configuration", StringComparison.OrdinalIgnoreCase))
                    return false;
                spec.Configuration = kv[1];
                continue;
            }

            if (TargetOptions.Contains(body) && !dotnetStyle)
            {
                if (inlineValue == null) return false;
                var action = MapTargets(inlineValue);
                if (action == null) return false;
                spec.Action = action;
                continue;
            }

            return false; // anything that may change the output: run it in the shell
        }

        spec.Projects.Add(target ?? ".");
        return true;
    }

    private static string? MapTargets(string value)
    {
        var targets = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();
        if (targets.Count == 0) return null;
        if (targets.Any(t => t is not ("build" or "rebuild" or "clean" or "restore"))) return null;
        if (targets.Contains("rebuild")) return "rebuild";
        if (targets.Contains("clean") && targets.Contains("build")) return "rebuild";
        if (targets.Contains("clean")) return "clean";
        return "build";
    }

    private static bool IsOption(string tok) =>
        tok.Length > 1 && (tok[0] == '-' || MsbuildSlashOption.IsMatch(tok));

    private static string ExeName(string tok)
    {
        var name = tok.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name.Substring(slash + 1);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        return name.ToLowerInvariant();
    }

    // Splits on whitespace, honouring "double" and 'single' quotes. Null on an
    // unterminated quote.
    private static List<string>? Tokenize(string command)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        bool inToken = false;
        foreach (var ch in command)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
                continue;
            }
            if (ch == '"' || ch == '\'')
            {
                quote = ch;
                inToken = true;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (inToken) { tokens.Add(cur.ToString()); cur.Clear(); inToken = false; }
                continue;
            }
            cur.Append(ch);
            inToken = true;
        }
        if (quote != '\0') return null;
        if (inToken) tokens.Add(cur.ToString());
        return tokens;
    }
}
