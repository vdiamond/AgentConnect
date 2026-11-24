using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace AgentConnect;

/// <summary>
/// System.Text.Json serializer that writes the camelCase names in section 5.
/// </summary>
public sealed class CosmosJsonSerializer : CosmosSerializer
{
    /// <summary>The options used for Cosmos documents. Names are camelCase. Nulls are omitted so <c>_etag</c> is not written back.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <inheritdoc />
    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (typeof(T) == typeof(Stream))
            {
                return (T)(object)stream;
            }

            return JsonSerializer.Deserialize<T>(stream, Options)!;
        }
    }

    /// <inheritdoc />
    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, Options);
        stream.Position = 0;
        return stream;
    }

    #region private

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetDateTimeOffset();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        }
    }

    #endregion
}

/// <summary>
/// Cosmos DB implementation of the section 5 containers. The client uses managed identity.
/// </summary>
public sealed class CosmosDatabase : ITenantRepository, IMemberRepository, IWorkspaceRepository, IListRepository, IItemRepository, IDeletionStore, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosDatabase"/> class.
    /// </summary>
    /// <param name="options">The options. <see cref="CosmosOptions.Endpoint"/> is required.</param>
    public CosmosDatabase(AgentConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Cosmos.Endpoint))
        {
            throw new InvalidOperationException("AgentConnect:Cosmos:Endpoint is required when DataProvider is Cosmos.");
        }

        _options = options.Cosmos;
        var clientOptions = new CosmosClientOptions
        {
            Serializer = new CosmosJsonSerializer(),
            ConnectionMode = ConnectionMode.Gateway,
        };
        var emulatorKey = EmulatorKey(options.Cosmos.Endpoint);
        if (emulatorKey is not null)
        {
            clientOptions.LimitToEndpoint = true;
            clientOptions.HttpClientFactory = () =>
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                };
                return new HttpClient(handler);
            };
            _client = new CosmosClient(options.Cosmos.Endpoint, emulatorKey, clientOptions);
        }
        else
        {
            _client = new CosmosClient(options.Cosmos.Endpoint, new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned), clientOptions);
        }
    }

    /// <inheritdoc />
    public Task<TenantDocument?> GetAsync(string id, CancellationToken cancellationToken)
    {
        return ReadAsync<TenantDocument>(Tenants, id, new PartitionKey(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateAsync(TenantDocument tenant, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Tenants.CreateItemAsync(tenant, new PartitionKey(tenant.Id), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task ReplaceAsync(TenantDocument tenant, CancellationToken cancellationToken)
    {
        return ReplaceDocumentAsync(Tenants, tenant, tenant.Id, new PartitionKey(tenant.Id), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Tenants.DeleteItemAsync<TenantDocument>(id, new PartitionKey(id), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<MemberDocument?> GetAsync(string tenantId, string memberId, CancellationToken cancellationToken)
    {
        return ReadAsync<MemberDocument>(Members, memberId, new PartitionKey(tenantId), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemberDocument?> FindByEmailAsync(string tenantId, string email, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE STRINGEQUALS(c.email, @email, true)").WithParameter("@email", email);
        var page = await QueryAsync<MemberDocument>(Members, query, new PartitionKey(tenantId), 1, null, cancellationToken).ConfigureAwait(false);
        return page.Items.Count == 0 ? null : page.Items[0];
    }

    /// <inheritdoc />
    public Task CreateAsync(MemberDocument member, CancellationToken cancellationToken)
    {
        member.StampKey();
        return WriteAsync(() => Members.CreateItemAsync(member, new PartitionKey(member.TenantId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task ReplaceAsync(MemberDocument member, CancellationToken cancellationToken)
    {
        member.StampKey();
        return ReplaceDocumentAsync(Members, member, member.Id, new PartitionKey(member.TenantId), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string memberId, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Members.DeleteItemAsync<MemberDocument>(memberId, new PartitionKey(tenantId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<ResultPage<MemberDocument>> ListAsync(string tenantId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.created ASC, c.id ASC");
        return QueryAsync<MemberDocument>(Members, query, new PartitionKey(tenantId), pageSize, cursor, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ResultPage<MemberDocument>> ListByWorkspaceAsync(string tenantId, string workspaceId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE IS_DEFINED(c.workspaceRoles[\"" + QuoteId(workspaceId) + "\"]) ORDER BY c.email ASC, c.id ASC");
        return QueryAsync<MemberDocument>(Members, query, new PartitionKey(tenantId), pageSize, cursor, cancellationToken);
    }

    /// <inheritdoc />
    Task<WorkspaceDocument?> IWorkspaceRepository.GetAsync(string tenantId, string workspaceId, CancellationToken cancellationToken)
    {
        return ReadAsync<WorkspaceDocument>(Workspaces, workspaceId, new PartitionKey(tenantId), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateAsync(WorkspaceDocument workspace, CancellationToken cancellationToken)
    {
        workspace.StampKey();
        return WriteAsync(() => Workspaces.CreateItemAsync(workspace, new PartitionKey(workspace.TenantId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task ReplaceAsync(WorkspaceDocument workspace, CancellationToken cancellationToken)
    {
        workspace.StampKey();
        return ReplaceDocumentAsync(Workspaces, workspace, workspace.Id, new PartitionKey(workspace.TenantId), cancellationToken);
    }

    /// <inheritdoc />
    Task IWorkspaceRepository.DeleteAsync(string tenantId, string workspaceId, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Workspaces.DeleteItemAsync<WorkspaceDocument>(workspaceId, new PartitionKey(tenantId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<ResultPage<WorkspaceDocument>> ListVisibleAsync(string tenantId, IReadOnlyCollection<string>? visibleIds, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        if (visibleIds is not null && visibleIds.Count == 0)
        {
            return Task.FromResult(new ResultPage<WorkspaceDocument>());
        }

        var query = visibleIds is null
            ? new QueryDefinition("SELECT * FROM c WHERE (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt)) ORDER BY c.updated ASC, c.id ASC")
            : new QueryDefinition("SELECT * FROM c WHERE (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt)) AND ARRAY_CONTAINS(@ids, c.id) ORDER BY c.updated ASC, c.id ASC")
                .WithParameter("@ids", visibleIds.ToArray());
        return QueryAsync<WorkspaceDocument>(Workspaces, query, new PartitionKey(tenantId), pageSize, cursor, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(string tenantId, string name, string? exceptId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT VALUE c.id FROM c WHERE (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt)) AND STRINGEQUALS(c.name, @name, true)")
            .WithParameter("@name", name);
        var page = await QueryAsync<string>(Workspaces, query, new PartitionKey(tenantId), 20, null, cancellationToken).ConfigureAwait(false);
        return page.Items.Any(id => id != exceptId);
    }

    /// <inheritdoc />
    public Task<ListDocument?> GetAsync(string tenantId, string workspaceId, string listId, CancellationToken cancellationToken)
    {
        return ReadAsync<ListDocument>(Lists, listId, ListKey(tenantId, workspaceId), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateAsync(ListDocument list, CancellationToken cancellationToken)
    {
        list.StampKey();
        return WriteAsync(() => Lists.CreateItemAsync(list, ListKey(list.TenantId, list.WorkspaceId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task ReplaceAsync(ListDocument list, CancellationToken cancellationToken)
    {
        list.StampKey();
        return ReplaceDocumentAsync(Lists, list, list.Id, ListKey(list.TenantId, list.WorkspaceId), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string workspaceId, string listId, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Lists.DeleteItemAsync<ListDocument>(listId, ListKey(tenantId, workspaceId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<ResultPage<ListDocument>> ListAsync(string tenantId, string workspaceId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt)) ORDER BY c.updated ASC, c.id ASC");
        return QueryAsync<ListDocument>(Lists, query, ListKey(tenantId, workspaceId), pageSize, cursor, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(string tenantId, string workspaceId, string name, string? exceptId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT VALUE c.id FROM c WHERE (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt)) AND STRINGEQUALS(c.name, @name, true)")
            .WithParameter("@name", name);
        var page = await QueryAsync<string>(Lists, query, ListKey(tenantId, workspaceId), 20, null, cancellationToken).ConfigureAwait(false);
        return page.Items.Any(id => id != exceptId);
    }

    /// <inheritdoc />
    public Task<ItemDocument?> GetAsync(string tenantId, string workspaceId, string listId, string itemId, CancellationToken cancellationToken)
    {
        return ReadAsync<ItemDocument>(Items, itemId, ItemKey(tenantId, workspaceId, listId), cancellationToken);
    }

    /// <inheritdoc />
    public Task UpsertAsync(ItemDocument item, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Items.UpsertItemAsync(item, ItemKey(item.TenantId, item.WorkspaceId, item.ListId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string workspaceId, string listId, string itemId, CancellationToken cancellationToken)
    {
        return WriteAsync(() => Items.DeleteItemAsync<ItemDocument>(itemId, ItemKey(tenantId, workspaceId, listId), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<ResultPage<ItemDocument>> QueryAsync(CompiledItemQuery query, CancellationToken cancellationToken)
    {
        return QueryAsync<ItemDocument>(Items, CosmosQuerySql.Build(query), ItemKey(query.TenantId, query.WorkspaceId, query.ListId), query.PageSize, query.StoreCursor, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteNextBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize < 1)
        {
            return 0;
        }

        var removed = await DeleteItemsOfDoomedListsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            return removed;
        }

        removed = await DeleteEmptyDoomedListsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            return removed;
        }

        removed = await DeleteEmptyDoomedWorkspacesAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            return removed;
        }

        removed = await DeleteMembersOfDeletedTenantsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            return removed;
        }

        return await DeleteEmptyTenantsAsync(batchSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the section 5 containers when the emulator key is set. Production uses managed identity and does not call this.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EnsureEmulatorContainersAsync(CancellationToken cancellationToken)
    {
        if (EmulatorKey(_options.Endpoint) is null)
        {
            return;
        }

        var database = await _client.CreateDatabaseIfNotExistsAsync(_options.Database, cancellationToken: cancellationToken).ConfigureAwait(false);
        await CreateContainerAsync(database.Database, _options.TenantContainer, ["/id"], [], null, cancellationToken).ConfigureAwait(false);
        await CreateContainerAsync(database.Database, _options.MemberContainer, ["/tenantId"], ["/created", "/id"], "/emailKey", cancellationToken).ConfigureAwait(false);
        await AddCompositeAsync(database.Database, _options.MemberContainer, ["/email", "/id"], cancellationToken).ConfigureAwait(false);
        await CreateContainerAsync(database.Database, _options.WorkspaceContainer, ["/tenantId"], ["/updated", "/id"], "/nameKey", cancellationToken).ConfigureAwait(false);
        await CreateContainerAsync(database.Database, _options.ListContainer, ["/tenantId", "/workspaceId"], ["/updated", "/id"], "/nameKey", cancellationToken).ConfigureAwait(false);
        await CreateContainerAsync(database.Database, _options.ItemContainer, ["/tenantId", "/workspaceId", "/listId"], ["/updated", "/id"], null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
    }

    #region private

    private readonly CosmosClient _client;
    private readonly CosmosOptions _options;

    private Container Tenants => _client.GetDatabase(_options.Database).GetContainer(_options.TenantContainer);

    private Container Members => _client.GetDatabase(_options.Database).GetContainer(_options.MemberContainer);

    private Container Workspaces => _client.GetDatabase(_options.Database).GetContainer(_options.WorkspaceContainer);

    private Container Lists => _client.GetDatabase(_options.Database).GetContainer(_options.ListContainer);

    private Container Items => _client.GetDatabase(_options.Database).GetContainer(_options.ItemContainer);

    private static PartitionKey ListKey(string tenantId, string workspaceId)
    {
        return new PartitionKeyBuilder().Add(tenantId).Add(workspaceId).Build();
    }

    private static PartitionKey ItemKey(string tenantId, string workspaceId, string listId)
    {
        return new PartitionKeyBuilder().Add(tenantId).Add(workspaceId).Add(listId).Build();
    }

    private static async Task<T?> ReadAsync<T>(Container container, string id, PartitionKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.Resource is IConcurrencyDocument document)
            {
                document.ETag = response.ETag;
            }

            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
        catch (CosmosException exception)
        {
            throw Map(exception);
        }
    }

    private static async Task WriteAsync(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (CosmosException exception)
        {
            throw Map(exception);
        }
    }

    private static async Task ReplaceDocumentAsync<T>(Container container, T document, string id, PartitionKey key, CancellationToken cancellationToken)
        where T : IConcurrencyDocument
    {
        var match = document.ETag;
        document.ETag = null;
        try
        {
            var request = new ItemRequestOptions();
            if (!string.IsNullOrEmpty(match))
            {
                request.IfMatchEtag = match;
            }

            var response = await container.ReplaceItemAsync(document, id, key, request, cancellationToken).ConfigureAwait(false);
            document.ETag = response.ETag;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            document.ETag = match;
            throw new StorageConflictException();
        }
        catch (CosmosException exception)
        {
            document.ETag = match;
            throw Map(exception);
        }
    }

    private static Exception Map(CosmosException exception)
    {
        var mapped = CosmosErrors.MapWrite(exception);
        return ReferenceEquals(mapped, exception) ? exception : mapped;
    }

    private static async Task<ResultPage<T>> QueryAsync<T>(Container container, QueryDefinition query, PartitionKey key, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            var options = new QueryRequestOptions
            {
                PartitionKey = key,
                MaxItemCount = pageSize,
            };
            using var iterator = container.GetItemQueryIterator<T>(query, cursor, options);
            if (!iterator.HasMoreResults)
            {
                return new ResultPage<T>();
            }

            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            return new ResultPage<T>
            {
                Items = page.ToList(),
                Continuation = page.ContinuationToken,
            };
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.BadRequest && cursor is not null)
        {
            throw new InvalidContinuationException();
        }
        catch (CosmosException exception)
        {
            throw Map(exception);
        }
    }

    private static string? EmulatorKey(string endpoint)
    {
        var key = Environment.GetEnvironmentVariable("AGENTCONNECT_COSMOS_KEY");
        if (string.IsNullOrEmpty(key) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            return null;
        }

        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(environment) || string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return key;
    }

    private static async Task CreateContainerAsync(Database database, string name, string[] partitionKey, string[] composite, string? uniquePath, CancellationToken cancellationToken)
    {
        var properties = new ContainerProperties
        {
            Id = name,
        };
        if (partitionKey.Length == 1)
        {
            properties.PartitionKeyPath = partitionKey[0];
        }
        else
        {
            properties.PartitionKeyPaths = partitionKey;
        }

        if (composite.Length >= 2)
        {
            var index = new Collection<CompositePath>();
            foreach (var path in composite)
            {
                index.Add(new CompositePath
                {
                    Path = path,
                    Order = CompositePathSortOrder.Ascending,
                });
            }

            properties.IndexingPolicy.CompositeIndexes.Add(index);
        }

        if (uniquePath is not null)
        {
            var unique = new UniqueKey();
            unique.Paths.Add(uniquePath);
            properties.UniqueKeyPolicy = new UniqueKeyPolicy();
            properties.UniqueKeyPolicy.UniqueKeys.Add(unique);
        }

        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddCompositeAsync(Database database, string name, string[] composite, CancellationToken cancellationToken)
    {
        var response = await database.GetContainer(name).ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var properties = response.Resource;
        foreach (var existing in properties.IndexingPolicy.CompositeIndexes)
        {
            if (existing.Count > 0 && existing[0].Path == composite[0])
            {
                return;
            }
        }

        var index = new Collection<CompositePath>();
        foreach (var path in composite)
        {
            index.Add(new CompositePath
            {
                Path = path,
                Order = CompositePathSortOrder.Ascending,
            });
        }

        properties.IndexingPolicy.CompositeIndexes.Add(index);
        await database.GetContainer(name).ReplaceContainerAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteId(string id)
    {
        foreach (var character in id)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
            {
                throw new ArgumentException("The id cannot be used in a query path.", nameof(id));
            }
        }

        return id;
    }

    private async Task<int> DeleteItemsOfDoomedListsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var removed = 0;
        await foreach (var list in DoomedListsAsync(cancellationToken).ConfigureAwait(false))
        {
            var page = await QueryAsync<ItemDocument>(Items, new QueryDefinition("SELECT c.id FROM c"), ItemKey(list.TenantId, list.WorkspaceId, list.Id), batchSize - removed, null, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                await DeleteIgnoringMissingAsync(() => Items.DeleteItemAsync<ItemDocument>(item.Id, ItemKey(list.TenantId, list.WorkspaceId, list.Id), cancellationToken: cancellationToken)).ConfigureAwait(false);
                removed++;
                if (removed >= batchSize)
                {
                    return removed;
                }
            }
        }

        return removed;
    }

    private async Task<int> DeleteEmptyDoomedListsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var removed = 0;
        await foreach (var list in DoomedListsAsync(cancellationToken).ConfigureAwait(false))
        {
            var page = await QueryAsync<ItemDocument>(Items, new QueryDefinition("SELECT c.id FROM c"), ItemKey(list.TenantId, list.WorkspaceId, list.Id), 1, null, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count > 0)
            {
                continue;
            }

            await DeleteIgnoringMissingAsync(() => Lists.DeleteItemAsync<ListDocument>(list.Id, ListKey(list.TenantId, list.WorkspaceId), cancellationToken: cancellationToken)).ConfigureAwait(false);
            removed++;
            if (removed >= batchSize)
            {
                return removed;
            }
        }

        return removed;
    }

    private async Task<int> DeleteEmptyDoomedWorkspacesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var removed = 0;
        await foreach (var workspace in DoomedWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            var lists = await QueryAsync<ListDocument>(Lists, new QueryDefinition("SELECT c.id FROM c"), ListKey(workspace.TenantId, workspace.Id), 1, null, cancellationToken).ConfigureAwait(false);
            if (lists.Items.Count > 0)
            {
                continue;
            }

            await StripRolesAsync(workspace.TenantId, workspace.Id, cancellationToken).ConfigureAwait(false);
            await DeleteIgnoringMissingAsync(() => Workspaces.DeleteItemAsync<WorkspaceDocument>(workspace.Id, new PartitionKey(workspace.TenantId), cancellationToken: cancellationToken)).ConfigureAwait(false);
            removed++;
            if (removed >= batchSize)
            {
                return removed;
            }
        }

        return removed;
    }

    private async Task StripRolesAsync(string tenantId, string workspaceId, CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await QueryAsync<MemberDocument>(Members, new QueryDefinition("SELECT * FROM c"), new PartitionKey(tenantId), 100, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var member in page.Items)
            {
                if (member.WorkspaceRoles.Remove(workspaceId))
                {
                    await ReplaceDocumentAsync(Members, member, member.Id, new PartitionKey(tenantId), cancellationToken).ConfigureAwait(false);
                }
            }

            cursor = page.Continuation;
        }
        while (cursor is not null);
    }

    private async Task<int> DeleteMembersOfDeletedTenantsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var removed = 0;
        await foreach (var tenant in DeletedTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            var page = await QueryAsync<MemberDocument>(Members, new QueryDefinition("SELECT c.id FROM c"), new PartitionKey(tenant.Id), batchSize - removed, null, cancellationToken).ConfigureAwait(false);
            foreach (var member in page.Items)
            {
                await Members.DeleteItemAsync<MemberDocument>(member.Id, new PartitionKey(tenant.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                removed++;
                if (removed >= batchSize)
                {
                    return removed;
                }
            }
        }

        return removed;
    }

    private async Task<int> DeleteEmptyTenantsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var removed = 0;
        await foreach (var tenant in DeletedTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            var members = await QueryAsync<MemberDocument>(Members, new QueryDefinition("SELECT c.id FROM c"), new PartitionKey(tenant.Id), 1, null, cancellationToken).ConfigureAwait(false);
            var workspaces = await QueryAsync<WorkspaceDocument>(Workspaces, new QueryDefinition("SELECT c.id FROM c"), new PartitionKey(tenant.Id), 1, null, cancellationToken).ConfigureAwait(false);
            if (members.Items.Count > 0 || workspaces.Items.Count > 0)
            {
                continue;
            }

            await Tenants.DeleteItemAsync<TenantDocument>(tenant.Id, new PartitionKey(tenant.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
            removed++;
            if (removed >= batchSize)
            {
                return removed;
            }
        }

        return removed;
    }

    private async IAsyncEnumerable<ListDocument> DoomedListsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var marked = new QueryDefinition("SELECT * FROM c WHERE IS_DEFINED(c.deletedAt) AND NOT IS_NULL(c.deletedAt)");
        using (var lists = Lists.GetItemQueryIterator<ListDocument>(marked))
        {
            while (lists.HasMoreResults)
            {
                var page = await lists.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var list in page)
                {
                    yield return list;
                }
            }
        }

        await foreach (var workspace in DoomedWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            string? cursor = null;
            do
            {
                var children = await QueryAsync<ListDocument>(Lists, new QueryDefinition("SELECT * FROM c"), ListKey(workspace.TenantId, workspace.Id), 100, cursor, cancellationToken).ConfigureAwait(false);
                foreach (var list in children.Items)
                {
                    yield return list;
                }

                cursor = children.Continuation;
            }
            while (cursor is not null);
        }
    }

    private async IAsyncEnumerable<WorkspaceDocument> DoomedWorkspacesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var marked = new QueryDefinition("SELECT * FROM c WHERE IS_DEFINED(c.deletedAt) AND NOT IS_NULL(c.deletedAt)");
        using (var workspaces = Workspaces.GetItemQueryIterator<WorkspaceDocument>(marked))
        {
            while (workspaces.HasMoreResults)
            {
                var page = await workspaces.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var workspace in page)
                {
                    yield return workspace;
                }
            }
        }

        await foreach (var tenant in DeletedTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            string? cursor = null;
            do
            {
                var children = await QueryAsync<WorkspaceDocument>(Workspaces, new QueryDefinition("SELECT * FROM c"), new PartitionKey(tenant.Id), 100, cursor, cancellationToken).ConfigureAwait(false);
                foreach (var workspace in children.Items)
                {
                    yield return workspace;
                }

                cursor = children.Continuation;
            }
            while (cursor is not null);
        }
    }

    private static async Task DeleteIgnoringMissingAsync(Func<Task> delete)
    {
        try
        {
            await delete().ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private async IAsyncEnumerable<TenantDocument> DeletedTenantsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE IS_DEFINED(c.deletedAt) AND NOT IS_NULL(c.deletedAt)");
        using var iterator = Tenants.GetItemQueryIterator<TenantDocument>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var tenant in page)
            {
                yield return tenant;
            }
        }
    }

    #endregion
}
