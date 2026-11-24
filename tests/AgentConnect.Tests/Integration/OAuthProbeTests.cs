using System.Net.Http.Headers;
using AgentConnect.Tests.Support;

namespace AgentConnect.Tests.Integration;

/// <summary>
/// OAuth discovery probes must not look like a protected resource, and the MCP endpoint
/// challenges with a static bearer scheme.
/// </summary>
[Collection("platform")]
public sealed class OAuthProbeTests
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthProbeTests"/> class.
    /// </summary>
    /// <param name="platform">The shared platform.</param>
    public OAuthProbeTests(PlatformFixture platform)
    {
        _platform = platform;
    }

    [Fact]
    public async Task GetWellKnown_Unauthenticated_ReturnsNotFoundOnBothHosts()
    {
        string[] paths =
        [
            "/.well-known/oauth-protected-resource",
            "/.well-known/oauth-protected-resource/mcp",
            "/.well-known/oauth-authorization-server",
            "/.well-known/openid-configuration",
        ];
        HttpClient[] clients = [_platform.ApiClient, _platform.McpHttp];

        foreach (var client in clients)
        {
            foreach (var path in paths)
            {
                var response = await SendAsync(client, HttpMethod.Get, path);
                Assert.Equal(404, response.Status);
                Assert.Equal("""{"error":"$: not found"}""", response.Body);
                Assert.Equal("", response.Challenge);
            }

            var ignoredToken = await SendAsync(client, HttpMethod.Get, "/.well-known/oauth-protected-resource", "not-a-token");
            Assert.Equal(404, ignoredToken.Status);
            Assert.Equal("""{"error":"$: not found"}""", ignoredToken.Body);
            Assert.Equal("", ignoredToken.Challenge);
        }
    }

    [Fact]
    public async Task PostMcp_MissingOrInvalidToken_ChallengesBearerWithoutResourceMetadata()
    {
        var missing = await SendAsync(_platform.McpHttp, HttpMethod.Post, "/mcp");
        Assert.Equal(401, missing.Status);
        Assert.Equal("""{"error":"$: invalid token"}""", missing.Body);
        Assert.Equal("Bearer", missing.Challenge);

        var invalid = await SendAsync(_platform.McpHttp, HttpMethod.Post, "/mcp", "not-a-token");
        Assert.Equal(401, invalid.Status);
        Assert.Equal("""{"error":"$: invalid token"}""", invalid.Body);
        Assert.Equal("Bearer", invalid.Challenge);

        var missingGet = await SendAsync(_platform.McpHttp, HttpMethod.Get, "/mcp");
        Assert.Equal(401, missingGet.Status);
        Assert.Equal("""{"error":"$: invalid token"}""", missingGet.Body);
        Assert.Equal("Bearer", missingGet.Challenge);

        var apiDenied = await SendAsync(_platform.ApiClient, HttpMethod.Get, "/tenant");
        Assert.Equal(401, apiDenied.Status);
        Assert.Equal("""{"error":"$: invalid token"}""", apiDenied.Body);
        Assert.Equal("", apiDenied.Challenge);
    }

    [Fact]
    public async Task PostWellKnown_Unauthenticated_StillRejectsTheCaller()
    {
        HttpClient[] clients = [_platform.ApiClient, _platform.McpHttp];
        foreach (var client in clients)
        {
            var response = await SendAsync(client, HttpMethod.Post, "/.well-known/oauth-protected-resource");
            Assert.Equal(401, response.Status);
            Assert.Equal("""{"error":"$: invalid token"}""", response.Body);
            Assert.Equal("", response.Challenge);
        }
    }

    #region private

    private readonly PlatformFixture _platform;

    private static async Task<(int Status, string Body, string Challenge)> SendAsync(HttpClient client, HttpMethod method, string url, string? token = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body, response.Headers.WwwAuthenticate.ToString());
    }

    #endregion
}
