using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentConnect.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AgentConnect.Tests.Integration;

/// <summary>Acceptance cases added or tightened by specification v9.</summary>
[Collection("platform")]
public sealed class V9SpecTests
{
    private const string Schema = """{"properties":[{"name":"title","type":"String","required":true}]}""";

    private readonly Session _session;

    public V9SpecTests(PlatformFixture platform)
    {
        _session = new Session(platform);
    }

    [Fact]
    [Trait("T", "T-81")]
    [Trait("T", "T-82")]
    public async Task InvitationAndTokenEmails_AreHtmlTables()
    {
        var owner = await _session.CreateTenantAsync("Layout");
        var invitation = _session.Platform.Email.For(owner.Email).Single();
        AssertHtml(invitation, "AgentConnect invited you to AgentConnect for Layout.");
        Assert.DoesNotContain("Workspace:", invitation.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailTemplates.PreviousTokenLine, invitation.Body, StringComparison.Ordinal);

        var email = await _session.AddMemberAsync(owner.Token, "seat");
        var layoutWorkspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" }, owner.Token)).Body).GetProperty("id").GetString()!;
        var added = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + layoutWorkspace + "/members", new { email, role = "WorkspaceWrite" }, owner.Token);
        Assert.True(Json(added.Body).GetProperty("emailSent").GetBoolean());
        var workspaceMail = _session.Platform.Email.For(email).Single();
        AssertHtml(workspaceMail, "Workspace: Ops. Role: WorkspaceWrite.");
        Assert.Contains("invited you to AgentConnect for Layout.", workspaceMail.Body, StringComparison.Ordinal);

        var regenerated = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/token/regenerate", new { }, owner.Token);
        Assert.Equal(200, regenerated.Status);
        var sent = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/token/send", new { }, owner.Token);
        Assert.True(Json(sent.Body).GetProperty("emailSent").GetBoolean());
        var tokenMail = _session.Platform.Email.For(email).Last();
        AssertHtml(tokenMail, "Here is your AgentConnect token for Layout.");
        Assert.Contains(EmailTemplates.PreviousTokenLine, tokenMail.Body, StringComparison.Ordinal);
        var noteAt = tokenMail.Body.IndexOf(EmailTemplates.TokenNote("Layout"), StringComparison.Ordinal);
        var previousAt = tokenMail.Body.IndexOf(EmailTemplates.PreviousTokenLine, StringComparison.Ordinal);
        Assert.True(noteAt >= 0 && previousAt > noteAt);
    }

    [Fact]
    [Trait("T", "T-83")]
    [Trait("T", "T-84")]
    public async Task WorkspaceFilters_AreCaseSensitiveAndRoleScoped()
    {
        var owner = await _session.CreateTenantAsync("Filter");
        foreach (var name in new[] { "Ops EU", "Ops US", "ops local", "Sales" })
        {
            Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name }, owner.Token)).Status);
        }

        var query = Uri.EscapeDataString("""{"where":[{"path":"name","op":"startsWith","value":"Ops"}],"orderBy":{"path":"name"}}""");
        var rest = await _session.SendAsync(HttpMethod.Get, "/workspaces?query=" + query, null, owner.Token);
        Assert.Equal(new[] { "Ops EU", "Ops US" }, Names(rest.Body, "workspaces"));
        var mcp = await _session.ToolAsync(owner.Token, "list_workspaces", new Dictionary<string, object?>
        {
            ["where"] = Session.Node("""[{"path":"name","op":"startsWith","value":"Ops"}]"""),
            ["orderBy"] = Session.Node("""{"path":"name"}"""),
        });
        Assert.False(mcp.IsError);
        Assert.Equal(new[] { "Ops EU", "Ops US" }, Names(mcp.Text, "workspaces"));

        var reader = await _session.AddMemberAsync(owner.Token, "reader");
        var workspaces = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces", null, owner.Token)).Body).GetProperty("workspaces");
        var first = workspaces[0].GetProperty("id").GetString()!;
        var second = workspaces[1].GetProperty("id").GetString()!;
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + first + "/members", new { email = reader, role = "WorkspaceOwner" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + second + "/members", new { email = reader, role = "WorkspaceRead" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(reader) + "/send-invitation", new { }, owner.Token);
        var readerToken = Session.TokenFrom(_session.Platform.Email.For(reader).Last().Body);
        var roleQuery = Uri.EscapeDataString("""{"where":[{"path":"role","op":"eq","value":"WorkspaceOwner"}]}""");
        var owned = await _session.SendAsync(HttpMethod.Get, "/workspaces?query=" + roleQuery, null, readerToken);
        Assert.Equal(new[] { first }, Json(owned.Body).GetProperty("workspaces").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray());
        var none = await _session.SendAsync(HttpMethod.Get, "/workspaces?query=" + roleQuery, null, owner.Token);
        Assert.Equal(0, Json(none.Body).GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    [Trait("T", "T-85")]
    [Trait("T", "T-89")]
    public async Task ListFilters_SortAndPageWithTheIdTieBreak()
    {
        var owner = await _session.CreateTenantAsync("Lists");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var described = await CreateListAsync(owner.Token, workspace, "Described", "my tasks");
        var middle = await CreateListAsync(owner.Token, workspace, "Middle", null);
        var plain = await CreateListAsync(owner.Token, workspace, "Plain", null);
        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + described + "/schema", JsonDocument.Parse(Schema).RootElement, owner.Token);
        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + described + "/schema", JsonDocument.Parse(Schema).RootElement, owner.Token);
        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + middle + "/schema", JsonDocument.Parse(Schema).RootElement, owner.Token);
        var versionQuery = Uri.EscapeDataString("""{"where":[{"path":"schemaVersion","op":"ge","value":2}]}""");
        var versions = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists?query=" + versionQuery, null, owner.Token);
        Assert.Equal(new[] { "Described", "Middle" }, Names(versions.Body, "lists").Order(StringComparer.Ordinal).ToArray());
        var task = Uri.EscapeDataString("""{"where":[{"path":"description","op":"contains","value":"task"}]}""");
        Assert.Equal(new[] { "Described" }, Names((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists?query=" + task, null, owner.Token)).Body, "lists"));
        var upper = Uri.EscapeDataString("""{"where":[{"path":"description","op":"contains","value":"TASK"}]}""");
        Assert.Equal(0, Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists?query=" + upper, null, owner.Token)).Body).GetProperty("lists").GetArrayLength());

        var database = _session.Platform.Database;
        var baseTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await StampListAsync(database, owner.TenantId, workspace, plain, baseTime);
        await StampListAsync(database, owner.TenantId, workspace, middle, baseTime.AddHours(1));
        await StampListAsync(database, owner.TenantId, workspace, described, baseTime.AddHours(2));
        var newest = Uri.EscapeDataString("""{"orderBy":{"path":"updated","desc":true}}""");
        Assert.Equal(new[] { "Described", "Middle", "Plain" }, Names((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists?query=" + newest, null, owner.Token)).Body, "lists"));

        var ties = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + ties + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add(await CreateListAsync(owner.Token, ties, "Tie " + index.ToString(), null));
        }

        var ascending = await PagesAsync(owner.Token, "/workspaces/" + ties + "/lists?pageSize=2&query=" + Uri.EscapeDataString("""{"orderBy":{"path":"schemaVersion"}}"""), "lists");
        var tieIds = ids.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(tieIds, ascending);
        var descending = await PagesAsync(owner.Token, "/workspaces/" + ties + "/lists?pageSize=2&query=" + Uri.EscapeDataString("""{"orderBy":{"path":"schemaVersion","desc":true}}"""), "lists");
        Assert.Equal(tieIds.Reverse().ToArray(), descending);
    }

    [Fact]
    [Trait("T", "T-86")]
    [Trait("T", "T-87")]
    [Trait("T", "T-88")]
    public async Task MemberFilters_FollowTheFieldRules()
    {
        var owner = await _session.CreateTenantAsync("People");
        var ann = "ann-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        var bob = "bob-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/members", new { email = ann }, owner.Token)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/members", new { email = bob }, owner.Token)).Status);
        var emailQuery = Uri.EscapeDataString("""{"where":[{"path":"email","op":"startsWith","value":"ANN"}]}""");
        Assert.Equal(new[] { ann }, Emails((await _session.SendAsync(HttpMethod.Get, "/members?query=" + emailQuery, null, owner.Token)).Body));
        var mcp = await _session.ToolAsync(owner.Token, "query_members", new Dictionary<string, object?>
        {
            ["where"] = Session.Node("""[{"path":"email","op":"startsWith","value":"ANN"}]"""),
        });
        Assert.False(mcp.IsError);
        Assert.Equal(new[] { ann }, Emails(mcp.Text));
        var uninvited = Uri.EscapeDataString("""{"where":[{"path":"invitationSent","op":"eq","value":false}]}""");
        var never = Emails((await _session.SendAsync(HttpMethod.Get, "/members?query=" + uninvited, null, owner.Token)).Body);
        Assert.Contains(ann, never);
        Assert.Contains(bob, never);
        Assert.DoesNotContain(owner.Email, never);
        var newest = Emails((await _session.SendAsync(HttpMethod.Get, "/members?query=" + Uri.EscapeDataString("""{"orderBy":{"path":"created","desc":true}}"""), null, owner.Token)).Body);
        Assert.Equal(bob, newest[0]);

        var writer = await _session.AddMemberAsync(owner.Token, "writer");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writer, role = "WorkspaceWrite" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(writer) + "/send-invitation", new { }, owner.Token);
        var writerToken = Session.TokenFrom(_session.Platform.Email.For(writer).Last().Body);
        Assert.Equal(403, (await _session.SendAsync(HttpMethod.Get, "/members", null, writerToken)).Status);
        var denied = await _session.ToolAsync(writerToken, "query_members", []);
        Assert.True(denied.IsError);
        Assert.Equal("$: not allowed", denied.Text);

        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = ann, role = "WorkspaceRead" }, owner.Token);
        var roles = Uri.EscapeDataString("""{"where":[{"path":"role","op":"in","value":["WorkspaceOwner","WorkspaceRead"]}]}""");
        var matched = Emails((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/members?query=" + roles, null, owner.Token)).Body);
        Assert.Contains(owner.Email, matched);
        Assert.Contains(ann, matched);
        Assert.DoesNotContain(writer, matched);
        var badOrder = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/members?query=" + Uri.EscapeDataString("""{"orderBy":{"path":"role"}}"""), null, owner.Token);
        Assert.Equal(400, badOrder.Status);
        Assert.Equal("orderBy.path: not sortable", Error(badOrder.Body));

        Assert.Equal("where[0].path: unknown property", Error((await _session.SendAsync(HttpMethod.Get, "/workspaces?query=" + Uri.EscapeDataString("""{"where":[{"path":"foo","op":"eq","value":"x"}]}"""), null, owner.Token)).Body));
        Assert.Equal("where[0].op: not allowed for Boolean", Error((await _session.SendAsync(HttpMethod.Get, "/members?query=" + Uri.EscapeDataString("""{"where":[{"path":"isTenantOwner","op":"contains","value":true}]}"""), null, owner.Token)).Body));
        Assert.Equal("orderBy.path: must be a required property", Error((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists?query=" + Uri.EscapeDataString("""{"orderBy":{"path":"description"}}"""), null, owner.Token)).Body));
        var shape = await _session.ToolAsync(owner.Token, "list_lists", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["shape"] = "full" });
        Assert.Equal("shape: unknown field", shape.Text);
        Assert.Equal("query: malformed JSON", Error((await _session.SendAsync(HttpMethod.Get, "/workspaces?query=not-json", null, owner.Token)).Body));
        Assert.Equal("where[0].value: expected DateTime, found Number", Error((await _session.SendAsync(HttpMethod.Get, "/workspaces?query=" + Uri.EscapeDataString("""{"where":[{"path":"created","op":"lt","value":5}]}"""), null, owner.Token)).Body));
    }

    [Fact]
    [Trait("T", "T-90")]
    [Trait("T", "T-93")]
    [Trait("T", "T-94")]
    [Trait("T", "T-95")]
    public async Task BatchChecks_FailClosed()
    {
        var owner = await _session.CreateTenantAsync("Batch");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await CreateListAsync(owner.Token, workspace, "Tasks", null);
        var before = _session.Platform.Audit.ForTenant(owner.TenantId).Count;
        var invalid = await BatchAsync(owner.Token, new object[]
        {
            Item("upsert", workspace, list, "a", new { title = "ok" }),
            Item("upsert", workspace, list, "b", new { title = 1 }),
            Item("upsert", workspace, list, "c", new { title = "ok" }),
        });
        Assert.Equal(200, invalid.Status);
        Assert.Equal(new[] { 424, 400, 424 }, Statuses(invalid.Body));
        Assert.Equal("title: expected String, found Number", Json(invalid.Body).GetProperty("results")[1].GetProperty("error").GetString());
        Assert.Equal(0, Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids", null, owner.Token)).Body).GetProperty("items").GetArrayLength());
        Assert.Equal(before, _session.Platform.Audit.ForTenant(owner.TenantId).Count);

        var reader = await _session.AddMemberAsync(owner.Token, "reader");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = reader, role = "WorkspaceRead" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(reader) + "/send-invitation", new { }, owner.Token);
        var readerToken = Session.TokenFrom(_session.Platform.Email.For(reader).Last().Body);
        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/i", new { title = "kept" }, owner.Token);
        var mixed = await BatchAsync(readerToken, new object[]
        {
            Item("get", workspace, list, "i", null),
            Item("upsert", workspace, list, "j", new { title = "no" }),
            Item("get", workspace, list, "i", null),
        });
        Assert.Equal(new[] { 424, 403, 424 }, Statuses(mixed.Body));
        var missing = await BatchAsync(owner.Token, new object[]
        {
            Item("get", workspace, list, "i", null),
            Item("get", workspace, list, "missing", null),
        });
        Assert.Equal(new[] { 424, 404 }, Statuses(missing.Body));

        var gone = await WorkspaceAsync(owner.Token);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + gone, null, owner.Token)).Status);
        var deletedWorkspace = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "list", workspaceId = gone, name = "X", schema = JsonDocument.Parse(Schema).RootElement } });
        Assert.Equal(new[] { 404 }, Statuses(deletedWorkspace.Body));
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/lists/" + list, null, owner.Token)).Status);
        var deletedList = await BatchAsync(owner.Token, new object[] { Item("get", workspace, list, "i", null) });
        Assert.Equal(new[] { 404 }, Statuses(deletedList.Body));

        var other = await _session.AddMemberAsync(owner.Token, "other");
        var self = await BatchAsync(owner.Token, new object[]
        {
            new { op = "delete", type = "member", email = owner.Email },
            new { op = "delete", type = "member", email = other },
        });
        Assert.Equal(403, Json(self.Body).GetProperty("results")[0].GetProperty("status").GetInt32());
        Assert.Equal("$: not allowed on yourself", Json(self.Body).GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal(424, Json(self.Body).GetProperty("results")[1].GetProperty("status").GetInt32());
        var kept = Uri.EscapeDataString("{\"where\":[{\"path\":\"email\",\"op\":\"eq\",\"value\":\"" + other + "\"}]}");
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/members?query=" + kept, null, owner.Token)).Status);

        var live = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + live + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var names = await BatchAsync(owner.Token, new object[]
        {
            new { op = "upsert", type = "list", workspaceId = live, name = "Tasks", schema = JsonDocument.Parse(Schema).RootElement },
            new { op = "upsert", type = "list", workspaceId = live, name = "tasks", schema = JsonDocument.Parse(Schema).RootElement },
        });
        Assert.Equal(new[] { 424, 409 }, Statuses(names.Body));
        Assert.Equal("name: already exists", Json(names.Body).GetProperty("results")[1].GetProperty("error").GetString());
        Assert.Equal(0, Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + live + "/lists", null, owner.Token)).Body).GetProperty("lists").GetArrayLength());

        Assert.Equal("operations: required", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { }, owner.Token)).Body));
        Assert.Equal("operations: must have 1 to 100 entries", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { operations = Array.Empty<object>() }, owner.Token)).Body));
        Assert.Equal("operations: must have 1 to 100 entries", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { operations = Enumerable.Range(0, 101).Select(_ => new { op = "get", type = "member", email = "a@example.com" }) }, owner.Token)).Body));
        Assert.Equal("operations[0].op: must be upsert, delete or get", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { operations = new[] { new { op = "patch", type = "member", email = "a@example.com" } } }, owner.Token)).Body));
        Assert.Equal("operations[0].type: must be member, workspace, list or item", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { operations = new[] { new { op = "get", type = "tenant" } } }, owner.Token)).Body));
        Assert.Equal("operations[0].itemId: required", Error((await BatchAsync(owner.Token, new object[] { new { op = "delete", type = "item", workspaceId = live, listId = "l" } })).Body));
        Assert.Equal("operations[0].ref: unknown field", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { operations = new[] { new { op = "get", type = "member", email = "a@example.com", @ref = "x" } } }, owner.Token)).Body));
        Assert.Equal("operations[1]: duplicate target", Error((await BatchAsync(owner.Token, new object[] { new { op = "get", type = "member", email = "Ann@example.com" }, new { op = "delete", type = "member", email = "ann@example.com" } })).Body));
        Assert.Equal("operations[1]: duplicate target", Error((await BatchAsync(owner.Token, new object[] { Item("upsert", live, "l", "same", new { title = "a" }), Item("delete", live, "l", "same", null) })).Body));
        Assert.Equal("operations[1]: duplicate target", Error((await BatchAsync(owner.Token, new object[] { Item("upsert", live, "l", "same", new { title = "a" }), Item("upsert", live, "l", "same", new { title = "b" }) })).Body));
    }

    [Fact]
    [Trait("T", "T-91")]
    [Trait("T", "T-92")]
    public async Task BatchSetup_IsOneTypeAndOneParent()
    {
        var owner = await _session.CreateTenantAsync("Setup");
        var restWorkspaces = await BatchAsync(owner.Token, new object[]
        {
            new { op = "upsert", type = "workspace", name = "One" },
            new { op = "upsert", type = "workspace", name = "Two" },
        });
        Assert.Equal(new[] { 200, 200 }, Statuses(restWorkspaces.Body));
        var workspace = Json(restWorkspaces.Body).GetProperty("results")[0].GetProperty("result").GetProperty("id").GetString()!;
        var mcpWorkspaces = await _session.ToolAsync(owner.Token, "batch", new Dictionary<string, object?>
        {
            ["operations"] = Session.Node("""[{"op":"upsert","type":"workspace","name":"Three"}]"""),
        });
        Assert.False(mcpWorkspaces.IsError);
        Assert.Equal(200, Json(mcpWorkspaces.Text).GetProperty("results")[0].GetProperty("status").GetInt32());

        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var lists = await BatchAsync(owner.Token, new object[]
        {
            new { op = "upsert", type = "list", workspaceId = workspace, name = "Alpha", schema = JsonDocument.Parse(Schema).RootElement },
            new { op = "upsert", type = "list", workspaceId = workspace, name = "Beta", schema = JsonDocument.Parse(Schema).RootElement },
        });
        Assert.Equal(new[] { 200, 200 }, Statuses(lists.Body));
        var list = Json(lists.Body).GetProperty("results")[0].GetProperty("result").GetProperty("id").GetString()!;
        var mcpLists = await _session.ToolAsync(owner.Token, "batch", new Dictionary<string, object?>
        {
            ["operations"] = Session.Node("[{\"op\":\"upsert\",\"type\":\"list\",\"workspaceId\":\"" + workspace + "\",\"name\":\"Gamma\",\"schema\":{\"properties\":[{\"name\":\"title\",\"type\":\"String\",\"required\":true}]}}]"),
        });
        Assert.Equal(200, Json(mcpLists.Text).GetProperty("results")[0].GetProperty("status").GetInt32());

        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/existing", new { title = "old" }, owner.Token);
        var items = await BatchAsync(owner.Token, new object[]
        {
            Item("upsert", workspace, list, "n1", new { title = "one" }),
            Item("upsert", workspace, list, "n2", new { title = "two" }),
            Item("get", workspace, list, "existing", null),
        });
        Assert.Equal(new[] { 200, 200, 200 }, Statuses(items.Body));
        Assert.Equal("one", Json(items.Body).GetProperty("results")[0].GetProperty("result").GetProperty("id").GetString() is "n1" ? "one" : "");
        var mcpItems = await _session.ToolAsync(owner.Token, "batch", new Dictionary<string, object?>
        {
            ["operations"] = Session.Node("[{\"op\":\"upsert\",\"type\":\"item\",\"workspaceId\":\"" + workspace + "\",\"listId\":\"" + list + "\",\"itemId\":\"n3\",\"data\":{\"title\":\"three\"}},{\"op\":\"get\",\"type\":\"item\",\"workspaceId\":\"" + workspace + "\",\"listId\":\"" + list + "\",\"itemId\":\"existing\"}]"),
        });
        Assert.Equal(new[] { 200, 200 }, Statuses(mcpItems.Text));

        var ann = "ann-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        var prior = await _session.AddMemberAsync(owner.Token, "prior");
        var members = await BatchAsync(owner.Token, new object[]
        {
            new { op = "upsert", type = "member", email = ann },
            new { op = "upsert", type = "member", email = owner.Email },
            new { op = "get", type = "member", email = prior },
        });
        Assert.Equal(new[] { 200, 200, 200 }, Statuses(members.Body));
        var createdToken = Json(members.Body).GetProperty("results")[0].GetProperty("result").GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(createdToken));
        Assert.False(Json(members.Body).GetProperty("results")[1].GetProperty("result").TryGetProperty("token", out _));
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, createdToken)).Status);
        Assert.Empty(_session.Platform.Email.For(ann));

        var writer = await _session.AddMemberAsync(owner.Token, "writer");
        var owned = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + owned + "/members", new { email = writer, role = "WorkspaceOwner" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(writer) + "/send-invitation", new { }, owner.Token);
        var writerToken = Session.TokenFrom(_session.Platform.Email.For(writer).Last().Body);
        var forbidden = await BatchAsync(writerToken, new object[] { new { op = "upsert", type = "member", email = "new-" + Guid.NewGuid().ToString("N")[..8] + "@example.com" } });
        Assert.Equal(new[] { 403 }, Statuses(forbidden.Body));
        var forbiddenMcp = await _session.ToolAsync(writerToken, "batch", new Dictionary<string, object?>
        {
            ["operations"] = Session.Node("""[{"op":"get","type":"member","email":"nobody@example.com"}]"""),
        });
        Assert.False(forbiddenMcp.IsError);
        Assert.Equal(403, Json(forbiddenMcp.Text).GetProperty("results")[0].GetProperty("status").GetInt32());
        var createDenied = await _session.ToolAsync(writerToken, "batch", new Dictionary<string, object?>
        {
            ["operations"] = Session.Node("""[{"op":"upsert","type":"workspace","name":"Denied"}]"""),
        });
        Assert.Equal(403, Json(createDenied.Text).GetProperty("results")[0].GetProperty("status").GetInt32());
        var deleteDenied = await BatchAsync(writerToken, new object[] { new { op = "delete", type = "workspace", workspaceId = owned } });
        Assert.Equal(new[] { 403 }, Statuses(deleteDenied.Body));
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + owned + "/lists", null, writerToken)).Status);

        Assert.Equal("operations[1].type: cannot mix types in one batch", Error((await BatchAsync(owner.Token, new object[] { new { op = "get", type = "workspace", workspaceId = workspace }, Item("get", workspace, list, "existing", null) })).Body));
        var other = await WorkspaceAsync(owner.Token);
        Assert.Equal("operations[1].workspaceId: must target one workspaceId per batch", Error((await BatchAsync(owner.Token, new object[]
        {
            new { op = "get", type = "list", workspaceId = workspace, listId = list },
            new { op = "get", type = "list", workspaceId = other, listId = list },
        })).Body));
        Assert.Equal("operations[1].listId: must target one listId per batch", Error((await BatchAsync(owner.Token, new object[]
        {
            Item("get", workspace, list, "existing", null),
            Item("get", workspace, "other-list", "existing", null),
        })).Body));
        Assert.Equal("operations[0].members: unknown field", Error((await _session.SendAsync(HttpMethod.Post, "/batch", new { operations = new[] { new { op = "upsert", type = "workspace", name = "WithMembers", members = Array.Empty<object>() } } }, owner.Token)).Body));
    }

    [Fact]
    [Trait("T", "T-96")]
    public async Task BatchLimits_KeepTheFailureTexts()
    {
        var owner = await _session.CreateTenantAsync("Limits");
        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await CreateListAsync(owner.Token, workspace, "Bulk", null);
        var huge = new string('x', 2_200_000);
        var tooBig = await BatchAsync(owner.Token, new object[]
        {
            Item("upsert", workspace, list, "a", new { title = "ok" }),
            Item("upsert", workspace, list, "b", new { title = huge }),
            Item("upsert", workspace, list, "c", new { title = "ok" }),
        });
        Assert.Equal(new[] { 424, 413, 424 }, Statuses(tooBig.Body));
        Assert.Equal("$: item too large", Json(tooBig.Body).GetProperty("results")[1].GetProperty("error").GetString());

        var wide = new string('y', 900_000);
        var batchTooBig = await BatchAsync(owner.Token, new object[]
        {
            Item("upsert", workspace, list, "d", new { title = wide }),
            Item("upsert", workspace, list, "e", new { title = wide }),
            Item("upsert", workspace, list, "f", new { title = wide }),
        });
        Assert.Equal(413, Json(batchTooBig.Body).GetProperty("results")[0].GetProperty("status").GetInt32());
        Assert.Equal("$: batch too large", Json(batchTooBig.Body).GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal(424, Json(batchTooBig.Body).GetProperty("results")[1].GetProperty("status").GetInt32());
        Assert.Equal(0, Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids", null, owner.Token)).Body).GetProperty("items").GetArrayLength());

        _session.Platform.Database.WriteFault = ItemWriteFault.Throttled;
        _session.Platform.Database.ThrottleRetryAfter = TimeSpan.FromSeconds(3);
        var throttled = await _session.Platform.SendAsync(HttpMethod.Post, "/batch", new
        {
            operations = new object[] { Item("upsert", workspace, list, "g", new { title = "ok" }), Item("upsert", workspace, list, "h", new { title = "ok" }) },
        }, owner.Token, false);
        Assert.Equal(200, throttled.Status);
        Assert.Equal(429, Json(throttled.Body).GetProperty("results")[0].GetProperty("status").GetInt32());
        Assert.Equal("$: too many requests", Json(throttled.Body).GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal("3", throttled.Response.Headers.GetValues("Retry-After").Single());

        var options = _session.Platform.Api.Services.GetRequiredService<AgentConnectOptions>();
        try
        {
            options.ConcurrencyRetries = 3;
            _session.Platform.Database.ReplaceConflictsRemaining = 4;
            var stale = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "workspace", workspaceId = workspace, name = "Renamed" } });
            Assert.Equal(409, Json(stale.Body).GetProperty("results")[0].GetProperty("status").GetInt32());
            Assert.Equal("$: conflict", Json(stale.Body).GetProperty("results")[0].GetProperty("error").GetString());
            var unchanged = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces", null, owner.Token)).Body).GetProperty("workspaces").EnumerateArray().First(item => item.GetProperty("id").GetString() == workspace).GetProperty("name").GetString();
            Assert.NotEqual("Renamed", unchanged);
        }
        finally
        {
            options.ConcurrencyRetries = 3;
            _session.Platform.Database.ReplaceConflictsRemaining = 0;
            _session.Platform.Database.WriteFault = ItemWriteFault.None;
        }
    }

    [Fact]
    [Trait("T", "T-97")]
    public async Task McpInstructionsRowsAndDescriptions_MatchTheSpec()
    {
        var owner = await _session.CreateTenantAsync("Rows");
        await using (var client = await _session.Platform.McpClientAsync(owner.Token))
        {
            Assert.Equal(McpCopy.Instructions, client.ServerInstructions);
            var tools = await client.ListToolsAsync();
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["list_workspaces"] = McpCopy.ListWorkspaces,
                ["update_workspace"] = McpCopy.UpdateWorkspace,
                ["list_lists"] = McpCopy.ListLists,
                ["create_list"] = McpCopy.CreateList,
                ["update_list"] = McpCopy.UpdateList,
                ["delete_list"] = McpCopy.DeleteList,
                ["get_schema"] = McpCopy.GetSchema,
                ["update_schema"] = McpCopy.UpdateSchema,
                ["query_items"] = McpCopy.QueryItems,
                ["get_item"] = McpCopy.GetItem,
                ["upsert_item"] = McpCopy.UpsertItem,
                ["delete_item"] = McpCopy.DeleteItem,
                ["invite_member"] = McpCopy.InviteMember,
                ["change_workspace_role"] = McpCopy.ChangeWorkspaceRole,
                ["revoke_workspace_access"] = McpCopy.RevokeWorkspaceAccess,
                ["list_workspace_members"] = McpCopy.ListWorkspaceMembers,
                ["set_tenant_owner"] = McpCopy.SetTenantOwner,
                ["query_members"] = McpCopy.QueryMembers,
                ["batch"] = McpCopy.Batch,
            };
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            foreach (var tool in tools)
            {
                Assert.Equal(expected[tool.Name], tool.Description);
            }
        }

        var workspace = await WorkspaceAsync(owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await CreateListAsync(owner.Token, workspace, "Many", null);
        for (var start = 0; start < 250; start += 100)
        {
            var count = Math.Min(100, 250 - start);
            var operations = Enumerable.Range(start, count).Select(index => Item("upsert", workspace, list, "i" + index.ToString("D3"), new { title = "t" })).ToArray();
            Assert.Equal(200, (await BatchAsync(owner.Token, operations)).Status);
        }

        var rows = new List<int>();
        string? continuation = null;
        for (var page = 0; page < 3; page++)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["workspaceId"] = workspace,
                ["listId"] = list,
                ["shape"] = "ids",
                ["pageSize"] = 100,
            };
            if (continuation is not null)
            {
                arguments["continuation"] = continuation;
            }

            var result = await _session.ToolAsync(owner.Token, "query_items", arguments);
            Assert.False(result.IsError);
            var root = Json(result.Text);
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                rows.Add(item.GetProperty("row").GetInt32());
            }

            continuation = root.TryGetProperty("continuation", out var token) ? token.GetString() : null;
        }

        Assert.Equal(Enumerable.Range(1, 250).ToArray(), rows);
        Assert.Null(continuation);
        var rest = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&pageSize=100", null, owner.Token);
        Assert.False(Json(rest.Body).GetProperty("items")[0].TryGetProperty("row", out _));
    }

    [Fact]
    [Trait("T", "T-98")]
    [Trait("T", "T-99")]
    public async Task UnknownKeysAndWhitespaceItemIds_AreRejected()
    {
        var owner = await _session.CreateTenantAsync("Shape");
        var email = "ann-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        var member = await RawAsync(HttpMethod.Post, "/members", "{\"email\":" + JsonSerializer.Serialize(email) + ",\"foo\":1}", owner.Token);
        Assert.Equal(400, member.Status);
        Assert.Equal("foo: unknown field", Error(member.Body));
        var workspace = await RawAsync(HttpMethod.Post, "/workspaces", "{\"name\":\"Ops\",\"members\":[{\"email\":" + JsonSerializer.Serialize(owner.Email) + ",\"role\":\"WorkspaceOwner\",\"foo\":1}]}", owner.Token);
        Assert.Equal("members[0].foo: unknown field", Error(workspace.Body));
        var created = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" }, owner.Token);
        var id = Json(created.Body).GetProperty("id").GetString()!;
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + id + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
        var list = await CreateListAsync(owner.Token, id, "Tasks", null);
        var extra = await RawAsync(HttpMethod.Put, "/workspaces/" + id + "/lists/" + list + "/items/i1", """{"title":"a","extra":1}""", owner.Token);
        Assert.Equal(400, extra.Status);
        Assert.DoesNotContain("unknown field", extra.Body, StringComparison.Ordinal);
        Assert.Contains("extra", Error(extra.Body), StringComparison.Ordinal);

        var space = await RawAsync(HttpMethod.Put, "/workspaces/" + id + "/lists/" + list + "/items/%20", """{"title":"a"}""", owner.Token);
        Assert.Equal(400, space.Status);
        Assert.Equal("itemId: invalid", Error(space.Body));
        var tool = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = id,
            ["listId"] = list,
            ["itemId"] = " ",
            ["data"] = Session.Node("""{"title":"a"}"""),
        });
        Assert.True(tool.IsError);
        Assert.Equal("itemId: invalid", tool.Text);
        Assert.Equal(0, Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + id + "/lists/" + list + "/items?shape=ids", null, owner.Token)).Body).GetProperty("items").GetArrayLength());
    }

    [Fact]
    [Trait("T", "T-100")]
    public async Task OAuthProbes_StayOnTheRecordedPaths()
    {
        HttpClient[] clients = [_session.Platform.ApiClient, _session.Platform.McpHttp];
        foreach (var client in clients)
        {
            var get = await SendAsync(client, HttpMethod.Get, "/.well-known/oauth-protected-resource");
            Assert.Equal(404, get.Status);
            Assert.Equal("""{"error":"$: not found"}""", get.Body);
            foreach (var method in new[] { HttpMethod.Head, HttpMethod.Options, HttpMethod.Post })
            {
                var response = await SendAsync(client, method, "/.well-known/oauth-protected-resource");
                Assert.Equal(401, response.Status);
                Assert.Equal("""{"error":"$: invalid token"}""", response.Body);
            }

            var bare = await SendAsync(client, HttpMethod.Get, "/.well-known");
            Assert.Equal(401, bare.Status);
            Assert.Equal("""{"error":"$: invalid token"}""", bare.Body);
        }

        var mcp = await SendAsync(_session.Platform.McpHttp, HttpMethod.Post, "/mcp");
        Assert.Equal(401, mcp.Status);
        Assert.Equal("Bearer", mcp.Challenge);
        Assert.DoesNotContain("realm", mcp.Challenge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resource_metadata", mcp.Challenge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("T", "T-101")]
    public async Task PlaintextToken_IsReturnedOnceAndStoredOnlyEncrypted()
    {
        var email = "once-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        var created = await _session.Platform.SendAsync(HttpMethod.Post, "/tenants", new { name = "Once", email }, null, true);
        var createdToken = Json(created.Body).GetProperty("token").GetString()!;
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, createdToken)).Status);
        var addedEmail = "added-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        var added = await _session.SendAsync(HttpMethod.Post, "/members", new { email = addedEmail }, createdToken);
        var addedToken = Json(added.Body).GetProperty("token").GetString()!;
        var regenerated = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(addedEmail) + "/token/regenerate", new { }, createdToken);
        var newToken = Json(regenerated.Body).GetProperty("token").GetString()!;
        Assert.NotEqual(addedToken, newToken);
        var listed = await _session.SendAsync(HttpMethod.Get, "/members", null, createdToken);
        foreach (var member in Json(listed.Body).GetProperty("members").EnumerateArray())
        {
            Assert.False(member.TryGetProperty("token", out _));
        }

        var queried = await _session.ToolAsync(createdToken, "query_members", []);
        Assert.False(queried.IsError);
        foreach (var member in Json(queried.Text).GetProperty("members").EnumerateArray())
        {
            Assert.False(member.TryGetProperty("token", out _));
        }

        var tenantId = Json(created.Body).GetProperty("id").GetString()!;
        var stored = (await _session.Platform.Database.ListAsync(tenantId, 20, null, CancellationToken.None)).Items.Single(item => EmailRules.Same(item.Email, addedEmail));
        var document = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain(newToken, document, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(stored.TokenHash));
        Assert.False(string.IsNullOrEmpty(stored.TokenEncrypted));
        Assert.DoesNotContain(newToken, stored.TokenHash, StringComparison.Ordinal);
        Assert.DoesNotContain(newToken, stored.TokenEncrypted, StringComparison.Ordinal);
        foreach (var entry in _session.Platform.Audit.Entries)
        {
            Assert.DoesNotContain(newToken, entry.Action, StringComparison.Ordinal);
            Assert.DoesNotContain(createdToken, string.Join('\n', entry.Targets.Values), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("T", "T-96")]
    public async Task BatchWrites_ReapplyOnTheFreshDocument()
    {
        var owner = await _session.CreateTenantAsync("Fresh");
        var database = _session.Platform.Database;
        var workspaces = (IWorkspaceRepository)database;
        try
        {
            var added = await _session.SendAsync(HttpMethod.Post, "/members", new { email = "token-" + Guid.NewGuid().ToString("N")[..8] + "@example.com" }, owner.Token);
            var memberId = Json(added.Body).GetProperty("id").GetString()!;
            var oldToken = Json(added.Body).GetProperty("token").GetString()!;
            var memberEmail = Json(added.Body).GetProperty("email").GetString()!;
            string? freshToken = null;
            var memberReads = 0;
            database.AfterMemberRead = () =>
            {
                if (++memberReads == 1)
                {
                    return;
                }

                database.AfterMemberRead = null;
                var current = database.GetAsync(owner.TenantId, memberId, CancellationToken.None).GetAwaiter().GetResult()!;
                var issued = _session.Platform.Tokens.Issue(owner.TenantId, memberId);
                current.TokenHash = issued.Hash;
                current.TokenEncrypted = issued.Encrypted;
                database.ReplaceAsync(current, CancellationToken.None).GetAwaiter().GetResult();
                freshToken = issued.Token;
            };
            var flagged = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "member", email = memberEmail, isTenantOwner = true } });
            Assert.Equal(new[] { 200 }, Statuses(flagged.Body));
            Assert.NotNull(freshToken);
            Assert.Equal(401, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, oldToken)).Status);
            Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces", null, freshToken)).Status);
            Assert.True(Json(flagged.Body).GetProperty("results")[0].GetProperty("result").GetProperty("isTenantOwner").GetBoolean());

            var grantedBody = await _session.SendAsync(HttpMethod.Post, "/members", new { email = "role-" + Guid.NewGuid().ToString("N")[..8] + "@example.com" }, owner.Token);
            var grantedId = Json(grantedBody.Body).GetProperty("id").GetString()!;
            var grantedEmail = Json(grantedBody.Body).GetProperty("email").GetString()!;
            var workspace = await WorkspaceAsync(owner.Token);
            memberReads = 0;
            database.AfterMemberRead = () =>
            {
                if (++memberReads == 1)
                {
                    return;
                }

                database.AfterMemberRead = null;
                var current = database.GetAsync(owner.TenantId, grantedId, CancellationToken.None).GetAwaiter().GetResult()!;
                current.WorkspaceRoles[workspace] = StoredRoles.Write;
                database.ReplaceAsync(current, CancellationToken.None).GetAwaiter().GetResult();
            };
            var owned = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "member", email = grantedEmail, isTenantOwner = true } });
            Assert.Equal(new[] { 200 }, Statuses(owned.Body));
            var seated = Emails((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/members", null, owner.Token)).Body);
            Assert.Contains(grantedEmail, seated);

            await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token);
            var doomed = await WorkspaceAsync(owner.Token);
            Assert.Equal(200, (await _session.SendAsync(HttpMethod.Post, "/workspaces/" + doomed + "/members", new { email = owner.Email, role = "WorkspaceOwner" }, owner.Token)).Status);
            var doomedName = (await workspaces.GetAsync(owner.TenantId, doomed, CancellationToken.None))!.Name;
            database.AfterWorkspaceRead = () =>
            {
                database.AfterWorkspaceRead = null;
                var current = workspaces.GetAsync(owner.TenantId, doomed, CancellationToken.None).GetAwaiter().GetResult()!;
                current.DeletedAt = DateTimeOffset.UtcNow;
                workspaces.ReplaceAsync(current, CancellationToken.None).GetAwaiter().GetResult();
            };
            var resurrect = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "workspace", workspaceId = doomed, name = "Renamed" } });
            Assert.Equal(404, Json(resurrect.Body).GetProperty("results")[0].GetProperty("status").GetInt32());
            Assert.Equal("$: not found", Json(resurrect.Body).GetProperty("results")[0].GetProperty("error").GetString());
            var deletedWorkspace = (await workspaces.GetAsync(owner.TenantId, doomed, CancellationToken.None))!;
            Assert.NotNull(deletedWorkspace.DeletedAt);
            Assert.Equal(doomedName, deletedWorkspace.Name);

            var list = await CreateListAsync(owner.Token, workspace, "Versioned", null);
            database.AfterListRead = () =>
            {
                database.AfterListRead = null;
                var current = database.GetAsync(owner.TenantId, workspace, list, CancellationToken.None).GetAwaiter().GetResult()!;
                current.SchemaVersion = 4;
                current.Schema = "{\"properties\":[{\"name\":\"title\",\"type\":\"String\",\"required\":true},{\"name\":\"note\",\"type\":\"String\"}]}";
                database.ReplaceAsync(current, CancellationToken.None).GetAwaiter().GetResult();
            };
            var renamed = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "list", workspaceId = workspace, listId = list, name = "Renamed" } });
            Assert.Equal(new[] { 200 }, Statuses(renamed.Body));
            var kept = (await database.GetAsync(owner.TenantId, workspace, list, CancellationToken.None))!;
            Assert.Equal("Renamed", kept.Name);
            Assert.Equal(4, kept.SchemaVersion);
            Assert.Contains("note", kept.Schema, StringComparison.Ordinal);

            var gone = await CreateListAsync(owner.Token, workspace, "Gone", null);
            database.AfterListRead = () =>
            {
                database.AfterListRead = null;
                var current = database.GetAsync(owner.TenantId, workspace, gone, CancellationToken.None).GetAwaiter().GetResult()!;
                current.DeletedAt = DateTimeOffset.UtcNow;
                database.ReplaceAsync(current, CancellationToken.None).GetAwaiter().GetResult();
            };
            var listRename = await BatchAsync(owner.Token, new object[] { new { op = "upsert", type = "list", workspaceId = workspace, listId = gone, name = "Back" } });
            Assert.Equal(404, Json(listRename.Body).GetProperty("results")[0].GetProperty("status").GetInt32());
            var deletedList = (await database.GetAsync(owner.TenantId, workspace, gone, CancellationToken.None))!;
            Assert.NotNull(deletedList.DeletedAt);
            Assert.Equal("Gone", deletedList.Name);
        }
        finally
        {
            database.AfterMemberRead = null;
            database.AfterWorkspaceRead = null;
            database.AfterListRead = null;
        }
    }

    [Fact]
    public async Task NonStringBatchIds_AreBadRequests()
    {
        var owner = await _session.CreateTenantAsync("Ids");
        var workspaceId = await RawAsync(HttpMethod.Post, "/batch", """{"operations":[{"op":"get","type":"list","workspaceId":5,"listId":"x"}]}""", owner.Token);
        Assert.Equal(400, workspaceId.Status);
        Assert.Equal("workspaceId: expected String, found Number", Error(workspaceId.Body));
        var listId = await RawAsync(HttpMethod.Post, "/batch", """{"operations":[{"op":"get","type":"item","workspaceId":"w","listId":5,"itemId":"i"}]}""", owner.Token);
        Assert.Equal(400, listId.Status);
        Assert.Equal("listId: expected String, found Number", Error(listId.Body));
    }

    #region private

    private async Task<string> WorkspaceAsync(string token)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" + Guid.NewGuid().ToString("N")[..4] }, token);
        Assert.Equal(200, response.Status);
        return Json(response.Body).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateListAsync(string token, string workspace, string name, string? description)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name, description, schema = JsonDocument.Parse(Schema).RootElement }, token);
        Assert.Equal(200, response.Status);
        return Json(response.Body).GetProperty("id").GetString()!;
    }

    private async Task<(int Status, string Body)> BatchAsync(string token, object operations)
    {
        return await _session.SendAsync(HttpMethod.Post, "/batch", new { operations }, token);
    }

    private static object Item(string op, string workspace, string list, string? itemId, object? data)
    {
        if (data is null)
        {
            return new { op, type = "item", workspaceId = workspace, listId = list, itemId };
        }

        return new { op, type = "item", workspaceId = workspace, listId = list, itemId, data };
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

    private static async Task<(int Status, string Body, string Challenge)> SendAsync(HttpClient client, HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body, response.Headers.WwwAuthenticate.ToString());
    }

    private async Task<string[]> PagesAsync(string token, string url, string array)
    {
        var ids = new List<string>();
        var next = url;
        for (var guard = 0; guard < 10; guard++)
        {
            var response = await _session.SendAsync(HttpMethod.Get, next, null, token);
            Assert.Equal(200, response.Status);
            var root = Json(response.Body);
            ids.AddRange(root.GetProperty(array).EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            if (!root.TryGetProperty("continuation", out var continuation))
            {
                break;
            }

            var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            next = url + separator + "continuation=" + Uri.EscapeDataString(continuation.GetString()!);
        }

        return ids.ToArray();
    }

    private static async Task StampListAsync(MemoryDatabase database, string tenantId, string workspaceId, string listId, DateTimeOffset updated)
    {
        var list = await database.GetAsync(tenantId, workspaceId, listId, CancellationToken.None);
        list!.Updated = updated;
        await database.ReplaceAsync(list, CancellationToken.None);
    }

    private static void AssertHtml(EmailMessage message, string greetingPiece)
    {
        Assert.True(message.Html);
        Assert.Contains("AgentConnect", message.Subject, StringComparison.Ordinal);
        Assert.StartsWith("<!DOCTYPE html>", message.Body, StringComparison.Ordinal);
        var greeting = message.Body.IndexOf(greetingPiece, StringComparison.Ordinal);
        var table = message.Body.IndexOf("<table>", StringComparison.Ordinal);
        var instructions = message.Body.IndexOf("To connect an agent", StringComparison.Ordinal);
        Assert.True(greeting >= 0 && table > greeting && instructions > table);
        Assert.Contains(">MCP Server URL</td>", message.Body, StringComparison.Ordinal);
        Assert.Contains(">REST API URL</td>", message.Body, StringComparison.Ordinal);
        Assert.Contains(">Token</td>", message.Body, StringComparison.Ordinal);
        Assert.Contains(EmailTemplates.ValueStyle, message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<a", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Keep this token secret.", message.Body, StringComparison.Ordinal);
    }

    private static string[] Names(string json, string array)
    {
        return Json(json).GetProperty(array).EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray();
    }

    private static string[] Emails(string json)
    {
        return Json(json).GetProperty("members").EnumerateArray().Select(item => item.GetProperty("email").GetString()!).ToArray();
    }

    private static int[] Statuses(string json)
    {
        return Json(json).GetProperty("results").EnumerateArray().Select(item => item.GetProperty("status").GetInt32()).ToArray();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Error(string body)
    {
        return Json(body).GetProperty("error").GetString() ?? "";
    }

    #endregion
}
