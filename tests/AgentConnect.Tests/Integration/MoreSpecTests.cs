using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentConnect.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AgentConnect.Tests.Integration;

[Collection("platform")]
public sealed class MoreSpecTests
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

    public MoreSpecTests(PlatformFixture platform)
    {
        _session = new Session(platform);
    }

    [Fact]
    [Trait("T", "T-23")]
    [Trait("T", "T-27")]
    [Trait("T", "T-32")]
    [Trait("T", "T-33")]
    [Trait("T", "T-43")]
    [Trait("T", "T-49")]
    [Trait("T", "T-50")]
    [Trait("T", "T-63")]
    public async Task QueriesMembershipAndReads_MatchAcrossMcpAndRest()
    {
        var owner = await _session.CreateTenantAsync("Query");
        var created = await _session.SendAsync(HttpMethod.Post, "/workspaces", new
        {
            name = "Ops",
            members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } },
        }, owner.Token);
        Assert.Equal(200, created.Status);
        Assert.Equal("WorkspaceOwner", Json(created.Body).GetProperty("members")[0].GetProperty("role").GetString());
        var bare = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Empty" }, owner.Token);
        Assert.Equal(0, Json(bare.Body).GetProperty("members").GetArrayLength());
        var workspace = Json(created.Body).GetProperty("id").GetString()!;
        var listId = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        await PutItem(owner.Token, workspace, listId, "i1", """{"title":"Fix login","status":"open","priority":3,"due":"2026-11-25T09:00:00Z","done":false,"tags":["auth"],"owner":"ann"}""");
        await PutItem(owner.Token, workspace, listId, "i2", """{"title":"Write docs","status":"done","priority":1,"due":"2026-12-03T09:00:00Z","done":true,"owner":"bob"}""");

        var filter = """{"where":[{"path":"status","op":"in","value":["open","blocked"]}],"orderBy":{"path":"priority","desc":true}}""";
        var rest = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + listId + "/items?shape=full&query=" + Uri.EscapeDataString(filter), null, owner.Token);
        Assert.Equal(200, rest.Status);
        Assert.Equal("i1", Json(rest.Body).GetProperty("items")[0].GetProperty("id").GetString());
        var mcp = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = listId,
            ["shape"] = "full",
            ["where"] = Session.Node("""[{"path":"status","op":"in","value":["open","blocked"]}]"""),
            ["orderBy"] = Session.Node("""{"path":"priority","desc":true}"""),
        });
        Assert.False(mcp.IsError);
        Assert.Equal("i1", Json(mcp.Text).GetProperty("items")[0].GetProperty("id").GetString());
        var ids = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + listId + "/items?shape=ids", null, owner.Token);
        Assert.False(Json(ids.Body).GetProperty("items")[0].TryGetProperty("data", out _));

        var fresh = await _session.AddMemberAsync(owner.Token, "fresh");
        var invited = await _session.ToolAsync(owner.Token, "invite_member", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = fresh,
            ["role"] = "WorkspaceWrite",
        });
        Assert.False(invited.IsError);
        Assert.True(Json(invited.Text).GetProperty("emailSent").GetBoolean());
        Assert.Contains("Workspace: Ops", _session.Platform.Email.For(fresh).Last().Body, StringComparison.Ordinal);
        var known = await _session.AddMemberAsync(owner.Token, "known");
        await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(known) + "/send-invitation", new { }, owner.Token);
        var before = Session.TokenFrom(_session.Platform.Email.For(known).Last().Body);
        var access = await _session.ToolAsync(owner.Token, "invite_member", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["email"] = known,
            ["role"] = "WorkspaceRead",
        });
        Assert.False(access.IsError);
        Assert.DoesNotContain("Token:", _session.Platform.Email.For(known).Last().Body, StringComparison.Ordinal);
        Assert.Equal(before, Session.TokenFrom((await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(known) + "/token/send", new { }, owner.Token)).Body is string _ ? _session.Platform.Email.For(known).Last(mail => mail.Body.Contains("Token:", StringComparison.Ordinal)).Body : ""));

        var writeToken = Session.TokenFrom(_session.Platform.Email.For(fresh).Last().Body);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, writeToken)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + listId + "/schema", null, writeToken)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + listId + "/items/i1", null, writeToken)).Status);
        Assert.Equal(403, (await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + listId, new { name = "Renamed" }, writeToken)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + listId, new { name = "Renamed" }, owner.Token)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/members", null, owner.Token)).Status);
        Assert.Equal(403, (await _session.SendAsync(HttpMethod.Get, "/members", null, writeToken)).Status);
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/members", null, owner.Token)).Status);
        Assert.Equal(403, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/members", null, writeToken)).Status);
        var revoked = await _session.ToolAsync(owner.Token, "revoke_workspace_access", new Dictionary<string, object?> { ["workspaceId"] = workspace, ["email"] = known });
        Assert.False(revoked.IsError);
    }

    [Fact]
    [Trait("T", "T-25")]
    [Trait("T", "T-29")]
    [Trait("T", "T-34")]
    [Trait("T", "T-36")]
    [Trait("T", "T-38")]
    [Trait("T", "T-40")]
    [Trait("T", "T-47")]
    [Trait("T", "T-48")]
    [Trait("T", "T-52")]
    [Trait("T", "T-54")]
    [Trait("T", "T-55")]
    [Trait("T", "T-57")]
    [Trait("T", "T-58")]
    public async Task PagingDeletionNamesAndAdminKey_MatchTheSpec()
    {
        var owner = await _session.CreateTenantAsync("Pages");
        var other = await _session.CreateTenantAsync("Other");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } } }, owner.Token)).Body).GetProperty("id").GetString()!;
        var duplicate = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "ops" }, owner.Token);
        Assert.Equal(409, duplicate.Status);
        var longName = await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = new string('a', 101) }, owner.Token);
        Assert.Equal(400, longName.Status);
        Assert.Equal("name: invalid length", Error(longName.Body));
        var listId = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", description = new string('d', 1001), schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("error").GetString();
        Assert.Equal("description: invalid length", listId);
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        Assert.Equal(409, (await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Status);

        for (var index = 0; index < 250; index++)
        {
            await PutItem(owner.Token, workspace, list, "n" + index.ToString("000"), "{\"title\":\"t\",\"status\":\"open\",\"priority\":1,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");
        }

        var first = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids", null, owner.Token)).Body);
        Assert.Equal(100, first.GetProperty("items").GetArrayLength());
        var token = first.GetProperty("continuation").GetString();
        var second = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&continuation=" + Uri.EscapeDataString(token!), null, owner.Token)).Body);
        Assert.Equal(100, second.GetProperty("items").GetArrayLength());
        var third = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&continuation=" + Uri.EscapeDataString(second.GetProperty("continuation").GetString()!), null, owner.Token)).Body);
        Assert.Equal(50, third.GetProperty("items").GetArrayLength());
        Assert.False(third.TryGetProperty("continuation", out _));
        var capped = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&pageSize=5000", null, owner.Token)).Body);
        Assert.Equal(250, capped.GetProperty("items").GetArrayLength());

        var settings = await _session.SendAsync(HttpMethod.Patch, "/tenant", new { pageSizeDefault = 20, pageSizeMax = 200 }, owner.Token);
        Assert.Equal(200, settings.Status);
        var smaller = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids", null, owner.Token)).Body);
        Assert.Equal(20, smaller.GetProperty("items").GetArrayLength());
        var limited = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&pageSize=5000", null, owner.Token)).Body);
        Assert.Equal(200, limited.GetProperty("items").GetArrayLength());
        var kept = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&continuation=" + Uri.EscapeDataString(token!), null, owner.Token)).Body);
        Assert.Equal(100, kept.GetProperty("items").GetArrayLength());
        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Patch, "/tenant", new { pageSizeDefault = 500, pageSizeMax = 200 }, owner.Token)).Status);
        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Patch, "/tenant", new { pageSizeMax = 10 }, owner.Token)).Status);
        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Patch, "/tenant", new { pageSizeDefault = 0 }, owner.Token)).Status);
        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Patch, "/tenant", new { pageSizeMax = 1001 }, owner.Token)).Status);
        var reader = await _session.AddMemberAsync(owner.Token, "reader");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = reader, role = "WorkspaceRead" }, owner.Token);
        var readerToken = await Invite(owner.Token, reader);
        Assert.Equal(403, (await _session.SendAsync(HttpMethod.Patch, "/tenant", new { pageSizeDefault = 10, pageSizeMax = 20 }, readerToken)).Status);

        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?query=" + Uri.EscapeDataString("""{"eq":0}"""), null, owner.Token)).Status);
        var malformed = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&query=not-json", null, owner.Token);
        Assert.Equal(400, malformed.Status);
        Assert.Equal("query: malformed JSON", Error(malformed.Body));
        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&pageSize=0", null, owner.Token)).Status);
        Assert.Equal(400, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&continuation=not-a-token", null, owner.Token)).Status);

        var hidden = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, other.Token);
        Assert.Equal(404, hidden.Status);
        var ownerNoRole = await _session.CreateTenantAsync("Plain");
        var foreign = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, ownerNoRole.Token);
        Assert.Equal(404, foreign.Status);

        await PutItem(owner.Token, workspace, list, "race", "{\"title\":\"a\",\"status\":\"open\",\"priority\":1,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");
        await PutItem(owner.Token, workspace, list, "race", "{\"title\":\"b\",\"status\":\"open\",\"priority\":2,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");
        Assert.Equal("b", Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items/race", null, owner.Token)).Body).GetProperty("data").GetProperty("title").GetString());

        var doomed = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Doomed", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        await PutItem(owner.Token, workspace, listId: doomed, "d1", "{\"title\":\"a\",\"status\":\"open\",\"priority\":1,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");
        await PutItem(owner.Token, workspace, doomed, "d2", "{\"title\":\"a\",\"status\":\"open\",\"priority\":1,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + workspace + "/lists/" + doomed, null, owner.Token)).Status);
        Assert.Equal(404, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + doomed + "/items/d1", null, owner.Token)).Status);
        var deletion = _session.Platform.Api.Services.GetRequiredService<DeletionService>();
        Assert.True(await deletion.DeleteNextBatchAsync(1, CancellationToken.None) > 0);
        Assert.Equal(404, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + doomed + "/items?shape=ids", null, owner.Token)).Status);
        await deletion.RunUntilQuietAsync(CancellationToken.None);
        Assert.Null(await _session.Platform.Database.GetAsync(owner.TenantId, workspace, doomed, "d1", CancellationToken.None));

        var gone = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Gone" }, owner.Token)).Body).GetProperty("id").GetString()!;
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Delete, "/workspaces/" + gone, null, owner.Token)).Status);
        Assert.Equal(404, (await _session.SendAsync(HttpMethod.Get, "/workspaces/" + gone + "/lists", null, owner.Token)).Status);

        foreach (var path in new[] { "/send-invitation", "/suspend", "/resume" })
        {
            Assert.Equal(401, (await _session.SendAsync(HttpMethod.Post, "/tenants/" + owner.TenantId + path, null, owner.Token)).Status);
            var wrong = new HttpRequestMessage(HttpMethod.Post, "/tenants/" + owner.TenantId + path);
            wrong.Headers.Add("X-Admin-Key", "nope");
            var response = await _session.Platform.ApiClient.SendAsync(wrong);
            Assert.Equal(401, (int)response.StatusCode);
        }

        var resent = await _session.SendAsync(HttpMethod.Post, "/tenants/" + owner.TenantId + "/send-invitation", null, null, admin: true);
        Assert.Equal(200, resent.Status);
        Assert.True(Json(resent.Body).GetProperty("emailSent").GetBoolean());
    }

    [Fact]
    [Trait("T", "T-35")]
    [Trait("T", "T-37")]
    [Trait("T", "T-41")]
    [Trait("T", "T-51")]
    [Trait("T", "T-59")]
    [Trait("T", "T-60")]
    public async Task FaultsAuditRaceAndOldItems_MatchTheSpec()
    {
        var owner = await _session.CreateTenantAsync("Faults");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } } }, owner.Token)).Body).GetProperty("id").GetString()!;
        var loose = """{"properties":[{"name":"title","type":"String","required":true}]}""";
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Loose", schema = JsonDocument.Parse(loose).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        await PutItem(owner.Token, workspace, list, "i5", "{\"title\":\"old\"}");
        await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token);
        var filtered = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&query=" + Uri.EscapeDataString("""{"where":[{"path":"priority","op":"ge","value":0}]}"""), null, owner.Token);
        Assert.Equal(200, filtered.Status);
        Assert.DoesNotContain("i5", filtered.Body, StringComparison.Ordinal);
        var sorted = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids&query=" + Uri.EscapeDataString("""{"orderBy":{"path":"priority"}}"""), null, owner.Token);
        Assert.DoesNotContain("i5", sorted.Body, StringComparison.Ordinal);

        _session.Platform.Database.AfterListRead = () =>
        {
            _session.Platform.Database.AfterListRead = null;
            var stored = _session.Platform.Database.GetAsync(owner.TenantId, workspace, list, CancellationToken.None).GetAwaiter().GetResult();
            if (stored is not null)
            {
                stored.SchemaVersion += 1;
                _session.Platform.Database.ReplaceAsync(stored, CancellationToken.None).GetAwaiter().GetResult();
            }
        };
        try
        {
            await PutItem(owner.Token, workspace, list, "raced", "{\"title\":\"now\",\"status\":\"open\",\"priority\":1,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");
        }
        finally
        {
            _session.Platform.Database.AfterListRead = null;
        }

        var raced = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items/raced", null, owner.Token)).Body);
        var current = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/schema", null, owner.Token)).Body).GetProperty("schemaVersion").GetInt32();
        Assert.True(raced.GetProperty("schemaVersion").GetInt32() < current);

        var big = new string('x', 2_200_000);
        var tooBig = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/big", new { title = big, status = "open", priority = 1, due = "2026-11-25T00:00:00Z", done = false }, owner.Token);
        Assert.Equal(413, tooBig.Status);
        _session.Platform.Database.WriteFault = ItemWriteFault.Throttled;
        _session.Platform.Database.ThrottleRetryAfter = TimeSpan.FromSeconds(3);
        var throttled = await _session.Platform.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/throttled", new { title = "ok", status = "open", priority = 1, due = "2026-11-25T00:00:00Z", done = false }, owner.Token, admin: false);
        Assert.Equal(429, throttled.Status);
        Assert.Equal("3", throttled.Response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
        _session.Platform.Database.WriteFault = ItemWriteFault.PartitionFull;
        var full = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/full", new { title = "ok", status = "open", priority = 1, due = "2026-11-25T00:00:00Z", done = false }, owner.Token);
        Assert.Equal(507, full.Status);
        Assert.Equal("$: list storage limit reached", Error(full.Body));

        _session.Platform.Email.Fail = true;
        try
        {
            var added = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = "new-" + Guid.NewGuid().ToString("N")[..8] + "@example.com", role = "WorkspaceRead" }, owner.Token);
            Assert.Equal(200, added.Status);
            Assert.False(Json(added.Body).GetProperty("emailSent").GetBoolean());
        }
        finally
        {
            _session.Platform.Email.Fail = false;
        }

        var suspended = await _session.SendAsync(HttpMethod.Post, "/tenants/" + owner.TenantId + "/suspend", null, null, admin: true);
        Assert.Equal(200, suspended.Status);
        var badToken = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list, null, "not-a-token");
        Assert.Equal(401, badToken.Status);
        var unknown = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + Guid.NewGuid(), null, owner.Token);
        Assert.Equal(403, unknown.Status);
        Assert.Equal("$: tenant suspended", Error(unknown.Body));
        await _session.SendAsync(HttpMethod.Post, "/tenants/" + owner.TenantId + "/resume", null, null, admin: true);
        var reader = await _session.AddMemberAsync(owner.Token, "reader");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = reader, role = "WorkspaceRead" }, owner.Token);
        var readerToken = await Invite(owner.Token, reader);
        var write = await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/nope", new { title = 1 }, readerToken);
        Assert.Equal(403, write.Status);

        Assert.Contains(_session.Platform.Audit.ForTenant(owner.TenantId), entry => entry.Action == AuditActions.TenantCreated);
        Assert.DoesNotContain(_session.Platform.Audit.Entries, entry => entry.Targets.Values.Any(value => value.Contains('.', StringComparison.Ordinal) && value.Length > 40));
    }

    [Fact]
    [Trait("T", "T-5")]
    public async Task PermissionMatrix_AllowsOnlyTheListedCells()
    {
        var owner = await _session.CreateTenantAsync("Matrix");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" }, owner.Token)).Body).GetProperty("id").GetString()!;
        var workspaceOwner = await _session.AddMemberAsync(owner.Token, "wo");
        var writer = await _session.AddMemberAsync(owner.Token, "ww");
        var reader = await _session.AddMemberAsync(owner.Token, "wr");
        var outsider = await _session.AddMemberAsync(owner.Token, "out");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = workspaceOwner, role = "WorkspaceOwner" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = writer, role = "WorkspaceWrite" }, owner.Token);
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = reader, role = "WorkspaceRead" }, owner.Token);
        var wo = await Invite(owner.Token, workspaceOwner);
        var ww = await Invite(owner.Token, writer);
        var wr = await Invite(owner.Token, reader);
        var none = await Invite(owner.Token, outsider);
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, wo)).Body).GetProperty("id").GetString()!;
        await PutItem(wo, workspace, list, "item", "{\"title\":\"t\",\"status\":\"open\",\"priority\":1,\"due\":\"2026-11-25T00:00:00Z\",\"done\":false}");

        await Expect(owner.Token, HttpMethod.Post, "/members", new { email = "extra-" + Guid.NewGuid().ToString("N")[..6] + "@example.com" }, 200);
        await Expect(wo, HttpMethod.Post, "/members", new { email = "nope@example.com" }, 403);
        await Expect(owner.Token, HttpMethod.Get, "/members", null, 200);
        await Expect(wo, HttpMethod.Get, "/members", null, 200);
        await Expect(ww, HttpMethod.Get, "/members", null, 403);
        await Expect(owner.Token, HttpMethod.Get, "/tenant", null, 200);
        await Expect(wo, HttpMethod.Patch, "/tenant", new { pageSizeDefault = 10, pageSizeMax = 20 }, 403);
        await Expect(wo, HttpMethod.Patch, "/workspaces/" + workspace, new { name = "Ops2" }, 200);
        await Expect(owner.Token, HttpMethod.Patch, "/workspaces/" + workspace, new { description = "no" }, 403);
        await Expect(ww, HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, 200);
        await Expect(wr, HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/schema", null, 200);
        await Expect(owner.Token, HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, 403);
        await Expect(none, HttpMethod.Get, "/workspaces/" + workspace + "/lists", null, 404);
        await Expect(ww, HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/item", new { title = "t2", status = "open", priority = 1, due = "2026-11-25T00:00:00Z", done = false }, 200);
        await Expect(wr, HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/items/item", new { title = "t3", status = "open", priority = 1, due = "2026-11-25T00:00:00Z", done = false }, 403);
        await Expect(wr, HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items/item", null, 200);
        await Expect(ww, HttpMethod.Patch, "/workspaces/" + workspace + "/lists/" + list, new { name = "Nope" }, 403);
        await Expect(wo, HttpMethod.Get, "/workspaces/" + workspace + "/members", null, 200);
        await Expect(ww, HttpMethod.Get, "/workspaces/" + workspace + "/members", null, 403);
    }

    [Fact]
    [Trait("T", "T-22")]
    public async Task GetItem_ReturnsTheStoredVersionWithoutRevalidation()
    {
        var owner = await _session.CreateTenantAsync("OldItem");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } } }, owner.Token)).Body).GetProperty("id").GetString()!;
        var loose = """{"properties":[{"name":"title","type":"String","required":true}]}""";
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Loose", schema = JsonDocument.Parse(loose).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        await PutItem(owner.Token, workspace, list, "old", "{\"title\":\"kept\"}");
        Assert.Equal(200, (await _session.SendAsync(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + list + "/schema", JsonDocument.Parse(TaskSchema).RootElement, owner.Token)).Status);
        var read = Json((await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items/old", null, owner.Token)).Body);
        Assert.Equal(1, read.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("kept", read.GetProperty("data").GetProperty("title").GetString());
    }

    [Fact]
    [Trait("T", "T-36")]
    [Trait("T", "T-10")]
    public async Task HiddenWorkspaceIs404_AndOwnerCannotDemoteSelfByInvite()
    {
        var owner = await _session.CreateTenantAsync("Hidden");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops" }, owner.Token)).Body).GetProperty("id").GetString()!;
        var workspaceOwner = await _session.AddMemberAsync(owner.Token, "wo");
        var outsider = await _session.AddMemberAsync(owner.Token, "out");
        await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = workspaceOwner, role = "WorkspaceOwner" }, owner.Token);
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, await Invite(owner.Token, workspaceOwner))).Body).GetProperty("id").GetString()!;
        var outsiderToken = await Invite(owner.Token, outsider);
        var hidden = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids", null, outsiderToken);
        Assert.Equal(404, hidden.Status);
        var tenantOwner = await _session.SendAsync(HttpMethod.Get, "/workspaces/" + workspace + "/lists/" + list + "/items?shape=ids", null, owner.Token);
        Assert.Equal(403, tenantOwner.Status);
        Assert.Equal("$: not allowed", Error(tenantOwner.Body));
        var ownerToken = await Invite(owner.Token, workspaceOwner);
        var demotion = await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/members", new { email = workspaceOwner, role = "WorkspaceRead" }, ownerToken);
        Assert.Equal(403, demotion.Status);
        Assert.Equal("$: not allowed on yourself", Error(demotion.Body));
    }

    [Fact]
    [Trait("T", "T-53")]
    public async Task EncodedForbiddenItemId_IsRejectedOnRestAndMcp()
    {
        var owner = await _session.CreateTenantAsync("Slash");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } } }, owner.Token)).Body).GetProperty("id").GetString()!;
        var loose = """{"properties":[{"name":"title","type":"String","required":true}]}""";
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Loose", schema = JsonDocument.Parse(loose).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        foreach (var itemId in new[] { "a%2Fb", "a%5Cb", "a%3Fb", "a%23b" })
        {
            var path = "/workspaces/" + workspace + "/lists/" + list + "/items/" + itemId;
            var put = await _session.SendAsync(HttpMethod.Put, path, new { title = "x" }, owner.Token);
            Assert.Equal(400, put.Status);
            Assert.Equal("itemId: invalid", Error(put.Body));
            Assert.DoesNotContain("Exception", put.Body, StringComparison.Ordinal);
            var get = await _session.SendAsync(HttpMethod.Get, path, null, owner.Token);
            Assert.Equal(400, get.Status);
            Assert.Equal("itemId: invalid", Error(get.Body));
            var deleted = await _session.SendAsync(HttpMethod.Delete, path, null, owner.Token);
            Assert.Equal(400, deleted.Status);
            Assert.Equal("itemId: invalid", Error(deleted.Body));
        }

        var mcp = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["itemId"] = "a%2Fb",
            ["data"] = Session.Node("""{"title":"x"}"""),
        });
        Assert.True(mcp.IsError);
        Assert.Equal("itemId: invalid", mcp.Text);
        var slash = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["itemId"] = "a/b",
            ["data"] = Session.Node("""{"title":"x"}"""),
        });
        Assert.True(slash.IsError);
        Assert.Equal("itemId: invalid", slash.Text);
    }

    [Fact]
    [Trait("T", "T-52")]
    public async Task QueryItems_RejectsAnObjectWhereAndUnknownArguments()
    {
        var owner = await _session.CreateTenantAsync("QueryShape");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } } }, owner.Token)).Body).GetProperty("id").GetString()!;
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        var where = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = "ids",
            ["where"] = Session.Node("""{"path":"status"}"""),
        });
        Assert.True(where.IsError);
        Assert.Equal("where: expected array, found Object", where.Text);
        var nested = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = "ids",
            ["where"] = Session.Node("""[{"path":"title","op":"eq","value":"a","foo":1}]"""),
        });
        Assert.True(nested.IsError);
        Assert.Equal("where[0].foo: unknown field", nested.Text);
        var extra = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = "ids",
            ["extra"] = 1,
        });
        Assert.True(extra.IsError);
        Assert.Equal("extra: unknown field", extra.Text);
        var data = await _session.ToolAsync(owner.Token, "upsert_item", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["data"] = Session.Node("[]"),
        });
        Assert.True(data.IsError);
        Assert.Contains("expected Object", data.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpRawArguments_ReturnThePinnedTexts()
    {
        var owner = await _session.CreateTenantAsync("Raw");
        var workspace = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Ops", members = new[] { new { email = owner.Email, role = "WorkspaceOwner" } } }, owner.Token)).Body).GetProperty("id").GetString()!;
        var created = await _session.ToolAsync(owner.Token, "create_list", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["name"] = "Bad",
            ["schema"] = Session.Node("[]"),
        });
        Assert.True(created.IsError);
        Assert.Equal("$: expected Object, found Array", created.Text);
        var list = Json((await _session.SendAsync(HttpMethod.Post, "/workspaces/" + workspace + "/lists", new { name = "Tasks", schema = JsonDocument.Parse(TaskSchema).RootElement }, owner.Token)).Body).GetProperty("id").GetString()!;
        var replaced = await _session.ToolAsync(owner.Token, "update_schema", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["schema"] = Session.Node("1"),
        });
        Assert.True(replaced.IsError);
        Assert.Equal("$: expected Object, found Number", replaced.Text);

        Assert.Equal("pageSize: must be a whole number of at least 1", (await PageAsync("list_workspaces", owner.Token, null, 1.5)).Text);
        Assert.Equal("pageSize: must be a whole number of at least 1", (await PageAsync("list_lists", owner.Token, workspace, "10")).Text);
        Assert.Equal("pageSize: must be a whole number of at least 1", (await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = "ids",
            ["pageSize"] = true,
        })).Text);
        Assert.Equal("pageSize: must be a whole number of at least 1", (await PageAsync("list_workspace_members", owner.Token, workspace, true)).Text);

        var shape = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = 1,
        });
        Assert.True(shape.IsError);
        Assert.Equal("shape: must be full or ids", shape.Text);
        var continuation = await _session.ToolAsync(owner.Token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace,
            ["listId"] = list,
            ["shape"] = "ids",
            ["continuation"] = true,
        });
        Assert.True(continuation.IsError);
        Assert.Equal("continuation: invalid token", continuation.Text);
        var listed = await _session.ToolAsync(owner.Token, "list_workspaces", new Dictionary<string, object?>
        {
            ["continuation"] = 1,
        });
        Assert.True(listed.IsError);
        Assert.Equal("continuation: invalid token", listed.Text);

        var jsonNull = JsonSerializer.Deserialize<JsonElement>("null");
        var where = await QueryAsync(owner.Token, workspace, list, "where", jsonNull);
        Assert.Equal("where: expected array, found Null", where.Text);
        var orderBy = await QueryAsync(owner.Token, workspace, list, "orderBy", jsonNull);
        Assert.Equal("orderBy: expected Object, found Null", orderBy.Text);
        var schemaVersion = await QueryAsync(owner.Token, workspace, list, "schemaVersion", jsonNull);
        Assert.Equal("schemaVersion: invalid filter", schemaVersion.Text);
    }

    #region private

    private async Task PutItem(string token, string workspace, string listId, string itemId, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Put, "/workspaces/" + workspace + "/lists/" + listId + "/items/" + itemId)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _session.Platform.ApiClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == 200, (int)response.StatusCode + " " + body);
    }

    private async Task<(bool IsError, string Text)> PageAsync(string tool, string token, string? workspaceId, object pageSize)
    {
        var arguments = new Dictionary<string, object?> { ["pageSize"] = pageSize };
        if (workspaceId is not null)
        {
            arguments["workspaceId"] = workspaceId;
        }

        var result = await _session.ToolAsync(token, tool, arguments);
        Assert.True(result.IsError);
        return result;
    }

    private async Task<(bool IsError, string Text)> QueryAsync(string token, string workspaceId, string listId, string name, JsonElement value)
    {
        var result = await _session.ToolAsync(token, "query_items", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspaceId,
            ["listId"] = listId,
            ["shape"] = "ids",
            [name] = value,
        });
        Assert.True(result.IsError);
        return result;
    }

    private async Task<string> Invite(string ownerToken, string email)
    {
        var response = await _session.SendAsync(HttpMethod.Post, "/members/" + Uri.EscapeDataString(email) + "/send-invitation", new { }, ownerToken);
        Assert.Equal(200, response.Status);
        return Session.TokenFrom(_session.Platform.Email.For(email).Last().Body);
    }

    private async Task Expect(string token, HttpMethod method, string url, object? body, int status)
    {
        var response = await _session.SendAsync(method, url, body, token);
        Assert.True(response.Status == status, method + " " + url + " " + response.Status + " " + response.Body);
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
