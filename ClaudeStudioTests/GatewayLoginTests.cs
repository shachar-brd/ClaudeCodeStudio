using ClaudeStudioExtension;
using Xunit;

namespace ClaudeStudioTests;

public class GatewayLoginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gwlogin-" + Guid.NewGuid().ToString("N"));

    public GatewayLoginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Gateway_login_with_jwt_is_detected_and_returns_only_the_host()
    {
        var path = Write("""
            {"enterpriseGateway":{"url":"https://claude-appgw.example.com","jwt":"x.y.z","idpRefreshToken":"r"}}
            """);

        Assert.True(GatewayLogin.TryRead(path, out var host));
        Assert.Equal("claude-appgw.example.com", host);
    }

    [Fact]
    public void Refresh_token_alone_counts_as_signed_in()
    {
        var path = Write("""
            {"enterpriseGateway":{"url":"https://gw.example.com","jwt":"","idpRefreshToken":"r"}}
            """);

        Assert.True(GatewayLogin.TryRead(path, out var host));
        Assert.Equal("gw.example.com", host);
    }

    [Fact]
    public void Missing_url_is_still_signed_in_with_no_host()
    {
        var path = Write("""{"enterpriseGateway":{"jwt":"x"}}""");

        Assert.True(GatewayLogin.TryRead(path, out var host));
        Assert.Null(host);
    }

    [Theory]
    [InlineData("""{"enterpriseGateway":{"url":"https://gw.example.com","jwt":"","idpRefreshToken":""}}""")]
    [InlineData("""{"enterpriseGateway":{"url":"https://gw.example.com"}}""")]
    [InlineData("""{"enterpriseGateway":"nope"}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a"}}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Without_usable_gateway_tokens_it_is_not_signed_in(string json)
    {
        Assert.False(GatewayLogin.TryRead(Write(json), out var host));
        Assert.Null(host);
    }

    [Fact]
    public void Missing_file_is_not_signed_in()
    {
        Assert.False(GatewayLogin.TryRead(Path.Combine(_dir, "nope.json"), out _));
    }
}
