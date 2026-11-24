extern alias ApiHost;

using System.Net.Http.Json;
using System.Text.Json;
using AgentConnect.Tests.Support;

namespace AgentConnect.Tests.Integration;

[Collection("platform")]
public sealed class BootTests
{
    private readonly PlatformFixture _platform;

    public BootTests(PlatformFixture platform)
    {
        _platform = platform;
    }

    [Fact]
    [Trait("T", "T-1")]
    [Trait("T", "T-28")]
    public async Task PostTenants_WithAndWithoutAdminKey_FollowsFr1()
    {
        var email = "boot-owner@example.com";
        var created = await _platform.SendAsync(HttpMethod.Post, "/tenants", new { name = "Boot", email }, null, admin: true);
        Assert.Equal(200, created.Status);
        using var document = JsonDocument.Parse(created.Body);
        Assert.Equal("Active", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(100, document.RootElement.GetProperty("pageSizeDefault").GetInt32());
        Assert.Equal(1000, document.RootElement.GetProperty("pageSizeMax").GetInt32());
        Assert.True(document.RootElement.GetProperty("emailSent").GetBoolean());
        var mail = Assert.Single(_platform.Email.For(email));
        Assert.Contains("Token:", mail.Body, StringComparison.Ordinal);

        var denied = await _platform.SendAsync(HttpMethod.Post, "/tenants", new { name = "Nope", email = "other@example.com" }, null, admin: false);
        Assert.Equal(401, denied.Status);
        Assert.Equal("$: invalid admin key", PlatformFixture.ErrorOf(denied.Body));

        var missing = await _platform.SendAsync(HttpMethod.Get, "/tenant", null, token: null, admin: false);
        Assert.Equal(401, missing.Status);
        Assert.Equal("$: invalid token", PlatformFixture.ErrorOf(missing.Body));
    }
}
