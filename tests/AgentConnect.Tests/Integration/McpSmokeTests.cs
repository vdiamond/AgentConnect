using AgentConnect.Tests.Support;

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
    public async Task Mcp_ExposesTheNineteenTools()
    {
        var email = "mcp-owner@example.com";
        var created = await _platform.SendAsync(HttpMethod.Post, "/tenants", new { name = "Mcp", email }, null, admin: true);
        Assert.Equal(200, created.Status);
        var token = Session.TokenFrom(Assert.Single(_platform.Email.For(email)).Body);
        await using var client = await _platform.McpClientAsync(token);
        var tools = await client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "batch",
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
                "query_members",
                "revoke_workspace_access",
                "set_tenant_owner",
                "update_list",
                "update_schema",
                "update_workspace",
                "upsert_item",
            },
            names);
    }
}
