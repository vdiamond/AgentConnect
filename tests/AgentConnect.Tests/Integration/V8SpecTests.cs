using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentConnect.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AgentConnect.Tests.Integration;

[Collection("platform")]
public sealed class V8SpecTests
{
    private const string TaskSchema = """
        {"properties":[
          {"name":"title","type":"String","required":true},
          {"name":"status","type":"String","required":true,"enum":["open","blocked","done"]},
          {"name":"priority","type":"Int32","required":true},
          {"name":"due","type":"DateTime","required":true},
          {"name":"done","type":"Boolean","required":true},
          {"name":"tags","type":"String[]","required":true},
          {"name":"owner","type":"String"}
        ]}
        """;

    private readonly Session _session;

    public V8SpecTests(PlatformFixture platform)
    {
        _session = new Session(platform);
    }

    [Fact]
    [Trait("T", "T-6")]
    [Trait("T", "T-7")]
    [Trait("T", "T-65")]
    public async Task TenantOwnerSurface_ChecksTheCallerBeforeTheEmail()
    {
        var owner = await _session.CreateTenantAsync("Owners");
        var workspace = await WorkspaceAsync(owner.Token);
        var writer = await _session.AddMemberAsync(owner.Token, "writer");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writer, role = "WorkspaceOwner" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(writer) + "/send-invitation", new { }, owner.Token);
        var writerToken = Session.TokenFrom(_session.Platform.Email.For(writer).Last().Body);

        var toolDenied = await _session.ToolAsync(writerToken, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = "missing@example.com",
            ["isTenantOwner"] = true,
        });
        Assert.True(toolDenied.IsError);
        Assert.Equal("$: not allowed", toolDenied.Text);
        var restDenied = await _session.SendAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString("missing@example.com") + "/tenant-owner", new { isTenantOwner = true }, writerToken);
        Assert.Equal(403, restDenied.Status);
        Assert.Equal("$: not allowed", Error(restDenied.Body));

        var selfTool = await _session.ToolAsync(owner.Token, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = owner.Email,
            ["isTenantOwner"] = true,
        });
        Assert.Equal("$: not allowed on yourself", selfTool.Text);
        var selfRest = await _session.SendAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString(owner.Email) + "/tenant-owner", new { isTenantOwner = false }, owner.Token);
        Assert.Equal(403, selfRest.Status);
        Assert.Equal("$: not allowed on yourself", Error(selfRest.Body));

        var missingTool = await _session.ToolAsync(owner.Token, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = writer,
        });
        Assert.Equal("isTenantOwner: required", missingTool.Text);
        var missingRest = await RawAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString(writer) + "/tenant-owner", "{}", owner.Token);
        Assert.Equal(400, missingRest.Status);
        Assert.Equal("isTenantOwner: required", Error(missingRest.Body));
        var yes = await _session.ToolAsync(owner.Token, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = writer,
            ["isTenantOwner"] = "yes",
        });
        Assert.Equal("isTenantOwner: expected Boolean, found String", yes.Text);
        var yesRest = await RawAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString(writer) + "/tenant-owner", """{"isTenantOwner":"yes"}""", owner.Token);
        Assert.Equal("isTenantOwner: expected Boolean, found String", Error(yesRest.Body));
        var unknown = await _session.SendAsync(HttpMethod.Put, "/members/nobody@example.com/tenant-owner", new { isTenantOwner = true }, owner.Token);
        Assert.Equal(404, unknown.Status);
        Assert.Equal("$: not found", Error(unknown.Body));

        var granted = await _session.SendAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString(writer) + "/tenant-owner", new { isTenantOwner = true }, owner.Token);
        Assert.Equal(200, granted.Status);
        Assert.True(Json(granted.Body).GetProperty("isTenantOwner").GetBoolean());
        var removed = await _session.ToolAsync(owner.Token, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = writer,
            ["isTenantOwner"] = false,
        });
        Assert.False(removed.IsError);
        Assert.False(Json(removed.Text).GetProperty("isTenantOwner").GetBoolean());
        var beforeRepeat = _session.Platform.Audit.ForTenant(owner.TenantId).Count(entry => entry.Action.Contains("TenantOwner", StringComparison.Ordinal));
        var again = await _session.ToolAsync(owner.Token, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = writer,
            ["isTenantOwner"] = false,
        });
        Assert.False(again.IsError);
        Assert.Equal(beforeRepeat, _session.Platform.Audit.ForTenant(owner.TenantId).Count(entry => entry.Action.Contains("TenantOwner", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("T", "T-30")]
    public async Task SuspendedTenant_IsRejectedOnTheMcpConnection()
    {
        var owner = await _session.CreateTenantAsync("Suspended");
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + owner.TenantId + "/suspend", null, null, admin: true)).Status);
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var response = await _session.Platform.McpHttp.SendAsync(request);
        Assert.Equal(403, (int)response.StatusCode);
        Assert.Equal("$: tenant suspended", Error(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    [Trait("T", "T-52")]
    public async Task NullPageArguments_UseTheFirstPage()
    {
        var owner = await _session.CreateTenantAsync("Pages");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        var jsonNull = JsonSerializer.Deserialize<JsonElement>("null");
        var tool = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = "ids",
            ["pageSize"] = jsonNull,
            ["continuation"] = jsonNull,
        });
        Assert.False(tool.IsError);
        Assert.Equal(JsonValueKind.Array, Json(tool.Text).GetProperty("items").ValueKind);
        var rest = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&pageSize=null&continuation=null", null, owner.Token);
        Assert.Equal(200, rest.Status);
        Assert.Equal(JsonValueKind.Array, Json(rest.Body).GetProperty("items").ValueKind);
    }

    [Fact]
    [Trait("T", "T-66")]
    public async Task TenantOwner_UsesMembershipToolsWithoutAWorkspaceRole()
    {
        var owner = await _session.CreateTenantAsync("Remote");
        var workspace = await WorkspaceAsync(owner.Token);
        var email = "guest-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        var invited = await _session.ToolAsync(owner.Token, "invite_member", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = email,
            ["role"] = "WorkspaceRead",
        });
        Assert.False(invited.IsError);
        var changed = await _session.ToolAsync(owner.Token, "change_workspace_role", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = email,
            ["role"] = "WorkspaceWrite",
        });
        Assert.False(changed.IsError);
        var listed = await _session.ToolAsync(owner.Token, "list_workspace_members", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
        });
        Assert.False(listed.IsError);
        Assert.Contains(email, listed.Text, StringComparison.Ordinal);
        var revoked = await _session.ToolAsync(owner.Token, "revoke_workspace_access", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = email,
        });
        Assert.False(revoked.IsError);
    }

    [Fact]
    [Trait("T", "T-67")]
    public async Task Members_AreOrderedByCreated_AndWorkspaceMembersByEmail()
    {
        var owner = await _session.CreateTenantAsync("Order");
        var workspace = await WorkspaceAsync(owner.Token);
        var cy = "cy-" + Guid.NewGuid().ToString("N")[..6] + "@example.com";
        var ann = "ann-" + Guid.NewGuid().ToString("N")[..6] + "@example.com";
        var bob = "Bob-" + Guid.NewGuid().ToString("N")[..6] + "@example.com";
        foreach (var email in new[] { cy, ann, bob })
        {
            await _session.SendAsync(HttpMethod.Post, "/members", new { email }, owner.Token);
            await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email, role = "WorkspaceRead" }, owner.Token);
        }

        var members = Json((await _session.SendAsync(HttpMethod.Get, "/members", null, owner.Token)).Body).GetProperty("members");
        var stored = new List<MemberDocument>();
        await foreach (var page in PagesAsync(owner.TenantId))
        {
            stored.Add(page);
        }

        var expected = stored.OrderBy(member => member.Created).ThenBy(member => member.Id, StringComparer.Ordinal).Select(member => member.Email).ToArray();
        var actual = members.EnumerateArray().Select(member => member.GetProperty("email").GetString()).ToArray();
        Assert.Equal(expected, actual);
        Assert.Contains(stored, member => member.Email == bob && member.EmailLower == bob.ToLowerInvariant());

        var workspaceMembers = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/members", null, owner.Token)).Body).GetProperty("members");
        var emails = workspaceMembers.EnumerateArray().Select(member => member.GetProperty("email").GetString()!).ToArray();
        Assert.Equal(emails.OrderBy(email => email, StringComparer.OrdinalIgnoreCase).ToArray(), emails);
        var tool = await _session.ToolAsync(owner.Token, "list_workspace_members", new Dictionary<string, object?> { ["workspaceId"] = workspace });
        var toolEmails = Json(tool.Text).GetProperty("members").EnumerateArray().Select(member => member.GetProperty("email").GetString()!).ToArray();
        Assert.Equal(emails, toolEmails);
    }

    [Fact]
    [Trait("T", "T-68")]
    [Trait("T", "T-80")]
    public async Task Bodies_RejectNonObjects_AndAnEmptySchema()
    {
        var owner = await _session.CreateTenantAsync("Bodies");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        var role = await RawAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", """{"email":"a@example.com","role":"Admin"}""", owner.Token);
        Assert.Equal(400, role.Status);
        Assert.Equal("role: invalid", Error(role.Body));
        var broken = await RawAsync(HttpMethod.Post, "/members", "{", owner.Token);
        Assert.Equal(400, broken.Status);
        Assert.Equal("$: malformed JSON", Error(broken.Body));
        var array = await RawAsync(HttpMethod.Post, "/members", "[1]", owner.Token);
        Assert.Equal("$: expected Object, found Array", Error(array.Body));
        var schema = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", "[1]", owner.Token);
        Assert.Equal("$: expected Object, found Array", Error(schema.Body));
        var item = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/a", "[1]", owner.Token);
        Assert.Equal("$: expected Object, found Array", Error(item.Body));
        var version = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/schema", null, owner.Token)).Body).GetProperty("schemaVersion").GetInt32();
        var empty = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", "", owner.Token);
        var spaces = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", "   ", owner.Token);
        Assert.Equal("schema: required", Error(empty.Body));
        Assert.Equal("schema: required", Error(spaces.Body));
        Assert.Equal(version, Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/schema", null, owner.Token)).Body).GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    [Trait("T", "T-69")]
    public async Task DeletedNames_CanBeReused()
    {
        var owner = await _session.CreateTenantAsync("Reuse");
        var first = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" }, owner.Token)).Body).GetProperty("id").GetString()!;
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + first, null, owner.Token)).Status);
        var second = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "ops" }, owner.Token);
        Assert.Equal(200, second.Status);
        var workspace = Json(second.Body).GetProperty("id").GetString()!;
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/lists/" + list, null, owner.Token)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Status);
    }

    [Fact]
    [Trait("T", "T-70")]
    [Trait("T", "T-75")]
    public async Task ToolArgumentsAndRestShapes_MatchSection6()
    {
        Assert.Equal("DoNotReply@azure4smb.com", new AgentConnectOptions().Email.SenderAddress);
        var owner = await _session.CreateTenantAsync("Shapes");
        var tenant = await _session.Platform.Database.GetAsync(owner.TenantId, CancellationToken.None);
        Assert.Null(tenant!.Subscription);
        await using var client = await _session.Platform.McpClientAsync(owner.Token);
        var tools = await client.ListToolsAsync();
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["list_workspaces"] = ["pageSize", "continuation"],
            ["update_workspace"] = ["workspaceId", "name", "description"],
            ["list_lists"] = ["workspaceId", "pageSize", "continuation"],
            ["create_list"] = ["workspaceId", "name", "description", "schema"],
            ["update_list"] = ["workspaceId", "listId", "name", "description"],
            ["delete_list"] = ["workspaceId", "listId"],
            ["get_schema"] = ["workspaceId", "listId"],
            ["update_schema"] = ["workspaceId", "listId", "schema"],
            ["query_items"] = ["workspaceId", "listId", "shape", "schemaVersion", "where", "orderBy", "pageSize", "continuation"],
            ["get_item"] = ["workspaceId", "listId", "itemId"],
            ["upsert_item"] = ["workspaceId", "listId", "itemId", "data"],
            ["delete_item"] = ["workspaceId", "listId", "itemId"],
            ["invite_member"] = ["workspaceId", "email", "role"],
            ["change_workspace_role"] = ["workspaceId", "email", "role"],
            ["revoke_workspace_access"] = ["workspaceId", "email"],
            ["list_workspace_members"] = ["workspaceId", "pageSize", "continuation"],
            ["set_tenant_owner"] = ["email", "isTenantOwner"],
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            var names = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected[tool.Name].Order(StringComparer.Ordinal).ToArray(), names);
        }

        var created = await _session.Platform.SendAsync(HttpMethod.Post, "/tenants", new { name = "Body", email = "body-" + Guid.NewGuid().ToString("N")[..8] + "@example.com" }, null, admin: true);
        Assert.Equal(200, created.Status);
        SameKeys(created.Body, "id", "name", "status", "pageSizeDefault", "pageSizeMax", "firstMemberId", "emailSent");
        var settings = await _session.SendAsync(HttpMethod.Get, "/tenant", null, owner.Token);
        Assert.Equal(200, settings.Status);
        SameKeys(settings.Body, "pageSizeDefault", "pageSizeMax");
        var member = await _session.SendAsync(HttpMethod.Post, "/members", new { email = "shape-" + Guid.NewGuid().ToString("N")[..8] + "@example.com" }, owner.Token);
        SameKeys(member.Body, "id", "email", "isTenantOwner", "invitationSent");
        var flagged = await _session.SendAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString(Json(member.Body).GetProperty("email").GetString()!) + "/tenant-owner", new { isTenantOwner = true }, owner.Token);
        SameKeys(flagged.Body, "id", "email", "isTenantOwner", "invitationSent");
        var workspace = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", description = "Desk" }, owner.Token);
        SameKeys(workspace.Body, "id", "name", "description", "members");
        var workspaceId = Json(workspace.Body).GetProperty("id").GetString()!;
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspaceId + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var patched = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspaceId, new { name = "Ops 2" }, owner.Token);
        SameKeys(patched.Body, "id", "name", "description");
        var added = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspaceId + "/members", new { email = "seat-" + Guid.NewGuid().ToString("N")[..8] + "@example.com", role = "WorkspaceRead" }, owner.Token);
        SameKeys(added.Body, "email", "role", "emailSent");
        var role = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspaceId + "/members/" + Uri.EscapeDataString(Json(added.Body).GetProperty("email").GetString()!), new { role = "WorkspaceWrite" }, owner.Token);
        SameKeys(role.Body, "email", "role");
        var list = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspaceId + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token);
        SameKeys(list.Body, "id", "name", "schemaVersion");
        var listId = Json(list.Body).GetProperty("id").GetString()!;
        var schema = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspaceId + "/lists/" + listId + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token);
        SameKeys(schema.Body, "schema", "schemaVersion");
        var item = await RawAsync(HttpMethod.Put, "/workspaces/" + workspaceId + "/lists/" + listId + "/items/i1", """{"title":"a","status":"open","priority":1,"due":"2026-11-25T00:00:00Z","done":false,"tags":["a"]}""", owner.Token);
        Assert.Equal(200, item.Status);
        SameKeys(item.Body, "id", "schemaVersion");
        var read = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspaceId + "/lists/" + listId + "/items/i1", null, owner.Token);
        SameKeys(read.Body, "id", "schemaVersion", "data");
        var deleted = await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspaceId + "/lists/" + listId + "/items/i1", null, owner.Token);
        SameKeys(deleted.Body, "deleted");
    }

    [Fact]
    [Trait("T", "T-71")]
    public async Task StaleWrites_StopAfterTheConfiguredRetries()
    {
        var owner = await _session.CreateTenantAsync("Retry");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        var first = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token);
        var second = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token);
        Assert.Equal(Json(first.Body).GetProperty("schemaVersion").GetInt32() + 1, Json(second.Body).GetProperty("schemaVersion").GetInt32());

        var api = _session.Platform.Api.Services.GetRequiredService<AgentConnectOptions>();
        var mcp = _session.Platform.Mcp.Services.GetRequiredService<AgentConnectOptions>();
        var database = _session.Platform.Database;
        try
        {
            database.ReplaceConflictsRemaining = 3;
            var renamed = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + list, new { name = "Retried" }, owner.Token);
            Assert.Equal(200, renamed.Status);
            Assert.Equal("Retried", Json(renamed.Body).GetProperty("name").GetString());
            Assert.Equal(0, database.ReplaceConflictsRemaining);

            database.ReplaceConflictsRemaining = 4;
            var conflict = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + list, new { name = "Blocked" }, owner.Token);
            Assert.Equal(409, conflict.Status);
            Assert.Equal("$: conflict", Error(conflict.Body));
            Assert.Equal(0, database.ReplaceConflictsRemaining);
            Assert.Equal("Retried", Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, owner.Token)).Body).GetProperty("lists")[0].GetProperty("name").GetString());

            api.ConcurrencyRetries = 1;
            mcp.ConcurrencyRetries = 1;
            database.ReplaceConflictsRemaining = 1;
            var once = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + list, new { name = "Once" }, owner.Token);
            Assert.Equal(200, once.Status);
            Assert.Equal("Once", Json(once.Body).GetProperty("name").GetString());
            Assert.Equal(0, database.ReplaceConflictsRemaining);

            database.ReplaceConflictsRemaining = 2;
            var twice = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + list, new { name = "Twice" }, owner.Token);
            Assert.Equal(409, twice.Status);
            Assert.Equal("$: conflict", Error(twice.Body));
            Assert.Equal(0, database.ReplaceConflictsRemaining);

            api.ConcurrencyRetries = 3;
            mcp.ConcurrencyRetries = 3;
            database.ReplaceConflictsRemaining = 3;
            var sent = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(owner.Email) + "/token/send", new { }, owner.Token);
            Assert.Equal(200, sent.Status);
            Assert.True(Json(sent.Body).GetProperty("emailSent").GetBoolean());
            Assert.Equal(0, database.ReplaceConflictsRemaining);

            database.ReplaceConflictsRemaining = 4;
            var blocked = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(owner.Email) + "/token/send", new { }, owner.Token);
            Assert.Equal(409, blocked.Status);
            Assert.Equal("$: conflict", Error(blocked.Body));
            Assert.Equal(0, database.ReplaceConflictsRemaining);
        }
        finally
        {
            api.ConcurrencyRetries = 3;
            mcp.ConcurrencyRetries = 3;
            database.ReplaceConflictsRemaining = 0;
        }
    }

    [Fact]
    [Trait("T", "T-72")]
    public async Task UnexpectedStorageFailure_IsAnInternalError()
    {
        var owner = await _session.CreateTenantAsync("Fault");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        _session.Platform.Database.WriteFault = ItemWriteFault.Unexpected;
        var rest = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/x", new { title = "a", status = "open", priority = 1, due = "2026-11-25T00:00:00Z", done = false, tags = new[] { "a" } }, owner.Token);
        Assert.Equal(500, rest.Status);
        Assert.Equal("$: internal error", Error(rest.Body));
        Assert.DoesNotContain("activity", rest.Body, StringComparison.Ordinal);
        _session.Platform.Database.WriteFault = ItemWriteFault.Unexpected;
        var tool = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["itemId"] = "y",
            ["data"] = Session.Node("""{"title":"a","status":"open","priority":1,"due":"2026-11-25T00:00:00Z","done":false,"tags":["a"]}"""),
        });
        Assert.True(tool.IsError);
        Assert.Equal("$: internal error", tool.Text);
    }

    [Fact]
    [Trait("T", "T-73")]
    public async Task DescriptionNull_Clears_AndNameNullDoesNot()
    {
        var owner = await _session.CreateTenantAsync("Nulls");
        var created = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", description = "Desk" }, owner.Token);
        var workspace = Json(created.Body).GetProperty("id").GetString()!;
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var cleared = await RawAsync(HttpMethod.Patch, "/workspaces/" + workspace, """{"description":null}""", owner.Token);
        Assert.Equal(200, cleared.Status);
        Assert.False(Json(cleared.Body).TryGetProperty("description", out _));
        var nameNull = await RawAsync(HttpMethod.Patch, "/workspaces/" + workspace, """{"name":null}""", owner.Token);
        Assert.Equal("name: expected String, found Null", Error(nameNull.Body));
        var list = await ListAsync(owner.Token, workspace);
        var number = await _session.ToolAsync(owner.Token, "update_list", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["name"] = 5,
        });
        Assert.Equal("name: expected String, found Number", number.Text);
        var flag = await _session.ToolAsync(owner.Token, "update_list", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["description"] = true,
        });
        Assert.Equal("description: expected String, found Boolean", flag.Text);
    }

    [Fact]
    [Trait("T", "T-74")]
    public async Task EveryTool_RejectsAnUnknownArgument()
    {
        var owner = await _session.CreateTenantAsync("Unknown");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        var calls = new (string Name, Dictionary<string, object?> Args)[]
        {
            ("list_workspaces", new Dictionary<string, object?> { ["foo"] = 1 }),
            ("update_workspace", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["foo"] = 1 }),
            ("list_lists", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["foo"] = 1 }),
            ("create_list", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["name"] = "n", ["schema"] = Session.Node("{}"), ["foo"] = 1 }),
            ("update_list", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["foo"] = 1 }),
            ("delete_list", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["foo"] = 1 }),
            ("get_schema", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["foo"] = 1 }),
            ("update_schema", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["schema"] = Session.Node("{}"), ["foo"] = 1 }),
            ("query_items", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["shape"] = "ids", ["foo"] = 1 }),
            ("get_item", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["itemId"] = "i", ["foo"] = 1 }),
            ("upsert_item", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["data"] = Session.Node("{}"), ["foo"] = 1 }),
            ("delete_item", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["listId"] = list, ["itemId"] = "i", ["foo"] = 1 }),
            ("invite_member", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["email"] = "a@example.com", ["role"] = "WorkspaceRead", ["foo"] = 1 }),
            ("change_workspace_role", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["email"] = "a@example.com", ["role"] = "WorkspaceRead", ["foo"] = 1 }),
            ("revoke_workspace_access", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["email"] = "a@example.com", ["foo"] = 1 }),
            ("list_workspace_members", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["foo"] = 1 }),
            ("set_tenant_owner", new Dictionary<string, object?> { ["email"] = owner.Email, ["isTenantOwner"] = true, ["foo"] = 1 }),
        };
        foreach (var call in calls)
        {
            var result = await _session.ToolAsync(owner.Token, call.Name, call.Args);
            Assert.True(result.IsError);
            Assert.Equal("foo: unknown field", result.Text);
        }
    }

    [Fact]
    [Trait("T", "T-74")]
    public async Task UnknownArgument_WaitsUntilAccessSucceeds()
    {
        var owner = await _session.CreateTenantAsync("Hidden");
        var workspace = await WorkspaceAsync(owner.Token);
        var outsider = await _session.AddMemberAsync(owner.Token, "outsider");
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(outsider) + "/send-invitation", new { }, owner.Token);
        var token = Session.TokenFrom(_session.Platform.Email.For(outsider).Last().Body);
        var hidden = await _session.ToolAsync(token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = "none",
            ["shape"] = "ids",
            ["foo"] = 1,
        });
        Assert.True(hidden.IsError);
        Assert.Equal("$: not found", hidden.Text);
    }

    [Fact]
    [Trait("T", "T-76")]
    public async Task PercentItemIds_AreRejected()
    {
        var owner = await _session.CreateTenantAsync("Percent");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        var rest = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/a%25b", """{"title":"a","status":"open","priority":1,"due":"2026-11-25T00:00:00Z","done":false,"tags":["a"]}""", owner.Token);
        Assert.Equal(400, rest.Status);
        Assert.Equal("itemId: invalid", Error(rest.Body));
        var tool = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["itemId"] = "a%b",
            ["data"] = Session.Node("""{"title":"a","status":"open","priority":1,"due":"2026-11-25T00:00:00Z","done":false,"tags":["a"]}"""),
        });
        Assert.Equal("itemId: invalid", tool.Text);
    }

    [Fact]
    [Trait("T", "T-77")]
    public async Task DeletedDuringUpdate_ReturnsNotFound()
    {
        var owner = await _session.CreateTenantAsync("Gone");
        var workspace = await WorkspaceAsync(owner.Token);
        var email = await _session.AddMemberAsync(owner.Token, "casey");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email, role = "WorkspaceRead" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/send-invitation", new { }, owner.Token);
        var token = Session.TokenFrom(_session.Platform.Email.For(email).Last().Body);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace, null, owner.Token)).Status);
        Assert.Equal(404, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, token)).Status);

        var next = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + next + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, next);
        _session.Platform.Database.AfterListRead = () =>
        {
            _session.Platform.Database.AfterListRead = null;
            _session.Platform.Database.ForgetList(owner.TenantId, next, list);
        };
        try
        {
            var updated = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + next + "/lists/" + list, new { name = "Moved" }, owner.Token);
            Assert.Equal(404, updated.Status);
            Assert.Equal("$: not found", Error(updated.Body));
        }
        finally
        {
            _session.Platform.Database.AfterListRead = null;
        }

        var softList = await ListAsync(owner.Token, next);
        _session.Platform.Database.AfterListRead = () =>
        {
            _session.Platform.Database.AfterListRead = null;
            var tenant = _session.Platform.Api.Services.GetRequiredService<ITenantRepository>().GetAsync(owner.TenantId, CancellationToken.None).GetAwaiter().GetResult();
            var members = _session.Platform.Api.Services.GetRequiredService<IMemberRepository>().ListAsync(owner.TenantId, 20, null, CancellationToken.None).GetAwaiter().GetResult();
            var member = members.Items.Single(item => EmailRules.Same(item.Email, owner.Email));
            var content = _session.Platform.Api.Services.GetRequiredService<ContentService>();
            var deleted = content.DeleteListAsync(new Caller(member, tenant!), next, softList, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(deleted.IsSuccess);
        };
        try
        {
            var updated = await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + next + "/lists/" + softList, new { name = "Soft" }, owner.Token);
            Assert.Equal(404, updated.Status);
            Assert.Equal("$: not found", Error(updated.Body));
            Assert.Equal(404, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + next + "/lists/" + softList + "/schema", null, owner.Token)).Status);
        }
        finally
        {
            _session.Platform.Database.AfterListRead = null;
        }
    }

    [Fact]
    [Trait("T", "T-78")]
    [Trait("T", "T-79")]
    public async Task RequiredNullsAndNonStringEmails_UseThePinnedTexts()
    {
        var owner = await _session.CreateTenantAsync("Required");
        var workspace = await WorkspaceAsync(owner.Token);
        var number = await _session.ToolAsync(owner.Token, "invite_member", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = JsonSerializer.Deserialize<JsonElement>("5"),
            ["role"] = "WorkspaceRead",
        });
        Assert.Equal("email: invalid format", number.Text);
        var posted = await RawAsync(HttpMethod.Post, "/members", """{"email":5}""", owner.Token);
        Assert.Equal("email: invalid format", Error(posted.Body));

        _session.Platform.Email.Fail = true;
        try
        {
            var before = _session.Platform.Audit.ForTenant(owner.TenantId).Count;
            var send = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(owner.Email) + "/token/send", new { }, owner.Token);
            var invite = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(owner.Email) + "/send-invitation", new { }, owner.Token);
            Assert.False(Json(send.Body).GetProperty("emailSent").GetBoolean());
            Assert.False(Json(invite.Body).GetProperty("emailSent").GetBoolean());
            Assert.Equal(before, _session.Platform.Audit.ForTenant(owner.TenantId).Count);
        }
        finally
        {
            _session.Platform.Email.Fail = false;
        }

        var nullEmail = await RawAsync(HttpMethod.Post, "/members", """{"email":null}""", owner.Token);
        var omitted = await RawAsync(HttpMethod.Post, "/members", "{}", owner.Token);
        Assert.Equal("email: required", Error(nullEmail.Body));
        Assert.Equal("email: required", Error(omitted.Body));
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await ListAsync(owner.Token, workspace);
        var nullRole = await _session.ToolAsync(owner.Token, "invite_member", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = "role-null@example.com",
            ["role"] = JsonSerializer.Deserialize<JsonElement>("null"),
        });
        Assert.Equal("role: required", nullRole.Text);
        var patch = await RawAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/members/" + Uri.EscapeDataString(owner.Email), """{"role":null}""", owner.Token);
        Assert.Equal("role: required", Error(patch.Body));
        var shape = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = JsonSerializer.Deserialize<JsonElement>("null"),
        });
        Assert.Equal("shape: required", shape.Text);
        var other = await _session.AddMemberAsync(owner.Token, "flag");
        var toolFlag = await _session.ToolAsync(owner.Token, "set_tenant_owner", new Dictionary<string, object?>
        {
            ["email"] = other,
            ["isTenantOwner"] = JsonSerializer.Deserialize<JsonElement>("null"),
        });
        Assert.Equal("isTenantOwner: required", toolFlag.Text);
        var restFlag = await RawAsync(HttpMethod.Put, "/members/" + Uri.EscapeDataString(other) + "/tenant-owner", """{"isTenantOwner":null}""", owner.Token);
        Assert.Equal("isTenantOwner: required", Error(restFlag.Body));

        var jsonNull = JsonSerializer.Deserialize<JsonElement>("null");
        var createSchema = await _session.ToolAsync(owner.Token, "create_list", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["name"] = "Other",
            ["schema"] = jsonNull,
        });
        Assert.Equal("schema: required", createSchema.Text);
        var updateSchema = await _session.ToolAsync(owner.Token, "update_schema", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["schema"] = jsonNull,
        });
        Assert.Equal("schema: required", updateSchema.Text);
        var data = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["data"] = jsonNull,
        });
        Assert.Equal("data: required", data.Text);
        var postedList = await RawAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", """{"name":"Other","schema":null}""", owner.Token);
        Assert.Equal("schema: required", Error(postedList.Body));
        var unwrappedSchema = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", "null", owner.Token);
        Assert.Equal("$: expected Object, found Null", Error(unwrappedSchema.Body));
        var unwrappedItem = await RawAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/z", "null", owner.Token);
        Assert.Equal("$: expected Object, found Null", Error(unwrappedItem.Body));
    }

    private async Task<string> WorkspaceAsync(string token)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" }, token);
        Assert.Equal(200, response.Status);
        return Json(response.Body).GetProperty("id").GetString()!;
    }

    private async Task<string> ListAsync(string token, string workspace)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, token);
        Assert.Equal(200, response.Status);
        return Json(response.Body).GetProperty("id").GetString()!;
    }

    private async Task<(int Status, string Body)> RawAsync(HttpMethod method, string url, string body, string token)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _session.Platform.ApiClient.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async IAsyncEnumerable<MemberDocument> PagesAsync(string tenantId)
    {
        var members = _session.Platform.Api.Services.GetRequiredService<IMemberRepository>();
        string? cursor = null;
        do
        {
            var page = await members.ListAsync(tenantId, 100, cursor, CancellationToken.None);
            foreach (var member in page.Items)
            {
                yield return member;
            }

            cursor = page.Continuation;
        }
        while (cursor is not null);
    }

    private static JsonElement Json(string body)
    {
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string Error(string body)
    {
        return Json(body).GetProperty("error").GetString()!;
    }

    private static void SameKeys(string body, params string[] keys)
    {
        var actual = Json(body).EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(keys.Order(StringComparer.Ordinal).ToArray(), actual);
    }
}
