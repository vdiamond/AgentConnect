using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentConnect;

/// <summary>
/// A document replaced with <c>If-Match</c> so a stale snapshot cannot overwrite a newer write.
/// </summary>
public interface IConcurrencyDocument
{
    /// <summary>Gets or sets the concurrency token. Null until the document is stored.</summary>
    string? ETag { get; set; }
}

/// <summary>
/// A Tenant document. Partition key is <c>id</c>.
/// </summary>
public sealed class TenantDocument : IConcurrencyDocument
{
    /// <summary>Gets or sets the tenant id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the trimmed tenant name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets <c>Active</c> or <c>Suspended</c>.</summary>
    public string Status { get; set; } = TenantStatuses.Active;

    /// <summary>Gets or sets the default page size.</summary>
    public int PageSizeDefault { get; set; } = PageRules.DefaultPageSize;

    /// <summary>Gets or sets the maximum page size.</summary>
    public int PageSizeMax { get; set; } = PageRules.DefaultPageSizeMax;

    /// <summary>Gets or sets the member created by FR-1.</summary>
    public string FirstMemberId { get; set; } = "";

    /// <summary>Gets or sets when the tenant was created, in UTC.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Gets or sets when the tenant was marked deleted. Null unless deleted.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Gets or sets subscription info. Billing is out of scope; the field is stored as JSON null until its shape is defined.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Subscription { get; set; }

    /// <inheritdoc />
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// A Member document. Partition key is <c>tenantId</c>.
/// </summary>
public sealed class MemberDocument : IConcurrencyDocument
{
    /// <summary>Gets or sets the member id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the tenant id.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Gets or sets the trimmed email. Uniqueness is case-insensitive.</summary>
    public string Email { get; set; } = "";

    /// <summary>Gets or sets the lowercase email used as the unique key inside a tenant.</summary>
    public string EmailLower { get; set; } = "";

    /// <summary>Gets or sets the secure hash of the token secret.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>Gets or sets the encrypted token, used to resend it. Never the plain token.</summary>
    public string TokenEncrypted { get; set; } = "";

    /// <summary>Gets or sets a value indicating whether the member is a TenantOwner.</summary>
    public bool IsTenantOwner { get; set; }

    /// <summary>Gets or sets workspace id to <c>Owner</c>, <c>Write</c>, or <c>Read</c>.</summary>
    public Dictionary<string, string> WorkspaceRoles { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether an invitation email was sent successfully.</summary>
    public bool InvitationSent { get; set; }

    /// <summary>Gets or sets when the member was created, in UTC.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Gets or sets when the current token was issued, in UTC.</summary>
    public DateTimeOffset LastTokenIssued { get; set; }

    /// <summary>Gets or sets when an email last contained the token, in UTC.</summary>
    public DateTimeOffset? LastTokenSent { get; set; }

    /// <summary>Sets <see cref="EmailLower"/> from <see cref="Email"/>.</summary>
    public void StampKey()
    {
        EmailLower = Email.ToLowerInvariant();
    }

    /// <inheritdoc />
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// A Workspace document. Partition key is <c>tenantId</c>.
/// </summary>
public sealed class WorkspaceDocument : IConcurrencyDocument
{
    /// <summary>Gets or sets the workspace id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the tenant id.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Gets or sets the trimmed name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the lowercase name used as the unique key. A deleted workspace keeps a private key so the name can be reused.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>Gets or sets the trimmed description, or null when none is set.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets when the workspace was created, in UTC.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Gets or sets when the workspace was last updated, in UTC.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <summary>Gets or sets when the workspace was marked deleted. Null unless deleted.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Sets <see cref="NameKey"/> from the live name, or to a private key once the workspace is deleted.</summary>
    public void StampKey()
    {
        NameKey = DeletedAt is null ? Name.ToLowerInvariant() : "\u0000" + Id;
    }

    /// <inheritdoc />
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// A List document. Hierarchical partition key is <c>tenantId</c>, <c>workspaceId</c>.
/// </summary>
public sealed class ListDocument : IConcurrencyDocument
{
    /// <summary>Gets or sets the list id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the tenant id.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Gets or sets the workspace id.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>Gets or sets the trimmed name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the lowercase name used as the unique key. A deleted list keeps a private key so the name can be reused.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>Gets or sets the trimmed description, or null when none is set.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the current EasyJsonSchema document as JSON text.</summary>
    public string Schema { get; set; } = "";

    /// <summary>Gets or sets the current schema version. Starts at 1.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets when the list was created, in UTC.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Gets or sets when the list was last updated, in UTC.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <summary>Gets or sets when the list was marked deleted. Null unless deleted.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Sets <see cref="NameKey"/> from the live name, or to a private key once the list is deleted.</summary>
    public void StampKey()
    {
        NameKey = DeletedAt is null ? Name.ToLowerInvariant() : "\u0000" + Id;
    }

    /// <inheritdoc />
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// An Item document. Hierarchical partition key is <c>tenantId</c>, <c>workspaceId</c>, <c>listId</c>.
/// </summary>
public sealed class ItemDocument : IConcurrencyDocument
{
    /// <summary>Gets or sets the item id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the tenant id.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Gets or sets the workspace id.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>Gets or sets the list id.</summary>
    public string ListId { get; set; } = "";

    /// <summary>Gets or sets the item JSON object. Stored as an object, not a string, so Cosmos can filter it.</summary>
    public JsonElement Data { get; set; }

    /// <summary>
    /// Parses item JSON into a <see cref="JsonElement"/> that outlives the source document.
    /// </summary>
    /// <param name="json">The item JSON object.</param>
    /// <returns>The element.</returns>
    public static JsonElement ParseData(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Gets or sets the schema version the data was validated against.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets when the item was created, in UTC.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Gets or sets when the item was last written, in UTC.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <inheritdoc />
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// Tenant status values.
/// </summary>
public static class TenantStatuses
{
    /// <summary>The tenant accepts calls.</summary>
    public const string Active = "Active";

    /// <summary>The tenant keeps its data and rejects member calls.</summary>
    public const string Suspended = "Suspended";
}

/// <summary>
/// Stored workspace role codes. API names are <c>WorkspaceOwner</c>, <c>WorkspaceWrite</c>, and <c>WorkspaceRead</c>.
/// </summary>
public static class StoredRoles
{
    /// <summary>WorkspaceOwner.</summary>
    public const string Owner = "Owner";

    /// <summary>WorkspaceWrite.</summary>
    public const string Write = "Write";

    /// <summary>WorkspaceRead.</summary>
    public const string Read = "Read";

    /// <summary>
    /// Parses an API role name.
    /// </summary>
    /// <param name="text">The role text.</param>
    /// <param name="stored">The stored code when the text is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a known role.</returns>
    public static bool TryParse(string? text, out string stored)
    {
        switch (text)
        {
            case "WorkspaceOwner":
            {
                stored = Owner;
                return true;
            }
            case "WorkspaceWrite":
            {
                stored = Write;
                return true;
            }
            case "WorkspaceRead":
            {
                stored = Read;
                return true;
            }
            default:
            {
                stored = "";
                return false;
            }
        }
    }

    /// <summary>
    /// Returns the API name for a stored role code.
    /// </summary>
    /// <param name="stored">The stored code.</param>
    /// <returns>The API name.</returns>
    public static string ToApi(string stored)
    {
        return stored switch
        {
            Owner => "WorkspaceOwner",
            Write => "WorkspaceWrite",
            Read => "WorkspaceRead",
            _ => stored,
        };
    }
}
