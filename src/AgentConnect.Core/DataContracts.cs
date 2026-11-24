namespace AgentConnect;

/// <summary>
/// One page of results and the continuation token when another page exists.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class ResultPage<T>
{
    /// <summary>Gets or sets the page.</summary>
    public IReadOnlyList<T> Items { get; set; } = [];

    /// <summary>Gets or sets the continuation token, or null when this is the last page.</summary>
    public string? Continuation { get; set; }
}

/// <summary>
/// A clock so tests can control timestamps.
/// </summary>
public interface IClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The system clock.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow
    {
        get
        {
            return DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>
/// Cosmos DB returned 408 or 503. The caller can retry.
/// </summary>
public sealed class StorageUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StorageUnavailableException"/> class.
    /// </summary>
    /// <param name="retryAfter">The Retry-After value, or one second when Cosmos DB did not send one.</param>
    public StorageUnavailableException(TimeSpan retryAfter)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the Retry-After value.</summary>
    public TimeSpan RetryAfter { get; }
}

/// <summary>
/// A document disappeared while it was being updated.
/// </summary>
public sealed class DocumentMissingException : Exception
{
}

/// <summary>
/// Cosmos DB rejected the request because the caller is over the provisioned throughput.
/// </summary>
public sealed class StorageThrottleException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StorageThrottleException"/> class.
    /// </summary>
    /// <param name="retryAfter">The Retry-After value from Cosmos DB.</param>
    public StorageThrottleException(TimeSpan retryAfter)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the Retry-After value.</summary>
    public TimeSpan RetryAfter { get; }
}

/// <summary>
/// The List partition has reached the 20 GB logical-partition limit.
/// </summary>
public sealed class StorageLimitException : Exception
{
}

/// <summary>
/// The Item document is larger than the Cosmos DB document limit.
/// </summary>
public sealed class StorageTooLargeException : Exception
{
}

/// <summary>
/// A continuation token is malformed for the store that issued it.
/// </summary>
public sealed class InvalidContinuationException : Exception
{
}

/// <summary>
/// A replace lost a race with another write. The caller retries against the current document.
/// </summary>
public sealed class StorageConflictException : Exception
{
}

/// <summary>
/// A unique email or name is already stored. The create or rename lost the race.
/// </summary>
public sealed class DuplicateValueException : Exception
{
}

/// <summary>
/// Cosmos DB failed for a reason that is not throttling, a size limit, or a conflict.
/// The message is a fixed text and does not include the Cosmos error.
/// </summary>
public sealed class StorageFailureException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StorageFailureException"/> class.
    /// </summary>
    /// <param name="statusCode">The Cosmos HTTP status code.</param>
    /// <param name="subStatusCode">The Cosmos substatus code.</param>
    /// <param name="activityId">The Cosmos activity id.</param>
    public StorageFailureException(int statusCode, int subStatusCode, string? activityId)
        : base("$: internal error")
    {
        StatusCode = statusCode;
        SubStatusCode = subStatusCode;
        ActivityId = activityId ?? "";
    }

    /// <summary>Gets the Cosmos HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the Cosmos substatus code.</summary>
    public int SubStatusCode { get; }

    /// <summary>Gets the Cosmos activity id.</summary>
    public string ActivityId { get; }
}

/// <summary>
/// Reads and writes Tenant documents.
/// </summary>
public interface ITenantRepository
{
    /// <summary>Gets a tenant by id, including a deleted tenant.</summary>
    /// <param name="id">The tenant id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant, or null.</returns>
    Task<TenantDocument?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Inserts a tenant.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task CreateAsync(TenantDocument tenant, CancellationToken cancellationToken);

    /// <summary>Replaces a tenant.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReplaceAsync(TenantDocument tenant, CancellationToken cancellationToken);

    /// <summary>Deletes a tenant document.</summary>
    /// <param name="id">The tenant id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes Member documents.
/// </summary>
public interface IMemberRepository
{
    /// <summary>Point-reads a member.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="memberId">The member id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The member, or null.</returns>
    Task<MemberDocument?> GetAsync(string tenantId, string memberId, CancellationToken cancellationToken);

    /// <summary>Finds a member by email, compared case-insensitively.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="email">The trimmed email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The member, or null.</returns>
    Task<MemberDocument?> FindByEmailAsync(string tenantId, string email, CancellationToken cancellationToken);

    /// <summary>Inserts a member.</summary>
    /// <param name="member">The member.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task CreateAsync(MemberDocument member, CancellationToken cancellationToken);

    /// <summary>Replaces a member.</summary>
    /// <param name="member">The member.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReplaceAsync(MemberDocument member, CancellationToken cancellationToken);

    /// <summary>Deletes a member.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="memberId">The member id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string tenantId, string memberId, CancellationToken cancellationToken);

    /// <summary>Lists members of a tenant, ordered by <c>created</c> then <c>id</c>.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cursor">The store cursor, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<ResultPage<MemberDocument>> ListAsync(string tenantId, int pageSize, string? cursor, CancellationToken cancellationToken);

    /// <summary>Lists members of one workspace, ordered by email then id.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cursor">The store cursor, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<ResultPage<MemberDocument>> ListByWorkspaceAsync(string tenantId, string workspaceId, int pageSize, string? cursor, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes Workspace documents.
/// </summary>
public interface IWorkspaceRepository
{
    /// <summary>Gets a workspace, including a deleted one.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workspace, or null.</returns>
    Task<WorkspaceDocument?> GetAsync(string tenantId, string workspaceId, CancellationToken cancellationToken);

    /// <summary>Inserts a workspace.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task CreateAsync(WorkspaceDocument workspace, CancellationToken cancellationToken);

    /// <summary>Replaces a workspace.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReplaceAsync(WorkspaceDocument workspace, CancellationToken cancellationToken);

    /// <summary>Deletes a workspace document.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string tenantId, string workspaceId, CancellationToken cancellationToken);

    /// <summary>Lists workspaces the caller can see, ordered by <c>updated</c> then <c>id</c>.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="visibleIds">Null when the caller sees every workspace; otherwise the ids they can see.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cursor">The store cursor, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<ResultPage<WorkspaceDocument>> ListVisibleAsync(string tenantId, IReadOnlyCollection<string>? visibleIds, int pageSize, string? cursor, CancellationToken cancellationToken);

    /// <summary>Returns true when another live workspace in the tenant has this name.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="name">The trimmed name.</param>
    /// <param name="exceptId">A workspace id to ignore, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the name is taken.</returns>
    Task<bool> NameExistsAsync(string tenantId, string name, string? exceptId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes List documents.
/// </summary>
public interface IListRepository
{
    /// <summary>Gets a list, including a deleted one.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list, or null.</returns>
    Task<ListDocument?> GetAsync(string tenantId, string workspaceId, string listId, CancellationToken cancellationToken);

    /// <summary>Inserts a list.</summary>
    /// <param name="list">The list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task CreateAsync(ListDocument list, CancellationToken cancellationToken);

    /// <summary>Replaces a list. The caller has already captured the schema version it validated against.</summary>
    /// <param name="list">The list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReplaceAsync(ListDocument list, CancellationToken cancellationToken);

    /// <summary>Deletes a list document.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string tenantId, string workspaceId, string listId, CancellationToken cancellationToken);

    /// <summary>Lists live lists in a workspace, ordered by <c>updated</c> then <c>id</c>.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cursor">The store cursor, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<ResultPage<ListDocument>> ListAsync(string tenantId, string workspaceId, int pageSize, string? cursor, CancellationToken cancellationToken);

    /// <summary>Returns true when another live list in the workspace has this name.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The trimmed name.</param>
    /// <param name="exceptId">A list id to ignore, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the name is taken.</returns>
    Task<bool> NameExistsAsync(string tenantId, string workspaceId, string name, string? exceptId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes Item documents inside one List partition.
/// </summary>
public interface IItemRepository
{
    /// <summary>Gets an item.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item, or null.</returns>
    Task<ItemDocument?> GetAsync(string tenantId, string workspaceId, string listId, string itemId, CancellationToken cancellationToken);

    /// <summary>Creates or replaces an item. The last write wins.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task UpsertAsync(ItemDocument item, CancellationToken cancellationToken);

    /// <summary>Deletes an item.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string tenantId, string workspaceId, string listId, string itemId, CancellationToken cancellationToken);

    /// <summary>Runs a validated item query inside one list partition.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<ResultPage<ItemDocument>> QueryAsync(CompiledItemQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Removes documents marked deleted, in batches, and resumes after an interruption.
/// </summary>
public interface IDeletionStore
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> child documents, then empty marked parents.
    /// </summary>
    /// <param name="batchSize">The maximum number of documents to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many documents were removed. Zero means nothing remains.</returns>
    Task<int> DeleteNextBatchAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Host settings. Secrets come from the environment, never from a committed value.
/// </summary>
public sealed class AgentConnectOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "AgentConnect";

    /// <summary>Gets or sets the platform admin key.</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Gets or sets the base64 token encryption key (32 bytes).</summary>
    public string TokenEncryptionKey { get; set; } = "";

    /// <summary>Gets or sets the MCP Server URL included in emails.</summary>
    public string McpServerUrl { get; set; } = "";

    /// <summary>Gets or sets the REST API URL included in emails.</summary>
    public string RestApiUrl { get; set; } = "";

    /// <summary>Gets or sets <c>Memory</c> or <c>Cosmos</c>.</summary>
    public string DataProvider { get; set; } = "Cosmos";

    /// <summary>Gets or sets how many documents one deletion batch removes.</summary>
    public int DeletionBatchSize { get; set; } = 100;

    /// <summary>Gets or sets how many times a stale Tenant, Member, Workspace or List write is retried. FR-53.</summary>
    public int ConcurrencyRetries { get; set; } = 3;

    /// <summary>Gets or sets email settings.</summary>
    public EmailOptions Email { get; set; } = new();

    /// <summary>Gets or sets Cosmos DB settings.</summary>
    public CosmosOptions Cosmos { get; set; } = new();

    /// <summary>Gets or sets Application Insights settings.</summary>
    public InsightsOptions ApplicationInsights { get; set; } = new();
}

/// <summary>
/// Azure Communication Services Email settings.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>Gets or sets the ACS endpoint. Empty disables live sending.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Gets or sets the sender address on the verified domain.</summary>
    public string SenderAddress { get; set; } = "DoNotReply@azure4smb.com";
}

/// <summary>
/// Cosmos DB settings. The app uses managed identity; account keys are not accepted.
/// </summary>
public sealed class CosmosOptions
{
    /// <summary>Gets or sets the account endpoint.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Gets or sets the database name.</summary>
    public string Database { get; set; } = "";

    /// <summary>Gets or sets the Tenants container name.</summary>
    public string TenantContainer { get; set; } = "Tenants";

    /// <summary>Gets or sets the Members container name.</summary>
    public string MemberContainer { get; set; } = "Members";

    /// <summary>Gets or sets the Workspaces container name.</summary>
    public string WorkspaceContainer { get; set; } = "Workspaces";

    /// <summary>Gets or sets the Lists container name.</summary>
    public string ListContainer { get; set; } = "Lists";

    /// <summary>Gets or sets the Items container name.</summary>
    public string ItemContainer { get; set; } = "Items";
}

/// <summary>
/// Application Insights settings.
/// </summary>
public sealed class InsightsOptions
{
    /// <summary>Gets or sets the connection string. Empty keeps audit events off the wire.</summary>
    public string ConnectionString { get; set; } = "";
}
