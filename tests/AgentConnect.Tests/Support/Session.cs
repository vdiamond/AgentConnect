using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentConnect.Tests.Support;

/// <summary>Helpers shared by the integration tests.</summary>
public sealed class Session
{
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="Session"/> class.
    /// </summary>
    /// <param name="platform">The platform.</param>
    public Session(PlatformFixture platform)
    {
        Platform = platform;
    }

    /// <summary>Gets the platform.</summary>
    public PlatformFixture Platform { get; }

    /// <summary>Creates a tenant and returns the owner token.</summary>
    /// <param name="name">The tenant name.</param>
    /// <returns>The tenant id, owner email, and token.</returns>
    public async Task<(string TenantId, string Email, string Token)> CreateTenantAsync(string name = "Acme")
    {
        var email = Next("owner");
        var created = await Platform.SendAsync(HttpMethod.Post, "/tenants", new { name, email }, null, admin: true);
        Assert.Equal(200, created.Status);
        using var document = JsonDocument.Parse(created.Body);
        return (document.RootElement.GetProperty("id").GetString()!, email, TokenFrom(Platform.Email.For(email).Last().Body));
    }

    /// <summary>Adds a member and returns the email. No mail is sent.</summary>
    /// <param name="ownerToken">The owner token.</param>
    /// <param name="prefix">The email prefix.</param>
    /// <returns>The email.</returns>
    public async Task<string> AddMemberAsync(string ownerToken, string prefix = "member")
    {
        var email = Next(prefix);
        var response = await Platform.SendAsync(HttpMethod.Post, "/members", new { email }, ownerToken, admin: false);
        Assert.Equal(200, response.Status);
        return email;
    }

    /// <summary>Sends a JSON request and returns the status and parsed body.</summary>
    /// <param name="method">The method.</param>
    /// <param name="url">The URL.</param>
    /// <param name="body">The body.</param>
    /// <param name="token">The token.</param>
    /// <param name="admin">Whether to send the admin key.</param>
    /// <returns>The status and JSON, when the body is JSON.</returns>
    public Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, object? body, string? token, bool admin = false)
    {
        return SendRawAsync(method, url, body, token, admin);
    }

    /// <summary>Calls an MCP tool and returns the text payload.</summary>
    /// <param name="token">The token.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>Whether the tool errored, and the text.</returns>
    public async Task<(bool IsError, string Text)> ToolAsync(string token, string name, Dictionary<string, object?>? arguments = null)
    {
        await using var client = await Platform.McpClientAsync(token);
        var result = await client.CallToolAsync(name, arguments ?? []);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return (result.IsError == true, text);
    }

    /// <summary>Parses a JSON object argument.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The node.</returns>
    public static JsonNode Node(string json)
    {
        return JsonNode.Parse(json)!;
    }

    /// <summary>Reads a token line from an email body.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The token.</returns>
    public static string TokenFrom(string body)
    {
        if (EmailTemplates.TryReadToken(body, out var token))
        {
            return token;
        }

        throw new InvalidOperationException("The email has no token cell.");
    }

    #region private

    private async Task<(int Status, string Body)> SendRawAsync(HttpMethod method, string url, object? body, string? token, bool admin)
    {
        var response = await Platform.SendAsync(method, url, body, token, admin);
        return (response.Status, response.Body);
    }

    private string Next(string prefix)
    {
        _count++;
        return prefix + "-" + _count.ToString() + "-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
    }

    #endregion
}
