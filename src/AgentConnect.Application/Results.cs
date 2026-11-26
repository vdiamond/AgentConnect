using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentConnect;

/// <summary>The result of creating a tenant.</summary>
/// <param name="Id">The tenant id.</param>
/// <param name="Name">The tenant name.</param>
/// <param name="Status">The status.</param>
/// <param name="PageSizeDefault">The default page size.</param>
/// <param name="PageSizeMax">The maximum page size.</param>
/// <param name="FirstMemberId">The first member id.</param>
/// <param name="Token">The first member's plaintext token, returned once.</param>
/// <param name="EmailSent">Whether the invitation email was sent.</param>
public sealed record TenantCreatedResult(string Id, string Name, string Status, int PageSizeDefault, int PageSizeMax, string FirstMemberId, string Token, bool EmailSent);

/// <summary>Tenant page sizes.</summary>
/// <param name="PageSizeDefault">The default page size.</param>
/// <param name="PageSizeMax">The maximum page size.</param>
public sealed record TenantSettingsResult(int PageSizeDefault, int PageSizeMax);

/// <summary>A call whose only extra result is whether email was sent.</summary>
/// <param name="EmailSent">Whether the email was sent.</param>
public sealed record EmailSentResult(bool EmailSent);

/// <summary>A member, without token material.</summary>
/// <param name="Id">The member id.</param>
/// <param name="Email">The email.</param>
/// <param name="IsTenantOwner">Whether the member is a TenantOwner.</param>
/// <param name="InvitationSent">Whether an invitation has been sent.</param>
/// <param name="Token">The plaintext token, returned once on create. Omitted on every later read.</param>
public sealed record MemberResult(string Id, string Email, bool IsTenantOwner, bool InvitationSent, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token = null);

/// <summary>One page of members.</summary>
/// <param name="Members">The members.</param>
/// <param name="Continuation">The continuation token, or null.</param>
/// <param name="RowStart">The 1-based row of the first entry. MCP only.</param>
public sealed record MemberPageResult(IReadOnlyList<MemberResult> Members, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Continuation, [property: JsonIgnore] int RowStart = 1);

/// <summary>A workspace the caller can see.</summary>
/// <param name="Id">The workspace id.</param>
/// <param name="Name">The name.</param>
    /// <param name="Description">The description, if set.</param>
    /// <param name="Role">The caller's API role, or null.</param>
public sealed record WorkspaceView(string Id, string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description, string? Role);

/// <summary>One page of workspaces.</summary>
/// <param name="Workspaces">The workspaces.</param>
/// <param name="Continuation">The continuation token, or null.</param>
/// <param name="RowStart">The 1-based row of the first entry. MCP only.</param>
public sealed record WorkspacePageResult(IReadOnlyList<WorkspaceView> Workspaces, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Continuation, [property: JsonIgnore] int RowStart = 1);

/// <summary>A member of a workspace.</summary>
/// <param name="Email">The email.</param>
/// <param name="Role">The API role.</param>
public sealed record WorkspaceMemberView(string Email, string Role);

/// <summary>One page of workspace members.</summary>
/// <param name="Members">The members.</param>
/// <param name="Continuation">The continuation token, or null.</param>
/// <param name="RowStart">The 1-based row of the first entry. MCP only.</param>
public sealed record WorkspaceMemberPageResult(IReadOnlyList<WorkspaceMemberView> Members, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Continuation, [property: JsonIgnore] int RowStart = 1);

/// <summary>One initial or added workspace role.</summary>
/// <param name="Email">The email.</param>
/// <param name="Role">The API role.</param>
/// <param name="EmailSent">Whether the email was sent.</param>
public sealed record RoleAssignmentResult(string Email, string Role, bool EmailSent);

/// <summary>A created workspace.</summary>
/// <param name="Id">The workspace id.</param>
/// <param name="Name">The name.</param>
/// <param name="Description">The description, if set.</param>
/// <param name="Members">The initial assignments.</param>
public sealed record WorkspaceCreatedResult(string Id, string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description, IReadOnlyList<RoleAssignmentResult> Members);

/// <summary>An updated workspace.</summary>
/// <param name="Id">The workspace id.</param>
/// <param name="Name">The name.</param>
    /// <param name="Description">The description, if set.</param>
public sealed record WorkspaceUpdatedResult(string Id, string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

/// <summary>A list summary.</summary>
/// <param name="Id">The list id.</param>
/// <param name="Name">The name.</param>
/// <param name="SchemaVersion">The current schema version.</param>
    /// <param name="Description">The description, if set.</param>
public sealed record ListView(string Id, string Name, int SchemaVersion, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

/// <summary>One page of lists.</summary>
/// <param name="Lists">The lists.</param>
/// <param name="Continuation">The continuation token, or null.</param>
/// <param name="RowStart">The 1-based row of the first entry. MCP only.</param>
public sealed record ListPageResult(IReadOnlyList<ListView> Lists, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Continuation, [property: JsonIgnore] int RowStart = 1);

/// <summary>A list schema.</summary>
/// <param name="Schema">The EasyJsonSchema document.</param>
/// <param name="SchemaVersion">The version.</param>
public sealed record SchemaView(JsonElement Schema, int SchemaVersion);

/// <summary>An item write result.</summary>
/// <param name="Id">The item id.</param>
/// <param name="SchemaVersion">The version the item was validated against.</param>
public sealed record ItemWriteResult(string Id, int SchemaVersion);

/// <summary>An item read result. <paramref name="Data"/> is null when the shape is <c>ids</c>.</summary>
/// <param name="Id">The item id.</param>
/// <param name="SchemaVersion">The stored schema version.</param>
/// <param name="Data">The stored JSON, or null.</param>
public sealed record ItemView(string Id, int SchemaVersion, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Data);

/// <summary>One page of items.</summary>
/// <param name="Items">The items.</param>
/// <param name="Continuation">The continuation token, or null.</param>
/// <param name="RowStart">The 1-based row of the first entry. MCP only.</param>
public sealed record ItemPageResult(IReadOnlyList<ItemView> Items, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Continuation, [property: JsonIgnore] int RowStart = 1);

/// <summary>A deletion confirmation.</summary>
/// <param name="Deleted">Always true when the call succeeded.</param>
public sealed record DeletedResult(bool Deleted);

/// <summary>A tenant status.</summary>
/// <param name="Status">The status.</param>
public sealed record StatusResult(string Status);

/// <summary>A regenerated token. The plaintext token is returned once.</summary>
/// <param name="LastTokenIssued">When the new token was issued, in UTC.</param>
/// <param name="Token">The new plaintext token.</param>
public sealed record TokenRegeneratedResult(DateTimeOffset LastTokenIssued, string Token);

/// <summary>One batch operation result. FR-56.</summary>
/// <param name="Index">The 0-based operation index.</param>
/// <param name="Status">The status the single call would return, or 424.</param>
/// <param name="Result">The single-call body when <paramref name="Status"/> is 200.</param>
/// <param name="Error">The error text otherwise.</param>
public sealed record BatchOpResult(int Index, int Status, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);

/// <summary>The batch response. REST returns 200 when the request checks pass.</summary>
/// <param name="Results">One entry per operation, in order.</param>
/// <param name="RetryAfter">A Retry-After value when an operation is 429 or 503. Not part of the JSON body.</param>
public sealed record BatchResult(IReadOnlyList<BatchOpResult> Results, [property: JsonIgnore] TimeSpan? RetryAfter = null);

/// <summary>A list read from a batch, including the current schema.</summary>
/// <param name="Id">The list id.</param>
/// <param name="Name">The name.</param>
/// <param name="SchemaVersion">The current schema version.</param>
/// <param name="Description">The description, if set.</param>
/// <param name="Schema">The current schema document.</param>
public sealed record ListDetail(string Id, string Name, int SchemaVersion, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description, JsonElement Schema);

/// <summary>An optional text field on a patch.</summary>
/// <param name="Specified">Whether the field was present.</param>
/// <param name="Value">The raw value.</param>
public readonly record struct OptionalText(bool Specified, string? Value);

/// <summary>An initial workspace role from a create request, already parsed.</summary>
/// <param name="Email">The raw email.</param>
/// <param name="Role">The raw role.</param>
public readonly record struct InitialRole(string? Email, string? Role);
