using System;
using System.IO;
using System.Text.Json;

namespace ClaudeStudioExtension;

// Detects a Claude Code "gateway" login (forceLoginMethod=gateway). Unlike a
// normal Anthropic login, it writes no oauthAccount to .claude.json; the login
// lives in <config>/.credentials.json under "enterpriseGateway".
// Only the gateway host is ever returned: token values are never read out,
// stored, logged or sent to the web view.
public static class GatewayLogin
{
    public static bool TryRead(out string? host) => TryRead(ClaudePaths.CredentialsJsonPath, out host);

    public static bool TryRead(string credentialsPath, out string? host)
    {
        host = null;
        try
        {
            if (!File.Exists(credentialsPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            return TryParse(doc.RootElement, out host);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParse(JsonElement root, out string? host)
    {
        host = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("enterpriseGateway", out var gw) || gw.ValueKind != JsonValueKind.Object)
            return false;

        // A refresh token alone is enough: the CLI renews an expired jwt itself.
        if (!HasValue(gw, "jwt") && !HasValue(gw, "idpRefreshToken"))
            return false;

        if (gw.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
            host = uri.Host;

        return true;
    }

    private static bool HasValue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString());
}
