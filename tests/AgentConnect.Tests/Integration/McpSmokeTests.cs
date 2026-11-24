using System.Text.Json;
using AgentConnect.Tests.Support;
using ModelContextProtocol.Protocol;

namespace AgentConnect.Tests.Integration;

[Collection("platform")]
public sealed class McpSmokeTests
{
    private readonly PlatformFixture _platform;

    public McpSmokeTests(PlatformFixture platform)
    {
        _platform = platform;
    }

    [Fact]
    [Trait("T", "T-26")]
    public async Task Mcp_ExposesTheSixteenTools()
    {
        var email = "mcp-owner@example.com";
        var created = await _platform.SendAsync(HttpMethod.Post, "/tenants", new { name = "Mcp", email }, null, admin: true);
        Assert.Equal(200, created.Status);
        var token = Token(Assert.Single(_platform.Email.For(email)).Body);
        await using var client = await _platform.McpClientAsync(token);
        var tools = await client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "change_workspace_role",
                "create_list",
                "delete_item",
                "delete_list",
                "get_item",
                "get_schema",
                "invite_member",
                "list_lists",
                "list_workspace_members",
                "list_workspaces",
                "query_items",
                "revoke_workspace_access",
                "update_list",
                "update_schema",
                "update_workspace",
                "upsert_item",
            },
            names);
    }

    private static string Token(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("Token: ", StringComparison.Ordinal))
            {
                return line["Token: ".Length..].Trim();
            }
        }

        throw new InvalidOperationException(body);
    }
}
