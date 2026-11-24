using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentConnect.Tests.Integration;

/// <summary>Skips unless the Cosmos DB emulator endpoint and key are configured.</summary>
public sealed class CosmosEmulatorFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="CosmosEmulatorFactAttribute"/> class.</summary>
    public CosmosEmulatorFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCONNECT_COSMOS_ENDPOINT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCONNECT_COSMOS_KEY")))
        {
            Skip = "Requires AGENTCONNECT_COSMOS_ENDPOINT and AGENTCONNECT_COSMOS_KEY (Cosmos DB emulator).";
        }
    }
}

/// <summary>Runs the Cosmos-backed cases. Skipped in this environment when the emulator is not configured.</summary>
[Collection("cosmos-emulator")]
public sealed class CosmosEmulatorTests
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
          {"name":"address","type":"Object","required":true,"properties":[{"name":"city","type":"String","required":true}]}
        ]}
        """;

    private readonly CosmosPlatform _platform;

    /// <summary>Initializes a new instance of the <see cref="CosmosEmulatorTests"/> class.</summary>
    /// <param name="platform">The shared emulator platform.</param>
    public CosmosEmulatorTests(CosmosPlatform platform)
    {
        _platform = platform;
    }

    /// <summary>Filtered and sorted item queries run inside Cosmos. T-45, T-46, T-64.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-45")]
    [Trait("T", "T-46")]
    [Trait("T", "T-64")]
    public async Task QueryItems_FiltersAndSortsInsideCosmos()
    {
        var caller = await _platform.OwnerAsync("Query");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        await _platform.GrantAsync(caller, workspace, caller.Member.Email, "WorkspaceOwner");
        caller = await _platform.ReloadAsync(caller);
        var list = await _platform.ListAsync(caller, workspace, "Tasks", TasksSchema);
        await _platform.PutAsync(caller, workspace, list, "i3", Item("Write docs", "done", 2, "2026-11-20T09:00:00Z", true, "[]", null), "2026-11-23T09:00:00Z");
        await _platform.PutAsync(caller, workspace, list, "i1", Item("Fix login", "open", 3, "2026-11-25T09:00:00Z", false, "[\"auth\",\"web\"]", "ann"), "2026-11-23T10:00:00Z");
        await _platform.PutAsync(caller, workspace, list, "i2", Item("Fix logout", "blocked", 1, "2026-12-03T09:00:00Z", false, "[\"auth\"]", "bob"), "2026-11-23T10:00:00Z");
        await _platform.PutAsync(caller, workspace, list, "i4", Item("Plan sprint", "open", 5, "2026-12-10T09:00:00Z", false, "[]", null), "2026-11-23T11:00:00Z");

        Assert.Equal(["i1", "i2"], await Ids(caller, workspace, list, where: """[{"path":"title","op":"startsWith","value":"Fix"}]"""));
        Assert.Equal(["i3"], await Ids(caller, workspace, list, where: """[{"path":"title","op":"contains","value":"docs"}]"""));
        Assert.Equal(["i1", "i4"], await Ids(caller, workspace, list, where: """[{"path":"priority","op":"ge","value":3}]"""));
        Assert.Equal(["i1", "i3"], await Ids(caller, workspace, list, where: """[{"path":"due","op":"lt","value":"2026-12-01T00:00:00Z"}]"""));
        Assert.Equal(["i3"], await Ids(caller, workspace, list, where: """[{"path":"done","op":"eq","value":true}]"""));
        Assert.Equal(["i1", "i2"], await Ids(caller, workspace, list, where: """[{"path":"tags","op":"contains","value":"auth"}]"""));
        Assert.Empty(await Ids(caller, workspace, list, where: """[{"path":"status","op":"eq","value":"Open"}]"""));
        Assert.Equal(["i2", "i3"], await Ids(caller, workspace, list, where: """[{"path":"status","op":"ne","value":"open"}]"""));
        Assert.Equal(["i2"], await Ids(caller, workspace, list, where: """[{"path":"owner","op":"ne","value":"ann"}]"""));
        Assert.Equal(["i4", "i1", "i3", "i2"], await Ids(caller, workspace, list, order: """{"path":"priority","desc":true}"""));
        Assert.Equal(["i3", "i1", "i2", "i4"], await Ids(caller, workspace, list));

        var seen = new List<string>();
        string? token = null;
        for (var guard = 0; guard < 4; guard++)
        {
            var page = Ok(await _platform.Content.QueryItemsAsync(caller, workspace, list, "ids", default, default, Node("""{"path":"done"}"""), [], 1, true, token, CancellationToken.None));
            Assert.Single(page.Items);
            seen.Add(page.Items[0].Id);
            token = page.Continuation;
            if (token is null)
            {
                break;
            }
        }

        Assert.Equal(4, seen.Count);
        Assert.Equal("i3", seen[3]);
        Assert.Equal(["i1", "i2", "i4"], seen.Take(3).Order(StringComparer.Ordinal).ToArray());
        Assert.Null(token);
    }

    /// <summary>schemaVersion filters select the stored version. T-23.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-23")]
    public async Task QueryItems_FiltersByStoredSchemaVersion()
    {
        var caller = await _platform.OwnerAsync("Versions");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        await _platform.GrantAsync(caller, workspace, caller.Member.Email, "WorkspaceOwner");
        caller = await _platform.ReloadAsync(caller);
        var loose = """{"properties":[{"name":"title","type":"String","required":true}]}""";
        var list = await _platform.ListAsync(caller, workspace, "Loose", loose);
        await _platform.PutAsync(caller, workspace, list, "old", "{\"title\":\"kept\"}", null);
        Assert.True(Ok(await _platform.Content.UpdateSchemaAsync(caller, workspace, list, TasksSchema, CancellationToken.None)).SchemaVersion == 2);
        await _platform.PutAsync(caller, workspace, list, "new", Item("Now", "open", 1, "2026-11-25T00:00:00Z", false, "[]", null), null);
        Assert.Equal(["old"], await Ids(caller, workspace, list, schemaVersion: """{"eq":1}"""));
        Assert.Equal(["old"], await Ids(caller, workspace, list, schemaVersion: """{"belowCurrent":true}"""));
        Assert.Equal(["new"], await Ids(caller, workspace, list, schemaVersion: """{"eq":2}"""));
    }

    /// <summary>Pages of items, workspace members, and a non-owner workspace list do not skip rows. T-34.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-34")]
    public async Task Pages_DoNotSkipRows()
    {
        var caller = await _platform.OwnerAsync("Pages");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        await _platform.GrantAsync(caller, workspace, caller.Member.Email, "WorkspaceOwner");
        caller = await _platform.ReloadAsync(caller);
        var list = await _platform.ListAsync(caller, workspace, "Tasks", TasksSchema);
        for (var index = 0; index < 5; index++)
        {
            await _platform.PutAsync(caller, workspace, list, "n" + index, Item("t", "open", 1, "2026-11-25T00:00:00Z", false, "[]", null), null);
        }

        var seen = new List<string>();
        string? continuation = null;
        for (var guard = 0; guard < 5; guard++)
        {
            var page = Ok(await _platform.Content.QueryItemsAsync(caller, workspace, list, "ids", default, default, default, [], 2, true, continuation, CancellationToken.None));
            seen.AddRange(page.Items.Select(item => item.Id));
            continuation = page.Continuation;
            if (continuation is null)
            {
                break;
            }
        }

        Assert.Equal(["n0", "n1", "n2", "n3", "n4"], seen.Order(StringComparer.Ordinal).ToArray());

        var emails = new List<string> { caller.Member.Email };
        for (var index = 0; index < 4; index++)
        {
            var email = "m" + index + "@example.com";
            Assert.True((await _platform.Members.AddAsync(caller, email, CancellationToken.None)).IsSuccess);
            await _platform.GrantAsync(caller, workspace, email, "WorkspaceRead");
            emails.Add(email);
        }

        var memberPages = new List<string>();
        string? memberToken = null;
        for (var guard = 0; guard < 5; guard++)
        {
            var page = Ok(await _platform.Members.ListWorkspaceMembersAsync(caller, workspace, 2, true, memberToken, CancellationToken.None));
            memberPages.AddRange(page.Members.Select(member => member.Email));
            memberToken = page.Continuation;
            if (memberToken is null)
            {
                break;
            }
        }

        Assert.Equal(emails.Order(StringComparer.OrdinalIgnoreCase).ToArray(), memberPages.ToArray());

        var reader = Ok(await _platform.Members.AddAsync(caller, "reader@example.com", CancellationToken.None));
        var visible = new List<string>();
        var hidden = await _platform.WorkspaceAsync(caller, "Hidden");
        for (var index = 0; index < 3; index++)
        {
            var id = await _platform.WorkspaceAsync(caller, "Seen" + index);
            await _platform.GrantAsync(caller, id, reader.Email, "WorkspaceRead");
            visible.Add(id);
        }

        var readerDoc = (await _platform.Store.FindByEmailAsync(caller.Tenant.Id, reader.Email, CancellationToken.None))!;
        var readerCaller = new Caller(readerDoc, caller.Tenant);
        var listed = new List<string>();
        string? workspaceToken = null;
        for (var guard = 0; guard < 5; guard++)
        {
            var page = Ok(await _platform.Workspaces.ListAsync(readerCaller, 2, true, workspaceToken, CancellationToken.None));
            listed.AddRange(page.Workspaces.Select(item => item.Id));
            workspaceToken = page.Continuation;
            if (workspaceToken is null)
            {
                break;
            }
        }

        Assert.Equal(visible.Order(StringComparer.Ordinal).ToArray(), listed.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(hidden, listed);
    }

    /// <summary>Removing a workspace role deletes the role entry. T-25.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-25")]
    public async Task RemoveRole_DeletesTheRoleEntry()
    {
        var caller = await _platform.OwnerAsync("Roles");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        var added = Ok(await _platform.Members.AddAsync(caller, "ann@example.com", CancellationToken.None));
        await _platform.GrantAsync(caller, workspace, added.Email, "WorkspaceWrite");
        var before = (await _platform.Store.GetAsync(caller.Tenant.Id, added.Id, CancellationToken.None))!;
        Assert.Equal(StoredRoles.Write, before.WorkspaceRoles[workspace]);
        Assert.True((await _platform.Members.RemoveFromWorkspaceAsync(caller, workspace, added.Email, CancellationToken.None)).IsSuccess);
        var after = (await _platform.Store.GetAsync(caller.Tenant.Id, added.Id, CancellationToken.None))!;
        Assert.False(after.WorkspaceRoles.ContainsKey(workspace));
    }

    /// <summary>Deleting a tenant removes its workspaces, lists, items, and the tenant. T-31.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-31")]
    public async Task DeleteTenant_RemovesChildrenOnCosmos()
    {
        var caller = await _platform.OwnerAsync("Gone");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        await _platform.GrantAsync(caller, workspace, caller.Member.Email, "WorkspaceOwner");
        caller = await _platform.ReloadAsync(caller);
        var list = await _platform.ListAsync(caller, workspace, "Tasks", TasksSchema);
        await _platform.PutAsync(caller, workspace, list, "item", Item("t", "open", 1, "2026-11-25T00:00:00Z", false, "[]", null), null);
        Assert.True((await _platform.Tenants.DeleteAsync(caller.Tenant.Id, CancellationToken.None)).IsSuccess);
        await _platform.Deletion.RunUntilQuietAsync(CancellationToken.None);
        Assert.Null(await _platform.Store.GetAsync(caller.Tenant.Id, CancellationToken.None));
        Assert.Null(await ((IWorkspaceRepository)_platform.Store).GetAsync(caller.Tenant.Id, workspace, CancellationToken.None));
        Assert.Null(await _platform.Store.GetAsync(caller.Tenant.Id, workspace, list, CancellationToken.None));
        Assert.Null(await _platform.Store.GetAsync(caller.Tenant.Id, workspace, list, "item", CancellationToken.None));
    }

    /// <summary>A deleted list's items are removed by the deletion job. T-55.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-55")]
    public async Task DeleteList_RemovesItsItems()
    {
        var caller = await _platform.OwnerAsync("Lists");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        await _platform.GrantAsync(caller, workspace, caller.Member.Email, "WorkspaceOwner");
        caller = await _platform.ReloadAsync(caller);
        var list = await _platform.ListAsync(caller, workspace, "Doomed", TasksSchema);
        await _platform.PutAsync(caller, workspace, list, "d1", Item("t", "open", 1, "2026-11-25T00:00:00Z", false, "[]", null), null);
        Assert.True((await _platform.Content.DeleteListAsync(caller, workspace, list, CancellationToken.None)).IsSuccess);
        var hidden = await _platform.Content.GetItemAsync(caller, workspace, list, "d1", CancellationToken.None);
        Assert.Equal(404, hidden.Error!.StatusCode);
        await _platform.Deletion.RunUntilQuietAsync(CancellationToken.None);
        Assert.Null(await _platform.Store.GetAsync(caller.Tenant.Id, workspace, list, "d1", CancellationToken.None));
        Assert.Null(await _platform.Store.GetAsync(caller.Tenant.Id, workspace, list, CancellationToken.None));
    }

    /// <summary>Items stored under an older schema stay out of filters and sorts on the new properties. T-60.</summary>
    [CosmosEmulatorFact]
    [Trait("T", "T-60")]
    public async Task OldItems_StayOutOfFiltersOnNewProperties()
    {
        var caller = await _platform.OwnerAsync("Old");
        var workspace = await _platform.WorkspaceAsync(caller, "Ops");
        caller = await _platform.ReloadAsync(caller);
        await _platform.GrantAsync(caller, workspace, caller.Member.Email, "WorkspaceOwner");
        caller = await _platform.ReloadAsync(caller);
        var list = await _platform.ListAsync(caller, workspace, "Loose", """{"properties":[{"name":"title","type":"String","required":true}]}""");
        await _platform.PutAsync(caller, workspace, list, "i5", "{\"title\":\"old\"}", null);
        Assert.True((await _platform.Content.UpdateSchemaAsync(caller, workspace, list, TasksSchema, CancellationToken.None)).IsSuccess);
        await _platform.PutAsync(caller, workspace, list, "i6", Item("Now", "open", 1, "2026-11-25T00:00:00Z", false, "[]", null), null);
        var filtered = await Ids(caller, workspace, list, where: """[{"path":"priority","op":"ge","value":0}]""");
        Assert.Equal(["i6"], filtered);
        var sorted = await Ids(caller, workspace, list, order: """{"path":"priority"}""");
        Assert.Equal(["i6"], sorted);
        var read = Ok(await _platform.Content.GetItemAsync(caller, workspace, list, "i5", CancellationToken.None));
        Assert.Equal(1, read.SchemaVersion);
        Assert.Equal("old", read.Data!.Value.GetProperty("title").GetString());
    }

    #region private

    private async Task<string[]> Ids(Caller caller, string workspace, string list, string? where = null, string? order = null, string? schemaVersion = null)
    {
        var page = Ok(await _platform.Content.QueryItemsAsync(
            caller,
            workspace,
            list,
            "ids",
            schemaVersion is null ? default : Node(schemaVersion),
            where is null ? default : Node(where),
            order is null ? default : Node(order),
            [],
            100,
            true,
            null,
            CancellationToken.None));
        return page.Items.Select(item => item.Id).ToArray();
    }

    private static JsonElement Node(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Item(string title, string status, int priority, string due, bool done, string tags, string? owner)
    {
        var ownerJson = owner is null ? "" : ",\"owner\":\"" + owner + "\"";
        return "{\"title\":\"" + title + "\",\"status\":\"" + status + "\",\"priority\":" + priority.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"due\":\"" + due + "\",\"done\":" + (done ? "true" : "false") + ",\"tags\":" + tags + ownerJson + ",\"labels\":[],\"address\":{\"city\":\"a\"}}";
    }

    private static T Ok<T>(OperationResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Text);
        return result.Value!;
    }

    #endregion
}

/// <summary>One Cosmos emulator database shared by <see cref="CosmosEmulatorTests"/>.</summary>
public sealed class CosmosPlatform : IAsyncLifetime
{
    private StepClock _clock = new();

    /// <summary>Gets the database. Null when the emulator is not configured.</summary>
    public CosmosDatabase? Database { get; private set; }

    /// <summary>Gets the database after the emulator has started.</summary>
    public CosmosDatabase Store
    {
        get
        {
            return Database ?? throw new InvalidOperationException("The Cosmos emulator is not configured.");
        }
    }

    /// <summary>Gets the tenants.</summary>
    public TenantService Tenants { get; private set; } = null!;

    /// <summary>Gets the members.</summary>
    public MemberService Members { get; private set; } = null!;

    /// <summary>Gets the workspaces.</summary>
    public WorkspaceService Workspaces { get; private set; } = null!;

    /// <summary>Gets the content.</summary>
    public ContentService Content { get; private set; } = null!;

    /// <summary>Gets the deletion job.</summary>
    public DeletionService Deletion { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AGENTCONNECT_COSMOS_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("AGENTCONNECT_COSMOS_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        }

        var options = new AgentConnectOptions
        {
            TokenEncryptionKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            McpServerUrl = "http://localhost/mcp",
            RestApiUrl = "http://localhost",
            DataProvider = "Cosmos",
            Cosmos = new CosmosOptions
            {
                Endpoint = endpoint,
                Database = "agentconnect-emulator",
            },
        };
        var database = new CosmosDatabase(options);
        await database.EnsureEmulatorContainersAsync(CancellationToken.None).ConfigureAwait(false);
        Database = database;
        var email = new QuietEmail();
        var audit = new QuietAudit();
        var tokens = new TokenService(options.TokenEncryptionKey);
        Members = new MemberService(database, database, tokens, email, audit, _clock, options, NullLogger<MemberService>.Instance);
        Tenants = new TenantService(database, database, tokens, email, audit, _clock, options, NullLogger<TenantService>.Instance);
        Workspaces = new WorkspaceService(database, Members, audit, _clock);
        Content = new ContentService(database, database, database, new SchemaGateway(new EasyJsonSchema.EasyJsonSchemaService()), audit, _clock);
        Deletion = new DeletionService(database, options, NullLogger<DeletionService>.Instance);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        Database?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Creates a tenant and returns its owner.</summary>
    /// <param name="name">The tenant name.</param>
    /// <returns>The owner.</returns>
    public async Task<Caller> OwnerAsync(string name)
    {
        var created = Ok(await Tenants.CreateAsync(name, name.ToLowerInvariant() + "@example.com", CancellationToken.None).ConfigureAwait(false));
        var tenant = (await Database!.GetAsync(created.Id, CancellationToken.None).ConfigureAwait(false))!;
        var member = (await Database.GetAsync(created.Id, created.FirstMemberId, CancellationToken.None).ConfigureAwait(false))!;
        return new Caller(member, tenant);
    }

    /// <summary>Re-reads a caller so later calls see the stored roles and etag.</summary>
    /// <param name="caller">The caller.</param>
    /// <returns>The fresh caller.</returns>
    public async Task<Caller> ReloadAsync(Caller caller)
    {
        var tenant = (await Database!.GetAsync(caller.Tenant.Id, CancellationToken.None).ConfigureAwait(false))!;
        var member = (await Database.GetAsync(caller.Tenant.Id, caller.Member.Id, CancellationToken.None).ConfigureAwait(false))!;
        return new Caller(member, tenant);
    }

    /// <summary>Creates a workspace.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="name">The name.</param>
    /// <returns>The workspace id.</returns>
    public async Task<string> WorkspaceAsync(Caller caller, string name)
    {
        return Ok(await Workspaces.CreateAsync(caller, name, null, false, [], CancellationToken.None).ConfigureAwait(false)).Id;
    }

    /// <summary>Grants a workspace role.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="role">The API role.</param>
    /// <returns>A task.</returns>
    public async Task GrantAsync(Caller caller, string workspaceId, string email, string role)
    {
        var result = await Members.AddToWorkspaceAsync(caller, workspaceId, email, role, CancellationToken.None).ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error?.Text);
    }

    /// <summary>Creates a list.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The name.</param>
    /// <param name="schema">The schema JSON.</param>
    /// <returns>The list id.</returns>
    public async Task<string> ListAsync(Caller caller, string workspaceId, string name, string schema)
    {
        return Ok(await Content.CreateListAsync(caller, workspaceId, name, null, false, schema, CancellationToken.None).ConfigureAwait(false)).Id;
    }

    /// <summary>Writes an item. When <paramref name="updated"/> is set, that timestamp is stored.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="json">The item JSON.</param>
    /// <param name="updated">The stored updated time, or null for the clock.</param>
    /// <returns>A task.</returns>
    public async Task PutAsync(Caller caller, string workspaceId, string listId, string itemId, string json, string? updated)
    {
        if (updated is not null)
        {
            _clock.UtcNow = DateTimeOffset.Parse(updated);
        }

        var result = await Content.UpsertItemAsync(caller, workspaceId, listId, itemId, json, CancellationToken.None).ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error?.Text);
    }

    private static T Ok<T>(OperationResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Text);
        return result.Value!;
    }

    private sealed class StepClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-23T09:00:00Z");
    }

    private sealed class QuietEmail : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class QuietAudit : IAuditRecorder
    {
        public void Record(AuditEntry entry)
        {
        }
    }
}

/// <summary>Shares one emulator platform and does not run its tests in parallel.</summary>
[CollectionDefinition("cosmos-emulator", DisableParallelization = true)]
public sealed class CosmosEmulatorCollection : ICollectionFixture<CosmosPlatform>
{
}
