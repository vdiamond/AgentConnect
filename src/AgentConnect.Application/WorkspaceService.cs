using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Workspaces. Membership changes go through <see cref="MemberService"/>.
/// </summary>
public sealed class WorkspaceService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceService"/> class.
    /// </summary>
    /// <param name="workspaces">The workspaces.</param>
    /// <param name="members">The membership service.</param>
    /// <param name="audit">The audit recorder.</param>
    /// <param name="clock">The clock.</param>
    public WorkspaceService(IWorkspaceRepository workspaces, MemberService members, IAuditRecorder audit, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        _workspaces = workspaces;
        _members = members;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Creates a workspace and optional initial roles. FR-15.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="name">The raw name.</param>
    /// <param name="description">The raw description.</param>
    /// <param name="descriptionSpecified">Whether a description was sent.</param>
    /// <param name="initial">The initial assignments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workspace.</returns>
    public async Task<OperationResult<WorkspaceCreatedResult>> CreateAsync(
        Caller caller,
        string? name,
        string? description,
        bool descriptionSpecified,
        IReadOnlyList<InitialRole> initial,
        CancellationToken cancellationToken)
    {
        var denied = Permissions.AuthorizeTenant(caller, PlatformAction.CreateWorkspace, null);
        if (denied is not null)
        {
            return OperationResult<WorkspaceCreatedResult>.Fail(denied);
        }

        if (!NameRules.TryNormalizeName(name, out var workspaceName, out var nameError))
        {
            return OperationResult<WorkspaceCreatedResult>.Fail(PlatformError.BadRequest(nameError));
        }

        string? workspaceDescription = null;
        if (descriptionSpecified && !NameRules.TryNormalizeDescription(description, out workspaceDescription, out var descriptionError))
        {
            return OperationResult<WorkspaceCreatedResult>.Fail(PlatformError.BadRequest(descriptionError));
        }

        var assignments = new List<(string Email, string Role)>();
        foreach (var entry in initial)
        {
            if (!EmailRules.TryNormalize(entry.Email, out var email, out var emailError))
            {
                return OperationResult<WorkspaceCreatedResult>.Fail(PlatformError.BadRequest(emailError));
            }

            if (!StoredRoles.TryParse(entry.Role, out var stored))
            {
                return OperationResult<WorkspaceCreatedResult>.Fail(PlatformError.BadRequest("role: invalid"));
            }

            assignments.Add((email, stored));
        }

        if (await _workspaces.NameExistsAsync(caller.Tenant.Id, workspaceName, null, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<WorkspaceCreatedResult>.Fail(PlatformError.Conflict("name: already exists"));
        }

        var now = _clock.UtcNow;
        var workspace = new WorkspaceDocument
        {
            Id = PlatformSupport.NewId(),
            TenantId = caller.Tenant.Id,
            Name = workspaceName,
            Description = workspaceDescription,
            Created = now,
            Updated = now,
        };
        var duplicate = await Unique.CreateAsync(ct => _workspaces.CreateAsync(workspace, ct), "name: already exists", cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return OperationResult<WorkspaceCreatedResult>.Fail(duplicate);
        }
        Record(caller.Member.Id, AuditActions.WorkspaceCreated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id));
        var results = new List<RoleAssignmentResult>();
        foreach (var (email, role) in assignments)
        {
            var assigned = await _members.AssignNewWorkspaceAsync(caller, workspace, email, role, cancellationToken).ConfigureAwait(false);
            if (assigned.Error is not null)
            {
                return OperationResult<WorkspaceCreatedResult>.Fail(assigned.Error);
            }

            results.Add(assigned.Value!);
        }

        return OperationResult<WorkspaceCreatedResult>.Ok(new WorkspaceCreatedResult(workspace.Id, workspace.Name, workspace.Description, results));
    }

    /// <summary>Marks a workspace deleted. FR-16.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    public async Task<OperationResult<DeletedResult>> DeleteAsync(Caller caller, string workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaces.GetAsync(caller.Tenant.Id, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.DeletedAt is not null)
        {
            return OperationResult<DeletedResult>.Fail(PlatformError.NotFound);
        }

        var denied = Permissions.AuthorizeTenant(caller, PlatformAction.DeleteWorkspace, null);
        if (denied is not null)
        {
            return caller.CanSee(workspaceId)
                ? OperationResult<DeletedResult>.Fail(denied)
                : OperationResult<DeletedResult>.Fail(PlatformError.NotFound);
        }

        await DocumentUpdates.UpdateAsync(
            ct => _workspaces.GetAsync(caller.Tenant.Id, workspaceId, ct),
            (current, ct) => _workspaces.ReplaceAsync(current, ct),
            current =>
            {
                current.DeletedAt = _clock.UtcNow;
                current.Updated = current.DeletedAt.Value;
            },
            cancellationToken).ConfigureAwait(false);
        Record(caller.Member.Id, AuditActions.WorkspaceDeleted, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id));
        return OperationResult<DeletedResult>.Ok(new DeletedResult(true));
    }

    /// <summary>Lists the workspaces the caller can see. FR-17.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="pageSize">The requested page size, when specified.</param>
    /// <param name="pageSizeSpecified">Whether a page size was sent.</param>
    /// <param name="continuation">The continuation token, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<OperationResult<WorkspacePageResult>> ListAsync(Caller caller, int? pageSize, bool pageSizeSpecified, string? continuation, CancellationToken cancellationToken)
    {
        var denied = Permissions.AuthorizeTenant(caller, PlatformAction.ListWorkspaces, null);
        if (denied is not null)
        {
            return OperationResult<WorkspacePageResult>.Fail(denied);
        }

        IReadOnlyCollection<string>? visible = caller.IsTenantOwner ? null : caller.Member.WorkspaceRoles.Keys.ToArray();
        var scope = caller.IsTenantOwner
            ? "*"
            : string.Join(',', caller.Member.WorkspaceRoles.Keys.Order(StringComparer.Ordinal));
        var fingerprint = "workspaces|" + caller.Tenant.Id + "|" + caller.Member.Id + "|" + scope;
        var window = PlatformSupport.OpenPage(caller.Tenant, pageSize, pageSizeSpecified, continuation, fingerprint);
        if (window.Error is not null)
        {
            return OperationResult<WorkspacePageResult>.Fail(window.Error);
        }

        var page = await _workspaces.ListVisibleAsync(caller.Tenant.Id, visible, window.Value!.PageSize, window.Value.Cursor, cancellationToken).ConfigureAwait(false);
        var views = page.Items.Select(workspace => new WorkspaceView(
            workspace.Id,
            workspace.Name,
            workspace.Description,
            caller.RoleOn(workspace.Id) is string role ? StoredRoles.ToApi(role) : null)).ToArray();
        return OperationResult<WorkspacePageResult>.Ok(new WorkspacePageResult(views, PlatformSupport.Seal(fingerprint, window.Value.PageSize, page.Continuation)));
    }

    /// <summary>Updates general data. FR-18.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The name field.</param>
    /// <param name="description">The description field.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workspace.</returns>
    public async Task<OperationResult<WorkspaceUpdatedResult>> UpdateAsync(Caller caller, string workspaceId, OptionalText name, OptionalText description, CancellationToken cancellationToken)
    {
        var workspace = await _workspaces.GetAsync(caller.Tenant.Id, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.DeletedAt is not null || !caller.CanSee(workspaceId))
        {
            return OperationResult<WorkspaceUpdatedResult>.Fail(PlatformError.NotFound);
        }

        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.UpdateWorkspace, null, null, null);
        if (denied is not null)
        {
            return OperationResult<WorkspaceUpdatedResult>.Fail(denied);
        }

        var nextName = workspace.Name;
        if (name.Specified && !NameRules.TryNormalizeName(name.Value, out nextName, out var nameError))
        {
            return OperationResult<WorkspaceUpdatedResult>.Fail(PlatformError.BadRequest(nameError));
        }

        var nextDescription = workspace.Description;
        if (description.Specified && !NameRules.TryNormalizeDescription(description.Value, out nextDescription, out var descriptionError))
        {
            return OperationResult<WorkspaceUpdatedResult>.Fail(PlatformError.BadRequest(descriptionError));
        }

        if (!NameRules.SameName(nextName, workspace.Name)
            && await _workspaces.NameExistsAsync(caller.Tenant.Id, nextName, workspace.Id, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<WorkspaceUpdatedResult>.Fail(PlatformError.Conflict("name: already exists"));
        }

        if (nextName != workspace.Name || nextDescription != workspace.Description)
        {
            try
            {
                workspace = await DocumentUpdates.UpdateAsync(
                    ct => _workspaces.GetAsync(caller.Tenant.Id, workspaceId, ct),
                    (current, ct) => _workspaces.ReplaceAsync(current, ct),
                    current =>
                    {
                        current.Name = nextName;
                        current.Description = nextDescription;
                        current.Updated = _clock.UtcNow;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DuplicateValueException)
            {
                return OperationResult<WorkspaceUpdatedResult>.Fail(PlatformError.Conflict("name: already exists"));
            }

            Record(caller.Member.Id, AuditActions.WorkspaceUpdated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id));
        }

        return OperationResult<WorkspaceUpdatedResult>.Ok(new WorkspaceUpdatedResult(workspace.Id, workspace.Name, workspace.Description));
    }

    /// <summary>Checks that the caller may update a workspace before the body is read. FR-42.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 404 or 403, or null when the body may be read.</returns>
    public async Task<PlatformError?> AccessAsync(Caller caller, string workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaces.GetAsync(caller.Tenant.Id, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.DeletedAt is not null || !caller.CanSee(workspaceId))
        {
            return PlatformError.NotFound;
        }

        return Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.UpdateWorkspace, null, null, null);
    }

    #region private

    private readonly IWorkspaceRepository _workspaces;
    private readonly MemberService _members;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;

    private void Record(string actor, string action, params (string Key, string Value)[] targets)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in targets)
        {
            map[key] = value;
        }

        _audit.Record(new AuditEntry(actor, action, _clock.UtcNow, map));
    }

    #endregion
}
