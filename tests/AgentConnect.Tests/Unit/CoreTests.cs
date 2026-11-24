using System.Net;
using System.Text;
using System.Text.Json;
using AgentConnect.Tests.Support;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentConnect.Tests.Unit;

/// <summary>Unit tests for the shared rules. Traits name the spec cases they lock.</summary>
public sealed class CoreTests
{
    private const string TasksSchema = """
        {"properties":[
          {"name":"title","type":"String","required":true},
          {"name":"status","type":"String","required":true,"enum":["open","blocked","done"]},
          {"name":"priority","type":"Int32","required":true},
          {"name":"due","type":"DateTime","required":true},
          {"name":"done","type":"Boolean","required":true},
          {"name":"tags","type":"String[]"},
          {"name":"owner","type":"String"},
          {"name":"labels","type":"String[]","required":true},
          {"name":"address","type":"Object","required":true,"properties":[{"name":"city","type":"String","required":true}]},
          {"name":"comments","type":"Object[]","properties":[{"name":"text","type":"String"}]}
        ]}
        """;

    [Fact]
    [Trait("T", "T-42")]
    public void Issue_StoresHashAndCiphertext_NotThePlainToken()
    {
        var service = new TokenService(Convert.ToBase64String(new byte[32]));
        var issued = service.Issue("tenant", "member");
        Assert.DoesNotContain(issued.Token, issued.Hash, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Token, issued.Encrypted, StringComparison.Ordinal);
        Assert.True(service.TryParse(issued.Token, out var tenant, out var member, out var secret));
        Assert.Equal("tenant", tenant);
        Assert.Equal("member", member);
        Assert.True(service.HashMatches(secret, issued.Hash));
        Assert.Equal(issued.Token, service.Decrypt(issued.Encrypted));
    }

    [Fact]
    [Trait("T", "T-53")]
    public void EmailRules_RejectsTheThreeInvalidForms()
    {
        Assert.False(EmailRules.TryNormalize("ann@", out _, out var error));
        Assert.Equal("email: invalid format", error);
        Assert.False(EmailRules.TryNormalize("ann example.com", out _, out error));
        Assert.Equal("email: invalid format", error);
        Assert.False(EmailRules.TryNormalize("ann@example", out _, out error));
        Assert.Equal("email: invalid format", error);
        Assert.True(EmailRules.TryNormalize(" Ann@Example.com ", out var trimmed, out _));
        Assert.True(EmailRules.Same("ann@example.com", trimmed));
    }

    [Fact]
    [Trait("T", "T-53")]
    public void ItemIdRules_RejectsSlash()
    {
        Assert.False(ItemIdRules.IsValid("a/b"));
        Assert.False(ItemIdRules.IsValid("a\\b"));
        Assert.False(ItemIdRules.IsValid("a?b"));
        Assert.False(ItemIdRules.IsValid("a#b"));
        Assert.False(ItemIdRules.IsValid("a%2Fb"));
        Assert.False(ItemIdRules.IsValid("a%2fb"));
        Assert.False(ItemIdRules.IsValid("a%5Cb"));
        Assert.False(ItemIdRules.IsValid("a%3Fb"));
        Assert.False(ItemIdRules.IsValid("a%23b"));
        Assert.False(ItemIdRules.IsValid("a%20b"));
        Assert.False(ItemIdRules.IsValid("a%2Fb"));
        Assert.False(ItemIdRules.IsValid("%"));
        Assert.False(ItemIdRules.IsValid("trail%"));
        Assert.True(ItemIdRules.IsValid("task-1"));
        Assert.True(ItemIdRules.IsValid("a+b"));
    }

    [Theory]
    [InlineData(100, 50, "pageSizeDefault: must not exceed pageSizeMax")]
    [InlineData(0, 100, "pageSizeDefault: must be a whole number of at least 1")]
    [InlineData(20, 1001, "pageSizeMax: must not exceed 1000")]
    [Trait("T", "T-54")]
    public void PageRules_RejectsTheSpecPairs(int pageSizeDefault, int pageSizeMax, string expected)
    {
        Assert.Equal(expected, PageRules.ValidatePair(pageSizeDefault, pageSizeMax));
    }

    [Fact]
    [Trait("T", "T-56")]
    public void Templates_MatchTheThreeMessages()
    {
        var invitation = EmailTemplates.Invitation("ann@example.com", "Acme", "AgentConnect", null, null, "tok", "http://mcp", "http://api");
        Assert.Contains("AgentConnect", invitation.Subject, StringComparison.Ordinal);
        Assert.Contains("Acme", invitation.Subject, StringComparison.Ordinal);
        Assert.Contains("Invited by: AgentConnect", invitation.Body, StringComparison.Ordinal);
        Assert.Contains("Token: tok", invitation.Body, StringComparison.Ordinal);
        Assert.Contains(EmailTemplates.TokenNote, invitation.Body, StringComparison.Ordinal);
        Assert.Contains("http://mcp", invitation.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace:", invitation.Body, StringComparison.Ordinal);

        var workspaceInvite = EmailTemplates.Invitation("ann@example.com", "Acme", "owner@example.com", "Ops", "WorkspaceWrite", "tok", "http://mcp", "http://api");
        Assert.Contains("Workspace: Ops", workspaceInvite.Body, StringComparison.Ordinal);
        Assert.Contains("Role: WorkspaceWrite", workspaceInvite.Body, StringComparison.Ordinal);

        var token = EmailTemplates.Token("ann@example.com", "Acme", "tok", "http://mcp", "http://api", previousTokenNoLongerWorks: true);
        Assert.Contains(EmailTemplates.PreviousTokenLine, token.Body, StringComparison.Ordinal);
        var quiet = EmailTemplates.Token("ann@example.com", "Acme", "tok", "http://mcp", "http://api", previousTokenNoLongerWorks: false);
        Assert.DoesNotContain(EmailTemplates.PreviousTokenLine, quiet.Body, StringComparison.Ordinal);

        var access = EmailTemplates.WorkspaceAccess("ann@example.com", "Acme", "Ops", "WorkspaceRead", "owner@example.com");
        Assert.Contains("AgentConnect", access.Subject, StringComparison.Ordinal);
        Assert.Contains("Acme", access.Subject, StringComparison.Ordinal);
        Assert.Contains("Granted by: owner@example.com", access.Body, StringComparison.Ordinal);
        Assert.False(access.ContainsToken);
        Assert.DoesNotContain("Token:", access.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("T", "T-61")]
    public void DateTimes_AreStoredWithSevenFractionDigits()
    {
        Assert.True(SchemaGateway.TryCanonicalDateTime("2026-11-25T10:00:00+01:00", out var zoned));
        Assert.Equal("2026-11-25T09:00:00.0000000Z", zoned);
        Assert.True(SchemaGateway.TryCanonicalDateTime("2026-11-25T10:00:00.5+01:00", out var fraction));
        Assert.Equal("2026-11-25T09:00:00.5000000Z", fraction);
        Assert.True(SchemaGateway.TryCanonicalDateTime("2026-11-25T04:00:00-05:00", out var west));
        Assert.Equal("2026-11-25T09:00:00.0000000Z", west);
        Assert.True(SchemaGateway.TryCanonicalDateTime("2026-11-25", out var date));
        Assert.Equal("2026-11-25T00:00:00.0000000Z", date);
        Assert.True(SchemaGateway.TryCanonicalDateTime("2026-11-25T10:00:00", out var zoneless));
        Assert.Equal("2026-11-25T10:00:00.0000000Z", zoneless);
    }

    [Fact]
    [Trait("T", "T-62")]
    public async Task AuditRecorder_KeepsEveryEventWhenSamplingDropsOtherTelemetry()
    {
        var auditEvents = await ApplicationInsightsAuditRecorder.ExerciseProductionSamplingAsync(200, CancellationToken.None);
        Assert.Equal(200, auditEvents);
    }

    [Fact]
    public void ContinuationToken_RejectsATamperedPageSizeAndClampsAtThePlatformCap()
    {
        ContinuationTokens.Configure(Convert.ToBase64String(new byte[32]));
        var issued = ContinuationTokens.Issue("fingerprint", 5000, "cursor");
        Assert.True(ContinuationTokens.TryOpen(issued, "fingerprint", out var clamped, out var cursor));
        Assert.Equal(1000, clamped);
        Assert.Equal("cursor", cursor);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(issued.Replace('-', '+').Replace('_', '/'))));
        var tampered = json.Replace("\"P\":1000", "\"P\":5000", StringComparison.Ordinal);
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(ContinuationTokens.TryOpen(forged, "fingerprint", out _, out _));
    }

    [Fact]
    public async Task Invite_DoesNotRestoreATokenRegeneratedWhileTheMailIsSending()
    {
        var database = new MemoryDatabase();
        var email = new InterruptingEmail();
        var key = Convert.ToBase64String(new byte[32]);
        var options = new AgentConnectOptions
        {
            TokenEncryptionKey = key,
            McpServerUrl = "http://localhost/mcp",
            RestApiUrl = "http://localhost",
        };
        var tokens = new TokenService(key);
        var members = new MemberService(database, database, tokens, email, new RecordingAudit(), new SystemClock(), options, NullLogger<MemberService>.Instance);
        var tenants = new TenantService(database, database, tokens, email, new RecordingAudit(), new SystemClock(), options, NullLogger<TenantService>.Instance);
        var created = await tenants.CreateAsync("Race", "owner@example.com", CancellationToken.None);
        Assert.True(created.IsSuccess);
        var tenant = (await database.GetAsync(created.Value!.Id, CancellationToken.None))!;
        var owner = (await database.GetAsync(tenant.Id, tenant.FirstMemberId, CancellationToken.None))!;
        var caller = new Caller(owner, tenant);
        var added = await members.AddAsync(caller, "ann@example.com", CancellationToken.None);
        Assert.True(added.IsSuccess);
        var before = (await database.GetAsync(tenant.Id, added.Value!.Id, CancellationToken.None))!;
        email.OnSend = async () =>
        {
            var regenerated = await members.RegenerateAsync(caller, "ann@example.com", CancellationToken.None);
            Assert.True(regenerated.IsSuccess);
        };
        var sent = await members.SendInvitationAsync(caller, "ann@example.com", CancellationToken.None);
        Assert.True(sent.IsSuccess);
        Assert.True(sent.Value!.EmailSent);
        var after = (await database.GetAsync(tenant.Id, added.Value.Id, CancellationToken.None))!;
        Assert.NotEqual(before.TokenHash, after.TokenHash);
        Assert.True(after.InvitationSent);
    }

    [Fact]
    [Trait("T", "T-35")]
    public void CosmosErrors_MapTheThreeStorageFailures()
    {
        var throttled = new CosmosException("throttle", HttpStatusCode.TooManyRequests, 0, "act", 1);
        var mapped = CosmosErrors.MapWrite(throttled);
        Assert.IsType<StorageThrottleException>(mapped);
        Assert.IsType<StorageTooLargeException>(CosmosErrors.MapWrite(new CosmosException("big", HttpStatusCode.RequestEntityTooLarge, 0, "act", 1)));
        Assert.IsType<StorageLimitException>(CosmosErrors.MapWrite(new CosmosException("full", HttpStatusCode.Forbidden, CosmosErrors.PartitionKeyQuotaOverLimit, "act", 1)));
        Assert.IsType<StorageThrottleException>(CosmosErrors.MapWrite(new CosmosException("timeout", HttpStatusCode.RequestTimeout, 0, "act", 1)));
        Assert.IsType<StorageThrottleException>(CosmosErrors.MapWrite(new CosmosException("unavailable", HttpStatusCode.ServiceUnavailable, 0, "act", 1)));
        var unexpected = Assert.IsType<StorageFailureException>(CosmosErrors.MapWrite(new CosmosException("signing secret", HttpStatusCode.Unauthorized, 42, "activity-9", 1)));
        Assert.Equal("$: internal error", unexpected.Message);
        Assert.Equal(401, unexpected.StatusCode);
        Assert.Equal(42, unexpected.SubStatusCode);
        Assert.Equal("activity-9", unexpected.ActivityId);
        Assert.DoesNotContain("signing", unexpected.Message, StringComparison.Ordinal);
        var logger = new ListLogger();
        PlatformSupport.LogCosmosFault(logger, unexpected);
        Assert.Contains(logger.Lines, line => line.Contains("status 401", StringComparison.Ordinal) && line.Contains("substatus 42", StringComparison.Ordinal) && line.Contains("activity activity-9", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("signing", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("T", "T-44")]
    public void QueryParser_ReturnsTheFirstError()
    {
        var schema = SchemaIndex.Load(TasksSchema);
        AssertError(Parse(schema, null, default, default, default, []), "shape: required");
        AssertError(Parse(schema, "x", default, default, default, []), "shape: must be full or ids");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"nope\",\"op\":\"eq\",\"value\":1}]"), default, []), "where[0].path: unknown property");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"comments.text\",\"op\":\"eq\",\"value\":\"a\"}]"), default, []), "where[0].path: unknown property");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"priority\",\"op\":\"contains\",\"value\":1}]"), default, []), "where[0].op: not allowed for Int32");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"priority\",\"op\":\"eq\",\"value\":\"1\"}]"), default, []), "where[0].value: expected Int32, found String");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"priority\",\"op\":\"in\",\"value\":[]}]"), default, []), "where[0].value: expected non-empty array");
        AssertError(Parse(schema, "full", default, default, Json("{\"path\":\"owner\"}"), []), "orderBy.path: must be a required property");
        AssertError(Parse(schema, "full", default, default, Json("{\"path\":\"labels\"}"), []), "orderBy.path: cannot sort on String[]");
        AssertError(Parse(schema, "full", Json("{\"eq\":0}"), default, default, []), "schemaVersion: invalid filter");
        AssertError(Parse(schema, "full", default, Json("{}"), default, []), "where: expected array, found Object");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"title\"}]"), default, []), "where[0].op: required");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"title\",\"op\":\"like\",\"value\":\"a\"}]"), default, []), "where[0].op: unknown operator");
        AssertError(Parse(schema, "full", default, Json("[{\"path\":\"title\",\"op\":\"eq\",\"value\":\"a\",\"foo\":1}]"), default, []), "where[0].foo: unknown field");
        AssertError(Parse(schema, "full", default, default, Json("{\"path\":\"priority\",\"desc\":\"yes\"}"), []), "orderBy.desc: expected Boolean, found String");
    }

    [Fact]
    [Trait("T", "T-45")]
    [Trait("T", "T-46")]
    [Trait("T", "T-64")]
    public void QueryEngine_MatchesFixtureF1()
    {
        var items = Fixture();
        var schema = SchemaIndex.Load(TasksSchema);
        SameSet(["i1", "i2"], Ids(items, schema, where: """[{"path":"title","op":"startsWith","value":"Fix"}]"""));
        SameSet(["i3"], Ids(items, schema, where: """[{"path":"title","op":"contains","value":"docs"}]"""));
        SameSet(["i1", "i4"], Ids(items, schema, where: """[{"path":"priority","op":"ge","value":3}]"""));
        SameSet(["i1", "i3"], Ids(items, schema, where: """[{"path":"due","op":"lt","value":"2026-12-01T00:00:00Z"}]"""));
        SameSet(["i3"], Ids(items, schema, where: """[{"path":"done","op":"eq","value":true}]"""));
        SameSet(["i1", "i2"], Ids(items, schema, where: """[{"path":"tags","op":"contains","value":"auth"}]"""));
        Assert.Empty(Ids(items, schema, where: """[{"path":"status","op":"eq","value":"Open"}]"""));
        SameSet(["i2", "i3"], Ids(items, schema, where: """[{"path":"status","op":"ne","value":"open"}]"""));
        SameSet(["i2"], Ids(items, schema, where: """[{"path":"owner","op":"ne","value":"ann"}]"""));
        Assert.Equal(["i4", "i1", "i3", "i2"], Ids(items, schema, order: """{"path":"priority","desc":true}"""));
        Assert.Equal(["i3", "i1", "i2", "i4"], Ids(items, schema));

        var pages = Pages(items, schema, """{"path":"done"}""", 1);
        Assert.Equal(4, pages.Count);
        Assert.Equal("i3", pages[3][0]);
        Assert.Equal(["i1", "i2", "i4"], pages.Take(3).Select(page => page[0]).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    [Trait("T", "T-5")]
    public void Permissions_FollowTheMatrixForWorkspaceRead()
    {
        var caller = Caller(isOwner: false, StoredRoles.Read);
        Assert.Equal(403, Permissions.AuthorizeWorkspace(caller, "w", PlatformAction.UpsertItem, null, null, null)!.StatusCode);
        Assert.Null(Permissions.AuthorizeWorkspace(caller, "w", PlatformAction.GetItem, null, null, null));
        Assert.Equal(404, Permissions.AuthorizeWorkspace(Caller(false, null), "w", PlatformAction.GetItem, null, null, null)!.StatusCode);
        Assert.Equal(403, Permissions.AuthorizeWorkspace(Caller(true, null), "w", PlatformAction.GetItem, null, null, null)!.StatusCode);
        Assert.Equal(403, Permissions.AuthorizeTenant(Caller(true, null), PlatformAction.DeleteTenantMember, "self")!.StatusCode);
        Assert.Equal("$: not allowed on yourself", Permissions.AuthorizeTenant(Caller(true, null), PlatformAction.DeleteTenantMember, "self")!.Text);
    }

    [Fact]
    public void CosmosSql_KeepsValuesOutOfTheText()
    {
        var schema = SchemaIndex.Load(TasksSchema);
        var parsed = ItemQueryParser.Parse(schema, 1, "t", "w", "l", "full", default, Json("""[{"path":"title","op":"eq","value":"Fix login"}]"""), default, [], 10, null);
        var sql = CosmosQuerySql.Build(parsed.Value!).QueryText;
        Assert.DoesNotContain("Fix login", sql, StringComparison.Ordinal);
        Assert.Contains("@p0", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY c.updated ASC, c.id ASC", sql, StringComparison.Ordinal);
    }

    #region private

    private static void SameSet(string[] expected, string[] actual)
    {
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), actual.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AssertError(OperationResult<CompiledItemQuery> result, string text)
    {
        Assert.Equal(text, result.Error!.Text);
    }

    private static OperationResult<CompiledItemQuery> Parse(SchemaIndex schema, string? shape, JsonElement schemaVersion, JsonElement where, JsonElement orderBy, IReadOnlyList<string> unknown)
    {
        return ItemQueryParser.Parse(schema, 4, "t", "w", "l", shape, schemaVersion, where, orderBy, unknown, 10, null);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string[] Ids(IReadOnlyList<ItemDocument> items, SchemaIndex schema, string? where = null, string? order = null)
    {
        var parsed = ItemQueryParser.Parse(schema, 4, "t", "w", "l", "ids", default, where is null ? default : Json(where), order is null ? default : Json(order), [], 50, null);
        var page = ItemQueryEngine.Execute(items, parsed.Value!);
        return page.Items.Select(item => item.Id).ToArray();
    }

    private static List<string[]> Pages(IReadOnlyList<ItemDocument> items, SchemaIndex schema, string order, int pageSize)
    {
        var pages = new List<string[]>();
        string? cursor = null;
        var parsed = ItemQueryParser.Parse(schema, 4, "t", "w", "l", "ids", default, default, Json(order), [], pageSize, null);
        for (var guard = 0; guard < 10; guard++)
        {
            var query = parsed.Value! with { StoreCursor = cursor };
            var page = ItemQueryEngine.Execute(items, query);
            pages.Add(page.Items.Select(item => item.Id).ToArray());
            if (page.Continuation is null)
            {
                break;
            }

            cursor = page.Continuation;
        }

        return pages;
    }

    private static List<ItemDocument> Fixture()
    {
        return
        [
            Item("i1", """{"title":"Fix login","status":"open","priority":3,"due":"2026-11-25T09:00:00.0000000Z","done":false,"tags":["auth","web"],"owner":"ann","labels":[],"address":{"city":"a"}}""", "2026-11-23T10:00:00.0000000Z"),
            Item("i2", """{"title":"Fix logout","status":"blocked","priority":1,"due":"2026-12-03T09:00:00.0000000Z","done":false,"tags":["auth"],"owner":"bob","labels":[],"address":{"city":"a"}}""", "2026-11-23T10:00:00.0000000Z"),
            Item("i3", """{"title":"Write docs","status":"done","priority":2,"due":"2026-11-20T09:00:00.0000000Z","done":true,"tags":[],"labels":[],"address":{"city":"a"}}""", "2026-11-23T09:00:00.0000000Z"),
            Item("i4", """{"title":"Plan sprint","status":"open","priority":5,"due":"2026-12-10T09:00:00.0000000Z","done":false,"labels":[],"address":{"city":"a"}}""", "2026-11-23T11:00:00.0000000Z"),
        ];
    }

    private static ItemDocument Item(string id, string data, string updated)
    {
        return new ItemDocument
        {
            Id = id,
            TenantId = "t",
            WorkspaceId = "w",
            ListId = "l",
            Data = ItemDocument.ParseData(data),
            SchemaVersion = 3,
            Created = DateTimeOffset.Parse(updated),
            Updated = DateTimeOffset.Parse(updated),
        };
    }

    private static Caller Caller(bool isOwner, string? role)
    {
        var member = new MemberDocument
        {
            Id = "self",
            TenantId = "t",
            Email = "a@example.com",
            IsTenantOwner = isOwner,
        };
        if (role is not null)
        {
            member.WorkspaceRoles["w"] = role;
        }

        return new Caller(member, new TenantDocument { Id = "t", Status = TenantStatuses.Active });
    }

    private static string Pad(string text)
    {
        var remainder = text.Length % 4;
        return remainder == 0 ? text : text + new string('=', 4 - remainder);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class InterruptingEmail : IEmailSender
    {
        public Func<Task>? OnSend { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            return OnSend is null ? Task.CompletedTask : OnSend();
        }
    }

    #endregion
}
