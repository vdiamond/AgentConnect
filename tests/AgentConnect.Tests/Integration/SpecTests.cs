using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentConnect.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AgentConnect.Tests.Integration;

[Collection("platform")]
public sealed class SpecTests
{
    private const string TaskSchema = """
        {"properties":[
          {"name":"title","type":"String","required":true},
          {"name":"status","type":"String","required":true,"enum":["open","blocked","done"]},
          {"name":"priority","type":"Int32","required":true},
          {"name":"due","type":"DateTime","required":true},
          {"name":"done","type":"Boolean","required":true},
          {"name":"tags","type":"String[]"},
          {"name":"owner","type":"String"}
        ]}
        """;

    private readonly Session _session;

    public SpecTests(PlatformFixture platform)
    {
        _session = new Session(platform);
    }

    [Fact]
    [Trait("T", "T-2")]
    public async Task AddMember_IsCaseInsensitiveAndIsolated()
    {
        var first = await _session.CreateTenantAsync("One");
        var email = await _session.AddMemberAsync(first.Token, "ann");
        var again = await _session.SendAsync(HttpMethod.Post, "/members", new { email = " " + email.ToUpperInvariant() + " " }, first.Token);
        Assert.Equal(409, again.Status);
        Assert.Equal("email: already exists", Error(again.Body));
        var second = await _session.CreateTenantAsync("Two");
        var other = await _session.SendAsync(HttpMethod.Post, "/members", new { email }, second.Token);
        Assert.Equal(200, other.Status);
        Assert.Empty(_session.Platform.Email.For(email));
    }

    [Fact]
    [Trait("T", "T-3")]
    [Trait("T", "T-8")]
    [Trait("T", "T-42")]
    public async Task InvitationAndToken_KeepTheStoredSecretUntilRegenerate()
    {
        var owner = await _session.CreateTenantAsync();
        var email = await _session.AddMemberAsync(owner.Token, "ann");
        var first = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/send-invitation", new { }, owner.Token);
        var second = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/send-invitation", new { }, owner.Token);
        Assert.True(Json(first.Body).GetProperty("emailSent").GetBoolean());
        Assert.True(Json(second.Body).GetProperty("emailSent").GetBoolean());
        var mails = _session.Platform.Email.For(email);
        var token1 = Session.TokenFrom(mails[0].Body);
        Assert.Equal(token1, Session.TokenFrom(mails[1].Body));
        var sent = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/token/send", new { }, owner.Token);
        Assert.True(Json(sent.Body).GetProperty("emailSent").GetBoolean());
        Assert.Equal(token1, Session.TokenFrom(_session.Platform.Email.For(email).Last().Body));
        Assert.DoesNotContain(EmailTemplates.PreviousTokenLine, _session.Platform.Email.For(email)[^2].Body, StringComparison.Ordinal);

        var regenerated = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/token/regenerate", new { }, owner.Token);
        Assert.Equal(200, regenerated.Status);
        var oldCall = await _session.SendAsync(HttpMethod.Get, "/tenant", null, token1);
        Assert.Equal(401, oldCall.Status);
        var tokenMail = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/token/send", new { }, owner.Token);
        Assert.True(Json(tokenMail.Body).GetProperty("emailSent").GetBoolean());
        var latest = _session.Platform.Email.For(email).Last();
        Assert.Contains(EmailTemplates.PreviousTokenLine, latest.Body, StringComparison.Ordinal);
        var token2 = Session.TokenFrom(latest.Body);
        Assert.NotEqual(token1, token2);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, token2)).Status);

        var member = await MemberAsync(owner.TenantId, email);
        Assert.False(string.IsNullOrEmpty(member.TokenHash));
        Assert.False(string.IsNullOrEmpty(member.TokenEncrypted));
        Assert.DoesNotContain(token2, member.TokenHash, StringComparison.Ordinal);
        Assert.DoesNotContain(token2, member.TokenEncrypted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("T", "T-4")]
    [Trait("T", "T-6")]
    [Trait("T", "T-7")]
    public async Task DeleteMember_InvalidatesTheToken_AndSelfIsForbidden()
    {
        var owner = await _session.CreateTenantAsync();
        var email = await _session.AddMemberAsync(owner.Token, "bob");
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/send-invitation", new { }, owner.Token);
        var token = Session.TokenFrom(_session.Platform.Email.For(email).Last().Body);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/members/" + Uri.EscapeDataString(email), null, owner.Token)).Status);
        Assert.Equal(401, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, token)).Status);
        var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        mcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var mcpResponse = await _session.Platform.McpHttp.SendAsync(mcp);
        Assert.Equal(401, (int)mcpResponse.StatusCode);

        var self = await _session.SendAsync(HttpMethod.Delete, "/members/" + Uri.EscapeDataString(owner.Email), null, owner.Token);
        Assert.Equal(403, self.Status);
        Assert.Equal("$: not allowed on yourself", Error(self.Body));

        var members = _session.Platform.Api.Services.GetRequiredService<MemberService>();
        var caller = await CallerAsync(owner.TenantId, owner.Email);
        var selfFlag = await members.SetTenantOwnerAsync(caller, owner.Email, false, CancellationToken.None);
        Assert.Equal("$: not allowed on yourself", selfFlag.Error!.Text);
        var other = await _session.AddMemberAsync(owner.Token, "second-owner");
        var promoted = await members.SetTenantOwnerAsync(await CallerAsync(owner.TenantId, owner.Email), other, true, CancellationToken.None);
        Assert.True(promoted.IsSuccess);
        var demoted = await members.SetTenantOwnerAsync(await CallerAsync(owner.TenantId, owner.Email), other, false, CancellationToken.None);
        Assert.False(demoted.Value!.IsTenantOwner);
    }

    [Fact]
    [Trait("T", "T-9")]
    [Trait("T", "T-10")]
    [Trait("T", "T-12")]
    [Trait("T", "T-39")]
    public async Task WorkspaceRoles_ReplaceAndProtectTheOwner()
    {
        var owner = await _session.CreateTenantAsync();
        var workspace = await CreateWorkspaceAsync(owner.Token, "Ops");
        var writer = await _session.AddMemberAsync(owner.Token, "writer");
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writer, role = "WorkspaceWrite" }, owner.Token)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/members/" + Uri.EscapeDataString(writer), new { role = "WorkspaceRead" }, owner.Token)).Status);
        var listed = Json(await Body(HttpMethod.Get, "/workspaces/" + workspace + "/members", owner.Token));
        var role = listed.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("email").GetString() == writer).GetProperty("role").GetString();
        Assert.Equal("WorkspaceRead", role);

        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writer, role = "WorkspaceOwner" }, owner.Token);
        var writerToken = await InviteTokenAsync(owner.Token, writer);
        var self = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/members/" + Uri.EscapeDataString(writer), new { role = "WorkspaceWrite" }, writerToken);
        Assert.Equal(403, self.Status);
        Assert.Equal("$: not allowed on yourself", Error(self.Body));
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writer, role = "WorkspaceRead" }, owner.Token);
        var otherOwner = await _session.AddMemberAsync(owner.Token, "other-owner");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = otherOwner, role = "WorkspaceOwner" }, owner.Token);
        var otherToken = await InviteTokenAsync(owner.Token, otherOwner);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/members/" + Uri.EscapeDataString(otherOwner), null, owner.Token)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, otherToken)).Status);

        var second = await CreateWorkspaceAsync(owner.Token, "Other");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + second + "/members", new { email = writer, role = "WorkspaceRead" }, owner.Token);
        await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/members/" + Uri.EscapeDataString(writer), null, owner.Token);
        var writerOnly = await InviteTokenAsync(owner.Token, writer);
        var workspaces = Json(await Body(HttpMethod.Get, "/workspaces", writerOnly));
        Assert.Single(workspaces.GetProperty("workspaces").EnumerateArray());

        await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/members/" + Uri.EscapeDataString(owner.Email), null, owner.Token);
        var reader = writer;
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = reader, role = "WorkspaceRead" }, owner.Token);
        var still = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, await InviteTokenAsync(owner.Token, reader));
        Assert.Equal(200, still.Status);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Status);
    }

    [Fact]
    [Trait("T", "T-11")]
    [Trait("T", "T-13")]
    [Trait("T", "T-14")]
    [Trait("T", "T-15")]
    [Trait("T", "T-16")]
    [Trait("T", "T-17")]
    [Trait("T", "T-18")]
    public async Task ListsAndWorkspaces_FollowTheRoleRules()
    {
        var owner = await _session.CreateTenantAsync("Lists");
        var w1 = await CreateWorkspaceAsync(owner.Token, "W1");
        var w2 = await CreateWorkspaceAsync(owner.Token, "W2");
        var w3 = await CreateWorkspaceAsync(owner.Token, "W3");
        var readerEmail = await _session.AddMemberAsync(owner.Token, "reader");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + w1 + "/members", new { email = readerEmail, role = "WorkspaceRead" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + w2 + "/members", new { email = readerEmail, role = "WorkspaceWrite" }, owner.Token);
        var reader = await InviteTokenAsync(owner.Token, readerEmail);
        var seen = Json(await Body(HttpMethod.Get, "/workspaces", reader)).GetProperty("workspaces").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "W1", "W2" }, seen);
        var all = Json(await Body(HttpMethod.Get, "/workspaces", owner.Token)).GetProperty("workspaces").EnumerateArray().Select(item => item.GetProperty("role").ValueKind).ToArray();
        Assert.Contains(JsonValueKind.Null, all);

        var noRole = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + w1 + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token);
        Assert.Equal(403, noRole.Status);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + w1 + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var created = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + w1 + "/lists", new { name = "Tasks", description = "Work", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token);
        Assert.Equal(200, created.Status);
        Assert.Equal(1, Json(created.Body).GetProperty("schemaVersion").GetInt32());
        var invalid = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + w1 + "/lists", new { name = "Bad", schema = JsonDocument.Parse("""{"properties":[{"name":"n","type":"Int32","enum":["1"]}]}""").RootElement }, owner.Token);
        Assert.Equal(400, invalid.Status);
        Assert.Equal("n/enum: enum only allowed on String or String[]", Error(invalid.Body));

        var lists = Json(await Body(HttpMethod.Get, "/workspaces/" + w1 + "/lists", reader));
        Assert.Equal("Tasks", lists.GetProperty("lists")[0].GetProperty("name").GetString());
        Assert.Equal("Work", lists.GetProperty("lists")[0].GetProperty("description").GetString());

        var listId = Json(created.Body).GetProperty("id").GetString()!;
        var schema = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + w1 + "/lists/" + listId + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token);
        Assert.Equal(200, schema.Status);
        Assert.Equal(2, Json(schema.Body).GetProperty("schemaVersion").GetInt32());
        var badSchema = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + w1 + "/lists/" + listId + "/schema", JsonDocument.Parse("""{"properties":[{"name":"n","type":"Nope"}]}""").RootElement, owner.Token);
        Assert.Equal(400, badSchema.Status);
        Assert.Equal(2, Json(await Body(HttpMethod.Get, "/workspaces/" + w1 + "/lists/" + listId + "/schema", reader)).GetProperty("schemaVersion").GetInt32());

        var renamed = await _session.ToolAsync(owner.Token, "update_workspace", new Dictionary<string, object?> { ["workspaceId"] = w1, ["name"] = "W1b", ["description"] = "Notes" });
        Assert.False(renamed.IsError);
        var writerDenied = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + w2, new { name = "Nope" }, reader);
        Assert.Equal(403, writerDenied.Status);
        _ = w3;
    }

    [Fact]
    [Trait("T", "T-19")]
    [Trait("T", "T-20")]
    [Trait("T", "T-21")]
    [Trait("T", "T-22")]
    [Trait("T", "T-24")]
    [Trait("T", "T-30")]
    [Trait("T", "T-31")]
    public async Task ItemsAndTenantLifecycle_MatchTheSpec()
    {
        var owner = await _session.CreateTenantAsync("Items");
        var workspace = await CreateWorkspaceAsync(owner.Token, "Ops");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var createdList = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token);
        Assert.Equal(200, createdList.Status);
        var list = Json(createdList.Body).GetProperty("id").GetString()!;
        var item = new { title = "Fix login", status = "open", priority = 1, due = "2026-11-25T09:00:00Z", done = false };
        var withId = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/task-1", item, owner.Token);
        Assert.Equal(200, withId.Status);
        Assert.Equal(1, Json(withId.Body).GetProperty("schemaVersion").GetInt32());
        var generated = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["data"] = Session.Node(JsonSerializer.Serialize(item)),
        });
        Assert.False(generated.IsError);
        Assert.True(Guid.TryParse(Json(generated.Text).GetProperty("id").GetString(), out _));

        var bad = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/bad", new { title = 1, status = "open", priority = 1, due = "2026-11-25T09:00:00Z", done = false }, owner.Token);
        Assert.Equal(400, bad.Status);
        Assert.Equal("title: expected String, found Number", Error(bad.Body));
        Assert.Equal(404, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items/bad", null, owner.Token)).Status);

        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token);
        var rewritten = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/task-1", item, owner.Token);
        Assert.Equal(2, Json(rewritten.Body).GetProperty("schemaVersion").GetInt32());
        var read = Json(await Body(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items/task-1", owner.Token));
        Assert.Equal("Fix login", read.GetProperty("data").GetProperty("title").GetString());

        var readerEmail = await _session.AddMemberAsync(owner.Token, "reader");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = readerEmail, role = "WorkspaceRead" }, owner.Token);
        var reader = await InviteTokenAsync(owner.Token, readerEmail);
        var denied = await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/lists/" + list + "/items/task-1", null, reader);
        Assert.Equal(403, denied.Status);
        var writerEmail = await _session.AddMemberAsync(owner.Token, "writer");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writerEmail, role = "WorkspaceWrite" }, owner.Token);
        var writer = await InviteTokenAsync(owner.Token, writerEmail);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/lists/" + list + "/items/task-1", null, writer)).Status);

        var tenantId = owner.TenantId;
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + tenantId + "/suspend", null, null, admin: true)).Status);
        Assert.Equal(403, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, owner.Token)).Status);
        Assert.Equal("$: tenant suspended", Error((await _session.SendAsync(HttpMethod.Get, "/workspaces", null, owner.Token)).Body));
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + tenantId + "/suspend", null, null, admin: true)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + tenantId + "/resume", null, null, admin: true)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, owner.Token)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + tenantId + "/resume", null, null, admin: true)).Status);
        Assert.Equal(404, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + Guid.NewGuid() + "/suspend", null, null, admin: true)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/tenants/" + tenantId, null, null, admin: true)).Status);
        Assert.Equal(401, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, owner.Token)).Status);
        await _session.Platform.Api.Services.GetRequiredService<DeletionService>().RunUntilQuietAsync(CancellationToken.None);
        Assert.Null(await _session.Platform.Database.GetAsync(tenantId, CancellationToken.None));
    }

    #region private

    private async Task<string> CreateWorkspaceAsync(string token, string name)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name }, token);
        Assert.Equal(200, response.Status);
        return Json(response.Body).GetProperty("id").GetString()!;
    }

    private async Task<string> InviteTokenAsync(string ownerToken, string email)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/send-invitation", new { }, ownerToken);
        Assert.Equal(200, response.Status);
        return Session.TokenFrom(_session.Platform.Email.For(email).Last().Body);
    }

    private async Task<string> Body(HttpMethod method, string url, string token)
    {
        var response = await _session.SendAsync(method, url, null, token);
        Assert.Equal(200, response.Status);
        return response.Body;
    }

    private async Task<MemberDocument> MemberAsync(string tenantId, string email)
    {
        var page = await _session.Platform.Database.ListAsync(tenantId, 100, null, CancellationToken.None);
        return page.Items.Single(member => EmailRules.Same(member.Email, email));
    }

    private async Task<Caller> CallerAsync(string tenantId, string email)
    {
        var member = await MemberAsync(tenantId, email);
        var tenant = await _session.Platform.Database.GetAsync(tenantId, CancellationToken.None);
        return new Caller(member, tenant!);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Error(string json)
    {
        return Json(json).GetProperty("error").GetString()!;
    }

    #endregion
}
