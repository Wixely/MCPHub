using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// Covers how a user-added server's declared credential turns into transport headers or environment,
/// and the places a token could leak into something user-visible.
/// </summary>
public class UserServerAuthTests
{
    private static UserMcpServerDefinition Http(McpAuthKind auth, string? headerName = null) => new()
    {
        DisplayName = "Remote",
        Kind = McpTransportKind.Http,
        Endpoint = "https://example.test/mcp",
        Auth = auth,
        AuthHeaderName = headerName,
    };

    private static UserMcpServerDefinition Stdio(McpAuthKind auth, string? variable = null) => new()
    {
        DisplayName = "Local",
        Kind = McpTransportKind.Stdio,
        Command = "some-server",
        Auth = auth,
        AuthEnvironmentVariable = variable,
    };

    [Fact]
    public void Bearer_token_becomes_an_authorization_header()
    {
        var headers = UserServerAuth.BuildHeaders(Http(McpAuthKind.BearerToken), "tok_abc");

        var header = Assert.Single(headers!);
        Assert.Equal("Authorization", header.Key);
        Assert.Equal("Bearer tok_abc", header.Value);
    }

    [Fact]
    public void Header_token_uses_the_named_header_verbatim()
    {
        var headers = UserServerAuth.BuildHeaders(Http(McpAuthKind.HeaderToken, "X-Custom-Key"), "tok_abc");

        var header = Assert.Single(headers!);
        Assert.Equal("X-Custom-Key", header.Key);
        Assert.Equal("tok_abc", header.Value); // no scheme prefix
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Header_token_without_a_name_falls_back_to_the_default(string? name)
    {
        var headers = UserServerAuth.BuildHeaders(Http(McpAuthKind.HeaderToken, name), "tok_abc");

        Assert.Equal(UserMcpServerDefinition.DefaultAuthHeaderName, Assert.Single(headers!).Key);
    }

    [Fact]
    public void Environment_token_becomes_a_child_process_variable()
    {
        var environment = UserServerAuth.BuildEnvironment(Stdio(McpAuthKind.EnvironmentToken, "MY_SERVER_TOKEN"), "tok_abc");

        var entry = Assert.Single(environment!);
        Assert.Equal("MY_SERVER_TOKEN", entry.Key);
        Assert.Equal("tok_abc", entry.Value);
    }

    [Fact]
    public void Environment_token_without_a_name_falls_back_to_the_default()
    {
        var environment = UserServerAuth.BuildEnvironment(Stdio(McpAuthKind.EnvironmentToken), "tok_abc");

        Assert.Equal(UserMcpServerDefinition.DefaultAuthEnvironmentVariable, Assert.Single(environment!).Key);
    }

    [Fact]
    public void No_auth_sends_nothing()
    {
        Assert.Null(UserServerAuth.BuildHeaders(Http(McpAuthKind.None), "tok_abc"));
        Assert.Null(UserServerAuth.BuildEnvironment(Stdio(McpAuthKind.None), "tok_abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_declared_mechanism_with_no_stored_token_sends_nothing(string? token)
    {
        // The alternative is a literal "Bearer " header, which reads as a malformed credential upstream
        // rather than as the absent one it is.
        Assert.Null(UserServerAuth.BuildHeaders(Http(McpAuthKind.BearerToken), token));
        Assert.Null(UserServerAuth.BuildHeaders(Http(McpAuthKind.HeaderToken, "X-Key"), token));
        Assert.Null(UserServerAuth.BuildEnvironment(Stdio(McpAuthKind.EnvironmentToken), token));
    }

    [Fact]
    public void Mechanisms_do_not_cross_transports()
    {
        // An environment credential is not silently promoted to a header, or the reverse.
        Assert.Null(UserServerAuth.BuildHeaders(Http(McpAuthKind.EnvironmentToken), "tok_abc"));
        Assert.Null(UserServerAuth.BuildEnvironment(Stdio(McpAuthKind.BearerToken), "tok_abc"));
    }

    [Fact]
    public void Token_is_never_written_to_the_settings_file()
    {
        // The definition has no token property at all; this pins that it stays that way.
        var definition = Http(McpAuthKind.BearerToken);
        var settings = new MCPHubSettings();
        settings.UserServers.Add(definition);

        var json = System.Text.Json.JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("tok_", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secret_keys_are_per_server()
    {
        var a = Http(McpAuthKind.BearerToken);
        var b = Http(McpAuthKind.BearerToken);

        Assert.NotEqual(a.SecretKey, b.SecretKey);
        Assert.StartsWith(SecretKeys.UserServerTokenPrefix, a.SecretKey);
        Assert.Equal(SecretKeys.UserServerToken(a.Id), a.SecretKey);
    }

    [Theory]
    [InlineData("https://example.test/mcp", "https://example.test/mcp")]
    [InlineData("https://tok_secret@example.test/mcp", "https://example.test/mcp")]
    [InlineData("https://user:tok_secret@example.test/mcp", "https://example.test/mcp")]
    public void Endpoint_label_never_carries_credentials(string input, string expected)
    {
        // This string is shown on the Proxy page and written to every connect and fault log line, so a token
        // pasted into the URL must not survive into it.
        var described = UpstreamRegistry.DescribeEndpoint(new Uri(input));

        Assert.Equal(expected, described);
        Assert.DoesNotContain("tok_secret", described, StringComparison.Ordinal);
    }
}
