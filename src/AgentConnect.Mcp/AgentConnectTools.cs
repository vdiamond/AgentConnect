using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentConnect;

/// <summary>
/// The 16 MCP tools from FR-33. They call the same services as the REST API.
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
    public AgentConnectTools(IHttpContextAccessor http, WorkspaceService workspaces, MemberService members, ContentService content)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(content);
        _http = http;
        _workspaces = workspaces;
        _members = members;
        _content = content;
    }

    /// <summary>Lists the workspaces the caller can see.</summary>
    /// <param name="pageSize">The page size.</param>
    /// <param name="continuation">The continuation token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "list_workspaces"), Description("Lists the workspaces the caller can see, with the caller's role in each.")]
    public async Task<CallToolResult> ListWorkspaces(int? pageSize = null, string? continuation = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _workspaces.ListAsync(caller, pageSize, pageSize is not null, continuation, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Updates a workspace name and description.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workspace.</returns>
    [McpServerTool(Name = "update_workspace"), Description("Updates a workspace's name and description.")]
    public async Task<CallToolResult> UpdateWorkspace(string workspaceId, string? name = null, string? description = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _workspaces.UpdateAsync(caller, workspaceId, new OptionalText(name is not null, name), new OptionalText(description is not null, description), cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Lists the lists in a workspace.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="continuation">The continuation token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "list_lists"), Description("Lists the lists in a workspace.")]
    public async Task<CallToolResult> ListLists(string workspaceId, int? pageSize = null, string? continuation = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.ListListsAsync(caller, workspaceId, pageSize, pageSize is not null, continuation, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Creates a list.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The name.</param>
    /// <param name="schema">The EasyJsonSchema document.</param>
    /// <param name="description">The description.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list.</returns>
    [McpServerTool(Name = "create_list"), Description("Creates a list with an EasyJsonSchema schema.")]
    public async Task<CallToolResult> CreateList(string workspaceId, string name, JsonObject schema, string? description = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.CreateListAsync(caller, workspaceId, name, description, description is not null, schema.ToJsonString(), cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Updates a list name or description.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="name">The name.</param>
    /// <param name="description">The description.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list.</returns>
    [McpServerTool(Name = "update_list"), Description("Updates a list's name or description.")]
    public async Task<CallToolResult> UpdateList(string workspaceId, string listId, string? name = null, string? description = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.UpdateListAsync(caller, workspaceId, listId, new OptionalText(name is not null, name), new OptionalText(description is not null, description), cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Deletes a list.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    [McpServerTool(Name = "delete_list"), Description("Deletes a list. Its items are removed in the background.")]
    public async Task<CallToolResult> DeleteList(string workspaceId, string listId, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.DeleteListAsync(caller, workspaceId, listId, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Reads a list schema.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema.</returns>
    [McpServerTool(Name = "get_schema"), Description("Reads a list's current schema and schema version.")]
    public async Task<CallToolResult> GetSchema(string workspaceId, string listId, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.GetSchemaAsync(caller, workspaceId, listId, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Replaces a list schema.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="schema">The new schema.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new version.</returns>
    [McpServerTool(Name = "update_schema"), Description("Supplies a new schema. The list version increases by 1.")]
    public async Task<CallToolResult> UpdateSchema(string workspaceId, string listId, JsonObject schema, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.UpdateSchemaAsync(caller, workspaceId, listId, schema.ToJsonString(), cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Queries items.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="shape">full or ids.</param>
    /// <param name="schemaVersion">The version filter, as raw JSON.</param>
    /// <param name="where">The conditions, as raw JSON.</param>
    /// <param name="orderBy">The sort, as raw JSON.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="continuation">The continuation token.</param>
    /// <param name="request">The raw call, used to reject unknown arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "query_items"), Description("Queries the items of one list.")]
    public async Task<CallToolResult> QueryItems(
        string workspaceId,
        string listId,
        string? shape = null,
        JsonElement? schemaVersion = null,
        JsonElement? where = null,
        JsonElement? orderBy = null,
        int? pageSize = null,
        string? continuation = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        var unknown = UnknownArguments(request, "workspaceId", "listId", "shape", "schemaVersion", "where", "orderBy", "pageSize", "continuation");
        var result = await Guarded(() => _content.QueryItemsAsync(caller, workspaceId, listId, shape, Copy(schemaVersion), Copy(where), Copy(orderBy), unknown, pageSize, pageSize is not null, continuation, cancellationToken)).ConfigureAwait(false);
        return ToResult(result);
    }

    /// <summary>Reads one item.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item.</returns>
    [McpServerTool(Name = "get_item"), Description("Reads one item. Reads do not re-validate.")]
    public async Task<CallToolResult> GetItem(string workspaceId, string listId, string itemId, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.GetItemAsync(caller, workspaceId, listId, itemId, cancellationToken)).ConfigureAwait(false));
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
        string workspaceId,
        string listId,
        JsonElement? data = null,
        string? itemId = null,
        RequestContext<CallToolRequestParams>? request = null,
        CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        var unknown = UnknownArguments(request, "workspaceId", "listId", "data", "itemId");
        if (unknown.Count > 0)
        {
            return Error(PlatformError.BadRequest(unknown[0] + ": unknown field"));
        }

        var json = data is { } value && value.ValueKind != JsonValueKind.Undefined ? value.GetRawText() : null;
        return ToResult(await Guarded(() => _content.UpsertItemAsync(caller, workspaceId, listId, itemId, json, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Deletes an item.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    [McpServerTool(Name = "delete_item"), Description("Deletes an item.")]
    public async Task<CallToolResult> DeleteItem(string workspaceId, string listId, string itemId, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _content.DeleteItemAsync(caller, workspaceId, listId, itemId, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Adds an email to a workspace.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="role">WorkspaceOwner, WorkspaceWrite, or WorkspaceRead.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The assignment, including emailSent.</returns>
    [McpServerTool(Name = "invite_member"), Description("Adds an email to a workspace and sends the matching email.")]
    public async Task<CallToolResult> InviteMember(string workspaceId, string email, string role, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _members.AddToWorkspaceAsync(caller, workspaceId, email, role, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Changes a workspace role.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="role">WorkspaceOwner, WorkspaceWrite, or WorkspaceRead.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The role.</returns>
    [McpServerTool(Name = "change_workspace_role"), Description("Changes a member's role in a workspace.")]
    public async Task<CallToolResult> ChangeWorkspaceRole(string workspaceId, string email, string role, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _members.ChangeRoleAsync(caller, workspaceId, email, role, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Removes a workspace role.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    [McpServerTool(Name = "revoke_workspace_access"), Description("Removes a member's role in a workspace. The member stays in the tenant.")]
    public async Task<CallToolResult> RevokeWorkspaceAccess(string workspaceId, string email, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _members.RemoveFromWorkspaceAsync(caller, workspaceId, email, cancellationToken)).ConfigureAwait(false));
    }

    /// <summary>Lists workspace members.</summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="continuation">The continuation token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "list_workspace_members"), Description("Lists the members who hold a role on a workspace.")]
    public async Task<CallToolResult> ListWorkspaceMembers(string workspaceId, int? pageSize = null, string? continuation = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Error(PlatformError.InvalidToken);
        }

        return ToResult(await Guarded(() => _members.ListWorkspaceMembersAsync(caller, workspaceId, pageSize, pageSize is not null, continuation, cancellationToken)).ConfigureAwait(false));
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

    private static JsonElement Copy(JsonElement? element)
    {
        return element is { } value && value.ValueKind != JsonValueKind.Undefined ? value.Clone() : default;
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

    private static async Task<OperationResult<T>> Guarded<T>(Func<Task<OperationResult<T>>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (StorageThrottleException exception)
        {
            return OperationResult<T>.Fail(PlatformError.TooManyRequests(exception.RetryAfter));
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
