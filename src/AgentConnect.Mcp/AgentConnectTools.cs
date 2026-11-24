using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentConnect;

/// <summary>
/// The 17 MCP tools from FR-33. They call the same services as the REST API.
/// </summary>
[McpServerToolType]
public sealed class AgentConnectTools
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentConnectTools"/> class.
    /// </summary>
    /// <param name="http">The HTTP accessor.</param>
    /// <param name="workspaces">The workspaces.</param>
    /// <param name="members">The members.</param>
    /// <param name="content">The lists and items.</param>
    /// <param name="logger">The logger.</param>
    public AgentConnectTools(IHttpContextAccessor http, WorkspaceService workspaces, MemberService members, ContentService content, ILogger<AgentConnectTools> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _workspaces = workspaces;
        _members = members;
        _content = content;
        _logger = logger;
    }

    /// <summary>Lists the workspaces the caller can see.</summary>
    /// <param name="pageSize">The page size, as raw JSON.</param>
    /// <param name="continuation">The continuation token, as raw JSON.</param>
    /// <param name="request">The raw call. An explicit JSON null is not treated as omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "list_workspaces"), Description("Lists the workspaces the caller can see, with the caller's role in each.")]
    public async Task<CallToolResult> ListWorkspaces(
        JsonElement? pageSize = null,
        JsonElement? continuation = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (Extra(request, "pageSize", "continuation") is { } extra)
        {
            return extra;
        }

        var (size, specified, token) = Page(request, pageSize, continuation);
        return ToResult(await Guarded(() => _workspaces.ListAsync(caller, size, specified, token, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Updates a workspace name and description.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workspace.</returns>
    [McpServerTool(Name = "update_workspace"), Description("Updates a workspace's name and description.")]
    public async Task<CallToolResult> UpdateWorkspace(
        JsonElement? workspaceId = null,
        JsonElement? name = null,
        JsonElement? description = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } idError)
        {
            return Error(idError);
        }

        var nameError = ReadPatch(request, "name", name, false, out var nextName);
        if (nameError is not null)
        {
            return Error(nameError);
        }

        var descriptionError = ReadPatch(request, "description", description, true, out var nextDescription);
        if (descriptionError is not null)
        {
            return Error(descriptionError);
        }

        return ToResult(await Guarded(() => _workspaces.UpdateAsync(caller, workspaceText, nextName, nextDescription, cancellationToken, UnknownAfterGate(request, "workspaceId", "name", "description"))).ConfigureAwait(false));
    }

    /// <summary>Lists the lists in a workspace.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The page size, as raw JSON.</param>
    /// <param name="continuation">The continuation token, as raw JSON.</param>
    /// <param name="request">The raw call. An explicit JSON null is not treated as omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "list_lists"), Description("Lists the lists in a workspace.")]
    public async Task<CallToolResult> ListLists(
        JsonElement? workspaceId = null,
        JsonElement? pageSize = null,
        JsonElement? continuation = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } idError)
        {
            return Error(idError);
        }

        var (size, specified, token) = Page(request, pageSize, continuation);
        return ToResult(await Guarded(() => _content.ListListsAsync(caller, workspaceText, size, specified, token, cancellationToken, UnknownAfterGate(request, "workspaceId", "pageSize", "continuation"))).ConfigureAwait(false));
    }

    /// <summary>Creates a list.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The name.</param>
    /// <param name="schema">The EasyJsonSchema document, as raw JSON.</param>
    /// <param name="description">The description.</param>
    /// <param name="request">The raw call. An explicit JSON null is not treated as omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list.</returns>
    [McpServerTool(Name = "create_list"), Description("Creates a list with an EasyJsonSchema schema.")]
    public async Task<CallToolResult> CreateList(
        JsonElement? workspaceId = null,
        JsonElement? name = null,
        JsonElement? schema = null,
        JsonElement? description = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        if (RequiredArgument(request, "name", name, out var nameText) is { } nameError)
        {
            return Error(nameError);
        }

        var descriptionElement = Read(request, "description", description);
        string? descriptionText = null;
        var descriptionSpecified = false;
        if (descriptionElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (descriptionElement.ValueKind != JsonValueKind.String)
            {
                return Error(PlatformError.BadRequest("description: expected String, found " + JsonKinds.Of(descriptionElement)));
            }

            descriptionText = descriptionElement.GetString();
            descriptionSpecified = true;
        }

        return ToResult(await Guarded(() => _content.CreateListAsync(caller, workspaceText, nameText, descriptionText, descriptionSpecified, JsonText(request, "schema", schema), cancellationToken, UnknownAfterGate(request, "workspaceId", "name", "schema", "description"))).ConfigureAwait(false));
    }

    /// <summary>Updates a list name or description.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list.</returns>
    [McpServerTool(Name = "update_list"), Description("Updates a list's name or description.")]
    public async Task<CallToolResult> UpdateList(
        JsonElement? workspaceId = null,
        JsonElement? listId = null,
        JsonElement? name = null,
        JsonElement? description = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listGate = AfterId(request, "listId", listId, out var listText, "workspaceId", "listId", "name", "description");
        var nameError = ReadPatch(request, "name", name, false, out var nextName);
        if (nameError is not null)
        {
            return Error(nameError);
        }

        var descriptionError = ReadPatch(request, "description", description, true, out var nextDescription);
        if (descriptionError is not null)
        {
            return Error(descriptionError);
        }

        return ToResult(await Guarded(() => _content.UpdateListAsync(caller, workspaceText, listText, nextName, nextDescription, cancellationToken, listGate)).ConfigureAwait(false));
    }

    /// <summary>Deletes a list.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    [McpServerTool(Name = "delete_list"), Description("Deletes a list. Its items are removed in the background.")]
    public async Task<CallToolResult> DeleteList(JsonElement? workspaceId = null, JsonElement? listId = null, RequestContext<CallToolRequestParams>? request = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listGate = AfterId(request, "listId", listId, out var listText, "workspaceId", "listId");
        return ToResult(await Guarded(() => _content.DeleteListAsync(caller, workspaceText, listText, cancellationToken, listGate)).ConfigureAwait(false));
    }

    /// <summary>Reads a list schema.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema.</returns>
    [McpServerTool(Name = "get_schema"), Description("Reads a list's current schema and schema version.")]
    public async Task<CallToolResult> GetSchema(JsonElement? workspaceId = null, JsonElement? listId = null, RequestContext<CallToolRequestParams>? request = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listGate = AfterId(request, "listId", listId, out var listText, "workspaceId", "listId");
        return ToResult(await Guarded(() => _content.GetSchemaAsync(caller, workspaceText, listText, cancellationToken, listGate)).ConfigureAwait(false));
    }

    /// <summary>Replaces a list schema.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="schema">The new schema, as raw JSON.</param>
    /// <param name="request">The raw call. An explicit JSON null is not treated as omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new version.</returns>
    [McpServerTool(Name = "update_schema"), Description("Supplies a new schema. The list version increases by 1.")]
    public async Task<CallToolResult> UpdateSchema(
        JsonElement? workspaceId = null,
        JsonElement? listId = null,
        JsonElement? schema = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listGate = AfterId(request, "listId", listId, out var listText, "workspaceId", "listId", "schema");
        return ToResult(await Guarded(() => _content.UpdateSchemaAsync(caller, workspaceText, listText, JsonText(request, "schema", schema), cancellationToken, listGate)).ConfigureAwait(false));
    }

    /// <summary>Queries items.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="shape">full or ids, as raw JSON.</param>
    /// <param name="schemaVersion">The version filter, as raw JSON.</param>
    /// <param name="where">The conditions, as raw JSON.</param>
    /// <param name="orderBy">The sort, as raw JSON.</param>
    /// <param name="pageSize">The page size, as raw JSON.</param>
    /// <param name="continuation">The continuation token, as raw JSON.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "query_items"), Description("Queries the items of one list.")]
    public async Task<CallToolResult> QueryItems(
        JsonElement? workspaceId = null,
        JsonElement? listId = null,
        JsonElement? shape = null,
        JsonElement? schemaVersion = null,
        JsonElement? where = null,
        JsonElement? orderBy = null,
        JsonElement? pageSize = null,
        JsonElement? continuation = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listGate = AfterId(request, "listId", listId, out var listText, "workspaceId", "listId", "shape", "schemaVersion", "where", "orderBy", "pageSize", "continuation");
        var (size, specified, token) = Page(request, pageSize, continuation);
        var result = await Guarded(() => _content.QueryItemsAsync(
            caller,
            workspaceText,
            listText,
            TextArgument(request, "shape", shape),
            Given(request, "schemaVersion", schemaVersion),
            Given(request, "where", where),
            Given(request, "orderBy", orderBy),
            [],
            size,
            specified,
            token,
            cancellationToken,
            listGate)).ConfigureAwait(false);
        return ToResult(result);
    }

    /// <summary>Reads one item.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item.</returns>
    [McpServerTool(Name = "get_item"), Description("Reads one item. Reads do not re-validate.")]
    public async Task<CallToolResult> GetItem(JsonElement? workspaceId = null, JsonElement? listId = null, JsonElement? itemId = null, RequestContext<CallToolRequestParams>? request = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listProblem = RequiredArgument(request, "listId", listId, out var listValue);
        var itemProblem = RequiredArgument(request, "itemId", itemId, out var itemValue);
        var unknown = UnknownAfterGate(request, "workspaceId", "listId", "itemId");
        return ToResult(await Guarded(() => _content.GetItemAsync(caller, workspaceText, listProblem is null ? listValue : null, itemProblem is null ? itemValue : null, cancellationToken, () => listProblem ?? itemProblem ?? unknown())).ConfigureAwait(false));
    }

    /// <summary>Creates or replaces an item.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="data">The item JSON, as raw JSON.</param>
    /// <param name="itemId">The item id. Omitted ids are generated.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The id and schema version.</returns>
    [McpServerTool(Name = "upsert_item"), Description("Creates or replaces an item. The JSON is validated against the current schema.")]
    public async Task<CallToolResult> UpsertItem(
        JsonElement? workspaceId = null,
        JsonElement? listId = null,
        JsonElement? data = null,
        JsonElement? itemId = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listProblem = RequiredArgument(request, "listId", listId, out var listValue);
        PlatformError? itemProblem = null;
        string? itemForCall = null;
        var itemElement = Read(request, "itemId", itemId);
        if (itemElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            itemProblem = RequiredArgument(request, "itemId", itemId, out var itemValue);
            if (itemProblem is null)
            {
                itemForCall = itemValue;
            }
        }

        var dataElement = Read(request, "data", data);
        var json = dataElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : dataElement.GetRawText();
        var unknown = UnknownAfterGate(request, "workspaceId", "listId", "data", "itemId");
        return ToResult(await Guarded(() => _content.UpsertItemAsync(caller, workspaceText, listProblem is null ? listValue : null, itemForCall, json, cancellationToken, () =>
        {
            var idError = listProblem ?? itemProblem ?? unknown();
            if (idError is not null)
            {
                return idError;
            }

            return dataElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? PlatformError.BadRequest("data: required")
                : null;
        })).ConfigureAwait(false));
    }

    /// <summary>Deletes an item.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    [McpServerTool(Name = "delete_item"), Description("Deletes an item.")]
    public async Task<CallToolResult> DeleteItem(JsonElement? workspaceId = null, JsonElement? listId = null, JsonElement? itemId = null, RequestContext<CallToolRequestParams>? request = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var listProblem = RequiredArgument(request, "listId", listId, out var listValue);
        var itemProblem = RequiredArgument(request, "itemId", itemId, out var itemValue);
        var unknown = UnknownAfterGate(request, "workspaceId", "listId", "itemId");
        return ToResult(await Guarded(() => _content.DeleteItemAsync(caller, workspaceText, listProblem is null ? listValue : null, itemProblem is null ? itemValue : null, cancellationToken, () => listProblem ?? itemProblem ?? unknown())).ConfigureAwait(false));
    }

    /// <summary>Adds an email to a workspace.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="role">WorkspaceOwner, WorkspaceWrite, or WorkspaceRead.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The assignment, including emailSent.</returns>
    [McpServerTool(Name = "invite_member"), Description("Adds an email to a workspace and sends the matching email.")]
    public async Task<CallToolResult> InviteMember(
        JsonElement? workspaceId = null,
        JsonElement? email = null,
        JsonElement? role = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        if (ReadMemberField(request, "email", email, "email: required", "email: invalid format") is not string emailText)
        {
            return Error(PlatformError.BadRequest(MemberFieldError(request, "email", email, "email: required", "email: invalid format")!));
        }

        if (ReadMemberField(request, "role", role, "role: required", "role: invalid") is not string roleText)
        {
            return Error(PlatformError.BadRequest(MemberFieldError(request, "role", role, "role: required", "role: invalid")!));
        }

        return ToResult(await Guarded(() => _members.AddToWorkspaceAsync(caller, workspaceText, emailText, roleText, cancellationToken, UnknownAfterGate(request, "workspaceId", "email", "role"))).ConfigureAwait(false));
    }

    /// <summary>Changes a workspace role.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="role">WorkspaceOwner, WorkspaceWrite, or WorkspaceRead.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The role.</returns>
    [McpServerTool(Name = "change_workspace_role"), Description("Changes a member's role in a workspace.")]
    public async Task<CallToolResult> ChangeWorkspaceRole(
        JsonElement? workspaceId = null,
        JsonElement? email = null,
        JsonElement? role = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        if (ReadMemberField(request, "email", email, "email: required", "email: invalid format") is not string emailText)
        {
            return Error(PlatformError.BadRequest(MemberFieldError(request, "email", email, "email: required", "email: invalid format")!));
        }

        if (ReadMemberField(request, "role", role, "role: required", "role: invalid") is not string roleText)
        {
            return Error(PlatformError.BadRequest(MemberFieldError(request, "role", role, "role: required", "role: invalid")!));
        }

        return ToResult(await Guarded(() => _members.ChangeRoleAsync(caller, workspaceText, emailText, roleText, cancellationToken, UnknownAfterGate(request, "workspaceId", "email", "role"))).ConfigureAwait(false));
    }

    /// <summary>Removes a workspace role.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    [McpServerTool(Name = "revoke_workspace_access"), Description("Removes a member's role in a workspace. The member stays in the tenant.")]
    public async Task<CallToolResult> RevokeWorkspaceAccess(
        JsonElement? workspaceId = null,
        JsonElement? email = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        if (ReadMemberField(request, "email", email, "email: required", "email: invalid format") is not string emailText)
        {
            return Error(PlatformError.BadRequest(MemberFieldError(request, "email", email, "email: required", "email: invalid format")!));
        }

        return ToResult(await Guarded(() => _members.RemoveFromWorkspaceAsync(caller, workspaceText, emailText, cancellationToken, UnknownAfterGate(request, "workspaceId", "email"))).ConfigureAwait(false));
    }

    /// <summary>Lists workspace members.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The page size, as raw JSON.</param>
    /// <param name="continuation">The continuation token, as raw JSON.</param>
    /// <param name="request">The raw call. An explicit JSON null is not treated as omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "list_workspace_members"), Description("Lists the members who hold a role on a workspace.")]
    public async Task<CallToolResult> ListWorkspaceMembers(
        JsonElement? workspaceId = null,
        JsonElement? pageSize = null,
        JsonElement? continuation = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (RequiredArgument(request, "workspaceId", workspaceId, out var workspaceText) is { } workspaceError)
        {
            return Error(workspaceError);
        }

        var (size, specified, token) = Page(request, pageSize, continuation);
        return ToResult(await Guarded(() => _members.ListWorkspaceMembersAsync(caller, workspaceText, size, specified, token, cancellationToken, UnknownAfterGate(request, "workspaceId", "pageSize", "continuation"))).ConfigureAwait(false));
    }

    /// <summary>Sets or clears the TenantOwner flag. FR-9.</summary>
    /// <param name="email">The member email, as raw JSON.</param>
    /// <param name="isTenantOwner">The flag, as raw JSON.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The member.</returns>
    [McpServerTool(Name = "set_tenant_owner"), Description("Gives or removes the TenantOwner flag. A TenantOwner cannot change their own flag.")]
    public async Task<CallToolResult> SetTenantOwner(
        JsonElement? email = null,
        JsonElement? isTenantOwner = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        if (!caller.IsTenantOwner)
        {
            return Error(PlatformError.NotAllowed);
        }

        var flag = Read(request, "isTenantOwner", isTenantOwner);
        if (flag.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Error(PlatformError.BadRequest("isTenantOwner: required"));
        }

        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Error(PlatformError.BadRequest("isTenantOwner: expected Boolean, found " + JsonKinds.Of(flag)));
        }

        if (ReadMemberField(request, "email", email, "email: required", "email: invalid format") is not string emailText)
        {
            return Error(PlatformError.BadRequest(MemberFieldError(request, "email", email, "email: required", "email: invalid format")!));
        }

        return ToResult(await Guarded(() => _members.SetTenantOwnerAsync(caller, emailText, flag.GetBoolean(), cancellationToken, UnknownAfterGate(request, "email", "isTenantOwner"))).ConfigureAwait(false));
    }

    #region private

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IHttpContextAccessor _http;
    private readonly WorkspaceService _workspaces;
    private readonly MemberService _members;
    private readonly ContentService _content;
    private readonly ILogger _logger;

    private static JsonElement Read(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound)
    {
        if (request?.Params?.Arguments is { } arguments && arguments.TryGetValue(name, out var raw))
        {
            return raw.ValueKind == JsonValueKind.Undefined ? default : raw.Clone();
        }

        return bound is { } value && value.ValueKind != JsonValueKind.Undefined ? value.Clone() : default;
    }

    private static string? JsonText(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound)
    {
        var element = Read(request, name, bound);
        return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : element.GetRawText();
    }

    private static string? TextArgument(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound)
    {
        var element = Read(request, name, bound);
        return element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => element.GetRawText(),
        };
    }

    private static JsonElement Given(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound)
    {
        var element = Read(request, name, bound);
        return element.ValueKind == JsonValueKind.Null ? default : element;
    }

    private static (int? Size, bool Specified, string? Continuation) Page(RequestContext<CallToolRequestParams>? request, JsonElement? pageSize, JsonElement? continuation)
    {
        var sizeElement = Read(request, "pageSize", pageSize);
        var specified = sizeElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        int? size = null;
        if (specified && IsWhole(sizeElement, out var number))
        {
            size = number;
        }

        return (size, specified, TextArgument(request, "continuation", continuation));
    }

    private static bool IsWhole(JsonElement element, out int number)
    {
        number = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw.StartsWith('-'))
        {
            return false;
        }

        return element.TryGetInt32(out number) && number >= 1;
    }

    private static Func<PlatformError?> AfterId(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound, out string? text, params string[] known)
    {
        var problem = RequiredArgument(request, name, bound, out var value);
        text = problem is null ? value : null;
        var unknown = UnknownAfterGate(request, known);
        return () => problem ?? unknown();
    }

    private static PlatformError? RequiredArgument(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound, out string text)
    {
        var element = Read(request, name, bound);
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            text = "";
            return PlatformError.BadRequest(name + ": required");
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            text = "";
            return PlatformError.BadRequest(name + ": expected String, found " + JsonKinds.Of(element));
        }

        text = element.GetString() ?? "";
        return null;
    }

    private static Func<PlatformError?> UnknownAfterGate(RequestContext<CallToolRequestParams>? request, params string[] known)
    {
        return () =>
        {
            var unknown = UnknownArguments(request, known);
            return unknown.Count == 0 ? null : PlatformError.BadRequest(unknown[0] + ": unknown field");
        };
    }

    private static CallToolResult? Extra(RequestContext<CallToolRequestParams>? request, params string[] known)
    {
        var unknown = UnknownArguments(request, known);
        return unknown.Count == 0 ? null : Error(PlatformError.BadRequest(unknown[0] + ": unknown field"));
    }

    private static PlatformError? ReadPatch(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound, bool clearOnNull, out OptionalText value)
    {
        var element = Read(request, name, bound);
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            value = new OptionalText(false, null);
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            value = clearOnNull ? new OptionalText(true, null) : new OptionalText(false, null);
            return clearOnNull ? null : PlatformError.BadRequest(name + ": expected String, found Null");
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            value = new OptionalText(false, null);
            return PlatformError.BadRequest(name + ": expected String, found " + JsonKinds.Of(element));
        }

        value = new OptionalText(true, element.GetString());
        return null;
    }

    private static string? ReadMemberField(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound, string missing, string wrongKind)
    {
        return MemberFieldError(request, name, bound, missing, wrongKind) is null
            ? Read(request, name, bound).GetString()
            : null;
    }

    private static string? MemberFieldError(RequestContext<CallToolRequestParams>? request, string name, JsonElement? bound, string missing, string wrongKind)
    {
        var element = Read(request, name, bound);
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return missing;
        }

        return element.ValueKind == JsonValueKind.String ? null : wrongKind;
    }

    private static List<string> UnknownArguments(RequestContext<CallToolRequestParams>? request, params string[] known)
    {
        var unknown = new List<string>();
        if (request?.Params?.Arguments is not { } arguments)
        {
            return unknown;
        }

        foreach (var name in arguments.Keys)
        {
            if (Array.IndexOf(known, name) < 0)
            {
                unknown.Add(name);
            }
        }

        return unknown;
    }

    private async Task<OperationResult<T>> Guarded<T>(Func<Task<OperationResult<T>>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (StorageThrottleException exception)
        {
            return OperationResult<T>.Fail(PlatformError.TooManyRequests(exception.RetryAfter));
        }
        catch (StorageUnavailableException exception)
        {
            return OperationResult<T>.Fail(PlatformError.ServiceUnavailable(exception.RetryAfter));
        }
        catch (DocumentMissingException)
        {
            return OperationResult<T>.Fail(PlatformError.NotFound);
        }
        catch (StorageTooLargeException)
        {
            return OperationResult<T>.Fail(PlatformError.ItemTooLarge);
        }
        catch (StorageLimitException)
        {
            return OperationResult<T>.Fail(PlatformError.ListStorageLimit);
        }
        catch (StorageConflictException)
        {
            return OperationResult<T>.Fail(PlatformError.Conflict("$: conflict"));
        }
        catch (StorageFailureException exception)
        {
            PlatformSupport.LogCosmosFault(_logger, exception);
            return OperationResult<T>.Fail(PlatformError.InternalError);
        }
    }

    private Caller? Caller()
    {
        return _http.HttpContext?.Items[AgentConnectBuilder.CallerItemKey] as Caller;
    }

    private static CallToolResult ToResult<T>(OperationResult<T> result)
    {
        if (result.Error is PlatformError error)
        {
            return Error(error);
        }

        return new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Value, Json) }],
        };
    }

    private static CallToolResult Error(PlatformError error)
    {
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = error.Text }],
        };
    }

    #endregion
}
