using System.Globalization;

namespace AgentConnect;

/// <summary>
/// Name and description limits from FR-45.
/// </summary>
public static class NameRules
{
    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest description.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>
    /// Trims a name and checks its length.
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <param name="trimmed">The trimmed name when valid.</param>
    /// <param name="error">The error text when invalid.</param>
    /// <returns><see langword="true"/> when the name is 1 to 100 characters.</returns>
    public static bool TryNormalizeName(string? name, out string trimmed, out string error)
    {
        trimmed = (name ?? "").Trim();
        if (trimmed.Length < 1 || trimmed.Length > MaxNameLength)
        {
            error = "name: invalid length";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Trims a description. An empty description becomes null.
    /// </summary>
    /// <param name="description">The raw description.</param>
    /// <param name="trimmed">The description, or null.</param>
    /// <param name="error">The error text when invalid.</param>
    /// <returns><see langword="true"/> when the description is at most 1,000 characters.</returns>
    public static bool TryNormalizeDescription(string? description, out string? trimmed, out string error)
    {
        var text = (description ?? "").Trim();
        if (text.Length > MaxDescriptionLength)
        {
            trimmed = null;
            error = "description: invalid length";
            return false;
        }

        trimmed = text.Length == 0 ? null : text;
        error = "";
        return true;
    }

    /// <summary>
    /// Compares names case-insensitively.
    /// </summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when they match.</returns>
    public static bool SameName(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Email rules from FR-46.
/// </summary>
public static class EmailRules
{
    /// <summary>The longest email, after trimming.</summary>
    public const int MaxLength = 254;

    /// <summary>
    /// Trims an email and checks its form.
    /// </summary>
    /// <param name="email">The raw email.</param>
    /// <param name="trimmed">The trimmed email when valid.</param>
    /// <param name="error">The error text when invalid.</param>
    /// <returns><see langword="true"/> when the email is valid.</returns>
    public static bool TryNormalize(string? email, out string trimmed, out string error)
    {
        trimmed = (email ?? "").Trim();
        if (!IsValid(trimmed))
        {
            error = "email: invalid format";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Compares emails case-insensitively.
    /// </summary>
    /// <param name="left">The first email.</param>
    /// <param name="right">The second email.</param>
    /// <returns><see langword="true"/> when they match.</returns>
    public static bool Same(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    #region private

    private static bool IsValid(string email)
    {
        if (email.Length == 0 || email.Length > MaxLength || email.Contains(' '))
        {
            return false;
        }

        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@'))
        {
            return false;
        }

        var domain = email[(at + 1)..];
        return domain.Contains('.');
    }

    #endregion
}

/// <summary>
/// Item id rules from FR-45.
/// </summary>
public static class ItemIdRules
{
    /// <summary>
    /// Checks an item id.
    /// </summary>
    /// <param name="itemId">The id.</param>
    /// <returns><see langword="true"/> when the id is allowed.</returns>
    public static bool IsValid(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId) || itemId.Length > 255 || ContainsForbidden(itemId))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(itemId);
        }
        catch (UriFormatException)
        {
            return true;
        }

        return !ContainsForbidden(decoded);
    }

    #region private

    private static bool ContainsForbidden(string itemId)
    {
        foreach (var character in itemId)
        {
            if (character is '/' or '\\' or '?' or '#')
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}

/// <summary>
/// Page-size rules from FR-41 and FR-50.
/// </summary>
public static class PageRules
{
    /// <summary>The platform default page size.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>The platform default maximum page size.</summary>
    public const int DefaultPageSizeMax = 1000;

    /// <summary>The platform cap on <c>pageSizeMax</c>.</summary>
    public const int PlatformPageSizeMax = 1000;

    /// <summary>
    /// Checks a stored pair.
    /// </summary>
    /// <param name="pageSizeDefault">The default.</param>
    /// <param name="pageSizeMax">The maximum.</param>
    /// <returns>The error text, or null when the pair is valid.</returns>
    public static string? ValidatePair(int pageSizeDefault, int pageSizeMax)
    {
        if (pageSizeDefault < 1)
        {
            return "pageSizeDefault: must be a whole number of at least 1";
        }

        if (pageSizeMax < 1)
        {
            return "pageSizeMax: must be a whole number of at least 1";
        }

        if (pageSizeDefault > pageSizeMax)
        {
            return "pageSizeDefault: must not exceed pageSizeMax";
        }

        if (pageSizeMax > PlatformPageSizeMax)
        {
            return "pageSizeMax: must not exceed 1000";
        }

        return null;
    }

    /// <summary>
    /// Parses a requested page size. A size above the maximum is reduced to the maximum.
    /// </summary>
    /// <param name="text">The raw text, or null when the caller omitted it.</param>
    /// <param name="tenantDefault">The tenant default.</param>
    /// <param name="tenantMax">The tenant maximum.</param>
    /// <param name="pageSize">The size to use.</param>
    /// <param name="error">The error text when invalid.</param>
    /// <returns><see langword="false"/> when the text is not a whole number of at least 1.</returns>
    public static bool TryResolve(string? text, int tenantDefault, int tenantMax, out int pageSize, out string error)
    {
        if (text is null)
        {
            pageSize = Math.Min(tenantDefault, tenantMax);
            error = "";
            return true;
        }

        if (!IsWholeNumber(text, out var requested) || requested < 1)
        {
            pageSize = 0;
            error = "pageSize: must be a whole number of at least 1";
            return false;
        }

        pageSize = requested > tenantMax ? tenantMax : requested;
        error = "";
        return true;
    }

    #region private

    private static bool IsWholeNumber(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 9)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    #endregion
}

/// <summary>
/// The caller of one REST call or MCP tool call. Rights come from the current Member document.
/// </summary>
public sealed class Caller
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Caller"/> class.
    /// </summary>
    /// <param name="member">The member.</param>
    /// <param name="tenant">The tenant.</param>
    public Caller(MemberDocument member, TenantDocument tenant)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(tenant);
        Member = member;
        Tenant = tenant;
    }

    /// <summary>Gets the member.</summary>
    public MemberDocument Member { get; }

    /// <summary>Gets the tenant.</summary>
    public TenantDocument Tenant { get; }

    /// <summary>Gets a value indicating whether the member is a TenantOwner.</summary>
    public bool IsTenantOwner
    {
        get
        {
            return Member.IsTenantOwner;
        }
    }

    /// <summary>
    /// Returns the stored role on a workspace, or null.
    /// </summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <returns>The stored role, or null.</returns>
    public string? RoleOn(string workspaceId)
    {
        return Member.WorkspaceRoles.TryGetValue(workspaceId, out var role) ? role : null;
    }

    /// <summary>
    /// Returns true when this member can see the workspace's general data.
    /// </summary>
    /// <param name="workspaceId">The workspace id.</param>
    /// <returns><see langword="true"/> when the member is a TenantOwner or holds a role there.</returns>
    public bool CanSee(string workspaceId)
    {
        return IsTenantOwner || RoleOn(workspaceId) is not null;
    }
}

/// <summary>
/// Actions in the permission matrix.
/// </summary>
public enum PlatformAction
{
    /// <summary>Add a member to the tenant.</summary>
    AddTenantMember,

    /// <summary>Delete a member.</summary>
    DeleteTenantMember,

    /// <summary>Assign or remove TenantOwner.</summary>
    SetTenantOwner,

    /// <summary>Send an invitation.</summary>
    SendInvitation,

    /// <summary>Send a token.</summary>
    SendToken,

    /// <summary>Regenerate a token.</summary>
    RegenerateToken,

    /// <summary>List tenant members.</summary>
    ListTenantMembers,

    /// <summary>Read tenant page sizes.</summary>
    ViewTenantSettings,

    /// <summary>Change tenant page sizes.</summary>
    SetTenantSettings,

    /// <summary>Create a workspace.</summary>
    CreateWorkspace,

    /// <summary>Delete a workspace.</summary>
    DeleteWorkspace,

    /// <summary>List workspaces.</summary>
    ListWorkspaces,

    /// <summary>Update workspace general data.</summary>
    UpdateWorkspace,

    /// <summary>Add a member to a workspace.</summary>
    AddWorkspaceMember,

    /// <summary>Remove a workspace role.</summary>
    RemoveWorkspaceMember,

    /// <summary>Change a workspace role.</summary>
    ChangeWorkspaceRole,

    /// <summary>List workspace members.</summary>
    ListWorkspaceMembers,

    /// <summary>Create a list.</summary>
    CreateList,

    /// <summary>Delete a list.</summary>
    DeleteList,

    /// <summary>Update a list name or description.</summary>
    UpdateList,

    /// <summary>Update a list schema.</summary>
    UpdateSchema,

    /// <summary>List lists.</summary>
    ListLists,

    /// <summary>Read a schema.</summary>
    GetSchema,

    /// <summary>Upsert an item.</summary>
    UpsertItem,

    /// <summary>Delete an item.</summary>
    DeleteItem,

    /// <summary>Query items.</summary>
    QueryItems,

    /// <summary>Read an item.</summary>
    GetItem,
}

/// <summary>
/// Permission checks from the section 3 matrix. Roles are not cumulative; a TenantOwner flag adds its own rights.
/// </summary>
public static class Permissions
{
    /// <summary>
    /// Checks a tenant-level action.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="action">The action.</param>
    /// <param name="targetMemberId">The target member, when the action has one.</param>
    /// <returns>Null when allowed; otherwise 403.</returns>
    public static PlatformError? AuthorizeTenant(Caller caller, PlatformAction action, string? targetMemberId)
    {
        var allowed = action switch
        {
            PlatformAction.AddTenantMember => caller.IsTenantOwner,
            PlatformAction.DeleteTenantMember => caller.IsTenantOwner,
            PlatformAction.SetTenantOwner => caller.IsTenantOwner,
            PlatformAction.SendInvitation => caller.IsTenantOwner,
            PlatformAction.SendToken => caller.IsTenantOwner,
            PlatformAction.RegenerateToken => caller.IsTenantOwner,
            PlatformAction.ListTenantMembers => caller.IsTenantOwner || IsWorkspaceOwnerAnywhere(caller),
            PlatformAction.ViewTenantSettings => caller.IsTenantOwner,
            PlatformAction.SetTenantSettings => caller.IsTenantOwner,
            PlatformAction.CreateWorkspace => caller.IsTenantOwner,
            PlatformAction.DeleteWorkspace => caller.IsTenantOwner,
            PlatformAction.ListWorkspaces => true,
            _ => false,
        };

        if (!allowed)
        {
            return PlatformError.NotAllowed;
        }

        if (targetMemberId is not null
            && targetMemberId == caller.Member.Id
            && action is PlatformAction.DeleteTenantMember or PlatformAction.SetTenantOwner)
        {
            return PlatformError.NotAllowedOnYourself;
        }

        return null;
    }

    /// <summary>
    /// Checks a workspace-level action. The workspace must already be known to exist.
    /// A caller who cannot see it gets 404. A caller who can see it but lacks the right gets 403.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="action">The action.</param>
    /// <param name="targetMemberId">The target member, when the action changes their role.</param>
    /// <param name="targetRole">The target's current stored role, or null.</param>
    /// <param name="newRole">The new stored role for a change, or null for a removal.</param>
    /// <returns>Null when allowed; otherwise 404 or 403.</returns>
    public static PlatformError? AuthorizeWorkspace(
        Caller caller,
        string workspaceId,
        PlatformAction action,
        string? targetMemberId,
        string? targetRole,
        string? newRole)
    {
        if (!caller.CanSee(workspaceId))
        {
            return PlatformError.NotFound;
        }

        var role = caller.RoleOn(workspaceId);
        var allowed = action switch
        {
            PlatformAction.UpdateWorkspace => role == StoredRoles.Owner,
            PlatformAction.AddWorkspaceMember => caller.IsTenantOwner || role == StoredRoles.Owner,
            PlatformAction.RemoveWorkspaceMember => caller.IsTenantOwner || role == StoredRoles.Owner,
            PlatformAction.ChangeWorkspaceRole => caller.IsTenantOwner || role == StoredRoles.Owner,
            PlatformAction.ListWorkspaceMembers => caller.IsTenantOwner || role == StoredRoles.Owner,
            PlatformAction.CreateList => role == StoredRoles.Owner,
            PlatformAction.DeleteList => role == StoredRoles.Owner,
            PlatformAction.UpdateList => role == StoredRoles.Owner,
            PlatformAction.UpdateSchema => role == StoredRoles.Owner,
            PlatformAction.ListLists => role is StoredRoles.Owner or StoredRoles.Write or StoredRoles.Read,
            PlatformAction.GetSchema => role is StoredRoles.Owner or StoredRoles.Write or StoredRoles.Read,
            PlatformAction.UpsertItem => role is StoredRoles.Owner or StoredRoles.Write,
            PlatformAction.DeleteItem => role is StoredRoles.Owner or StoredRoles.Write,
            PlatformAction.QueryItems => role is StoredRoles.Owner or StoredRoles.Write or StoredRoles.Read,
            PlatformAction.GetItem => role is StoredRoles.Owner or StoredRoles.Write or StoredRoles.Read,
            _ => false,
        };

        if (!allowed)
        {
            return PlatformError.NotAllowed;
        }

        if (targetMemberId is not null
            && targetMemberId == caller.Member.Id
            && !caller.IsTenantOwner
            && role == StoredRoles.Owner
            && targetRole == StoredRoles.Owner
            && action == PlatformAction.RemoveWorkspaceMember)
        {
            return PlatformError.NotAllowedOnYourself;
        }

        if (targetMemberId is not null
            && targetMemberId == caller.Member.Id
            && !caller.IsTenantOwner
            && role == StoredRoles.Owner
            && targetRole == StoredRoles.Owner
            && action == PlatformAction.ChangeWorkspaceRole
            && newRole != StoredRoles.Owner)
        {
            return PlatformError.NotAllowedOnYourself;
        }

        return null;
    }

    #region private

    private static bool IsWorkspaceOwnerAnywhere(Caller caller)
    {
        foreach (var role in caller.Member.WorkspaceRoles.Values)
        {
            if (role == StoredRoles.Owner)
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}
