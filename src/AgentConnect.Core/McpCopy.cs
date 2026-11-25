namespace AgentConnect;

/// <summary>
/// MCP instructions and tool descriptions from section 6 and section 9.2. The instructions text is the fenced block, verbatim.
/// </summary>
public static class McpCopy
{
    /// <summary>The <c>instructions</c> value returned by MCP initialize. FR-57.</summary>
    public const string Instructions = """
        AgentConnect stores shared data as Items in Lists inside Workspaces. Each List has a schema.

        Showing results:
        - Show lists as numbered rows using each entry's row number. Numbers continue across pages.
        - Hide ids unless asked; keep them to act on the row the person picks.
        - A row number refers to the most recent list you showed. If the list changed, show it again before acting.
        - Fetch more pages only when the person wants more.

        Before changing data:
        - Before any delete, or a batch that changes data, say in one line what will change and wait for a yes. Deletes cannot be undone.
        - If a result contains a token, show it once, say it will not be shown again, and never repeat or store it.

        Working well:
        - Read get_schema before writing Items. Let the server filter and sort (where, orderBy).
        - After a schema change, tell the person older Items may need migrating.

        Errors ("<path>: <problem>"):
        - Tell the person in plain words what failed. Fix a named field and retry once.
        - "$: not allowed": say which role can do it (the tool's description names it).
        - "$: conflict": re-read, then retry once.
        - "$: too many requests" or "$: service unavailable": wait, then retry.
        - emailSent: false: say the email was not sent and can be resent.
        """;

    /// <summary>Lists the Workspaces the caller can see.</summary>
    public const string ListWorkspaces = "Lists the Workspaces the caller can see, with optional `where` and `orderBy` (FR-55) and paging (FR-41); any Member (FR-17).";

    /// <summary>Updates a Workspace name or description.</summary>
    public const string UpdateWorkspace = "Updates a Workspace's name or description (FR-18); WorkspaceOwner of that Workspace.";

    /// <summary>Lists the Lists in a Workspace.</summary>
    public const string ListLists = "Lists a Workspace's Lists, with optional `where` and `orderBy` (FR-55) and paging (FR-41); WorkspaceOwner, WorkspaceWrite or WorkspaceRead of that Workspace (FR-24).";

    /// <summary>Creates a List.</summary>
    public const string CreateList = "Creates a List with a name, optional description and a valid EasyJsonSchema schema (FR-22); WorkspaceOwner of that Workspace.";

    /// <summary>Updates a List name or description.</summary>
    public const string UpdateList = "Updates a List's name or description (FR-40); WorkspaceOwner of that Workspace. The schema is changed only with `update_schema` (FR-26).";

    /// <summary>Deletes a List.</summary>
    public const string DeleteList = "Deletes a List and hides it at once; its Items are removed in the background (FR-23, FR-51); WorkspaceOwner of that Workspace.";

    /// <summary>Reads a List schema.</summary>
    public const string GetSchema = "Returns a List's current schema and `schemaVersion` (FR-25); WorkspaceOwner, WorkspaceWrite or WorkspaceRead of that Workspace.";

    /// <summary>Replaces a List schema.</summary>
    public const string UpdateSchema = "Replaces a List's schema and increments `schemaVersion` by 1 if valid (FR-26); WorkspaceOwner of that Workspace. Existing Items keep their stored version.";

    /// <summary>Queries Items.</summary>
    public const string QueryItems = "Lists Items of one List; `shape` is required (`full` or `ids`); optional `schemaVersion`, `where` and `orderBy` (FR-29), paged (FR-41); WorkspaceOwner, WorkspaceWrite or WorkspaceRead of that Workspace.";

    /// <summary>Reads one Item.</summary>
    public const string GetItem = "Returns one Item's stored JSON and `schemaVersion` (FR-28); WorkspaceOwner, WorkspaceWrite or WorkspaceRead of that Workspace.";

    /// <summary>Creates or updates an Item.</summary>
    public const string UpsertItem = "Creates or updates an Item after EasyJsonSchema data validation against the List's current schema (FR-27); WorkspaceOwner or WorkspaceWrite of that Workspace. `itemId` is optional; the server generates a GUID when it is omitted (FR-45).";

    /// <summary>Deletes one Item.</summary>
    public const string DeleteItem = "Deletes one Item (FR-30); WorkspaceOwner or WorkspaceWrite of that Workspace.";

    /// <summary>Adds an email to a Workspace.</summary>
    public const string InviteMember = "Adds an email to a Workspace with a role, creating the Member if new, and sends the invitation or Workspace access email (FR-19, FR-43); WorkspaceOwner of that Workspace or TenantOwner.";

    /// <summary>Changes a Workspace role.</summary>
    public const string ChangeWorkspaceRole = "Changes a Member's role on a Workspace (FR-21, FR-10, FR-13); WorkspaceOwner of that Workspace or TenantOwner. A WorkspaceOwner cannot change their own WorkspaceOwner role.";

    /// <summary>Removes a Workspace role.</summary>
    public const string RevokeWorkspaceAccess = "Removes a Member's role on a Workspace (FR-20, FR-13); WorkspaceOwner of that Workspace or TenantOwner. A WorkspaceOwner cannot remove their own access.";

    /// <summary>Lists Workspace Members.</summary>
    public const string ListWorkspaceMembers = "Lists Members who hold a role on a Workspace, with optional `where` and `orderBy` (FR-55) and paging (FR-41); WorkspaceOwner of that Workspace or TenantOwner (FR-39).";

    /// <summary>Assigns or removes TenantOwner.</summary>
    public const string SetTenantOwner = "Assigns or removes another Member's TenantOwner flag (FR-9); TenantOwner, not self.";

    /// <summary>Lists Tenant Members.</summary>
    public const string QueryMembers = "Lists the Tenant's Members, with optional `where` and `orderBy` (FR-55) and paging (FR-41); TenantOwner, or a WorkspaceOwner of at least one Workspace (FR-8).";

    /// <summary>Runs one all-or-nothing batch.</summary>
    public const string Batch = "Runs 1 to 100 upsert, delete or get operations of one entity type, all-or-nothing, in one partition (one `workspaceId` for `list`; one `workspaceId` and `listId` for `item`; no `members` on Workspace create) (FR-56). Who may call each operation is the same as the matching single call: TenantOwner for `member` operations and for Workspace create or delete; WorkspaceOwner for Workspace update and for List writes; WorkspaceOwner, WorkspaceWrite or WorkspaceRead for Workspace, List or Item reads; WorkspaceOwner or WorkspaceWrite for Item writes.";
}
