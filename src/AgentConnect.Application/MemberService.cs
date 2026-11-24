using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Tenant members, tokens, and workspace membership.
/// </summary>
public sealed class MemberService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemberService"/> class.
    /// </summary>
    /// <param name="members">The members.</param>
    /// <param name="workspaces">The workspaces.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="email">The email sender.</param>
    /// <param name="audit">The audit recorder.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public MemberService(
        IMemberRepository members,
        IWorkspaceRepository workspaces,
        TokenService tokens,
        IEmailSender email,
        IAuditRecorder audit,
        IClock clock,
        AgentConnectOptions options,
        ILogger<MemberService> logger)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _members = members;
        _workspaces = workspaces;
        _tokens = tokens;
        _email = email;
        _audit = audit;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>Adds a member. No email is sent. FR-2.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The member.</returns>
    public async Task<OperationResult<MemberResult>> AddAsync(Caller caller, string? email, CancellationToken cancellationToken)
    {
        var denied = Permissions.AuthorizeTenant(caller, PlatformAction.AddTenantMember, null);
        if (denied is not null)
        {
            return OperationResult<MemberResult>.Fail(denied);
        }

        if (email is null)
        {
            return OperationResult<MemberResult>.Fail(PlatformError.BadRequest("email: required"));
        }

        if (!EmailRules.TryNormalize(email, out var normalized, out var error))
        {
            return OperationResult<MemberResult>.Fail(PlatformError.BadRequest(error));
        }

        if (await _members.FindByEmailAsync(caller.Tenant.Id, normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            return OperationResult<MemberResult>.Fail(PlatformError.Conflict("email: already exists"));
        }

        var member = CreateMember(caller.Tenant.Id, normalized, false);
        var duplicate = await Unique.CreateAsync(ct => _members.CreateAsync(member, ct), "email: already exists", cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return OperationResult<MemberResult>.Fail(duplicate);
        }
        Audit(caller.Member.Id, AuditActions.MemberAdded, ("tenantId", caller.Tenant.Id), ("memberId", member.Id));
        return OperationResult<MemberResult>.Ok(ToResult(member));
    }

    /// <summary>Sends or resends the invitation. The token does not change. FR-3.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the email was sent.</returns>
    public async Task<OperationResult<EmailSentResult>> SendInvitationAsync(Caller caller, string? email, CancellationToken cancellationToken)
    {
        var member = await RequireTenantMemberAsync(caller, PlatformAction.SendInvitation, email, cancellationToken).ConfigureAwait(false);
        if (member.Error is not null)
        {
            return OperationResult<EmailSentResult>.Fail(member.Error);
        }

        var sent = await SendInvitationMailAsync(caller.Member.Id, caller.Member.Email, member.Value!, caller.Tenant, null, null, cancellationToken).ConfigureAwait(false);
        return OperationResult<EmailSentResult>.Ok(new EmailSentResult(sent));
    }

    /// <summary>Deletes a member other than the caller. FR-4.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    public async Task<OperationResult<DeletedResult>> DeleteAsync(Caller caller, string? email, CancellationToken cancellationToken)
    {
        var member = await RequireTenantMemberAsync(caller, PlatformAction.DeleteTenantMember, email, cancellationToken).ConfigureAwait(false);
        if (member.Error is not null)
        {
            return OperationResult<DeletedResult>.Fail(member.Error);
        }

        await _members.DeleteAsync(caller.Tenant.Id, member.Value!.Id, cancellationToken).ConfigureAwait(false);
        Audit(caller.Member.Id, AuditActions.MemberDeleted, ("tenantId", caller.Tenant.Id), ("memberId", member.Value.Id));
        return OperationResult<DeletedResult>.Ok(new DeletedResult(true));
    }

    /// <summary>Regenerates a token. No email is sent. FR-6.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>When the new token was issued.</returns>
    public async Task<OperationResult<TokenRegeneratedResult>> RegenerateAsync(Caller caller, string? email, CancellationToken cancellationToken)
    {
        var member = await RequireTenantMemberAsync(caller, PlatformAction.RegenerateToken, email, cancellationToken).ConfigureAwait(false);
        if (member.Error is not null)
        {
            return OperationResult<TokenRegeneratedResult>.Fail(member.Error);
        }

        var issued = _tokens.Issue(member.Value!.TenantId, member.Value.Id);
        var stored = await UpdateMemberAsync(member.Value.TenantId, member.Value.Id, current =>
        {
            current.TokenHash = issued.Hash;
            current.TokenEncrypted = issued.Encrypted;
            current.LastTokenIssued = PlatformSupport.After(_clock.UtcNow, current.LastTokenSent);
        }, cancellationToken).ConfigureAwait(false);
        Audit(caller.Member.Id, AuditActions.TokenRegenerated, ("tenantId", caller.Tenant.Id), ("memberId", stored.Id));
        return OperationResult<TokenRegeneratedResult>.Ok(new TokenRegeneratedResult(stored.LastTokenIssued));
    }

    /// <summary>Emails the stored token. FR-7.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the email was sent.</returns>
    public async Task<OperationResult<EmailSentResult>> SendTokenAsync(Caller caller, string? email, CancellationToken cancellationToken)
    {
        var member = await RequireTenantMemberAsync(caller, PlatformAction.SendToken, email, cancellationToken).ConfigureAwait(false);
        if (member.Error is not null)
        {
            return OperationResult<EmailSentResult>.Fail(member.Error);
        }

        if (!TryOpenToken(member.Value!, out var token))
        {
            return OperationResult<EmailSentResult>.Ok(new EmailSentResult(false));
        }

        var previous = member.Value!.LastTokenSent is DateTimeOffset sent && member.Value.LastTokenIssued > sent;
        var message = EmailTemplates.Token(member.Value.Email, caller.Tenant.Name, token, _options.McpServerUrl, _options.RestApiUrl, previous);
        var sentOk = await PlatformSupport.TrySendAsync(_email, _logger, message, cancellationToken).ConfigureAwait(false);
        if (!sentOk)
        {
            return OperationResult<EmailSentResult>.Ok(new EmailSentResult(false));
        }

        var stored = await UpdateMemberAsync(member.Value.TenantId, member.Value.Id, current =>
        {
            current.LastTokenSent = PlatformSupport.After(_clock.UtcNow, current.LastTokenIssued);
        }, cancellationToken).ConfigureAwait(false);
        Audit(caller.Member.Id, AuditActions.TokenSent, ("tenantId", caller.Tenant.Id), ("memberId", stored.Id));
        return OperationResult<EmailSentResult>.Ok(new EmailSentResult(true));
    }

    /// <summary>Lists the tenant's members. FR-8.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="pageSize">The requested page size, when specified.</param>
    /// <param name="pageSizeSpecified">Whether a page size was sent.</param>
    /// <param name="continuation">The continuation token, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<OperationResult<MemberPageResult>> ListAsync(Caller caller, int? pageSize, bool pageSizeSpecified, string? continuation, CancellationToken cancellationToken)
    {
        var denied = Permissions.AuthorizeTenant(caller, PlatformAction.ListTenantMembers, null);
        if (denied is not null)
        {
            return OperationResult<MemberPageResult>.Fail(denied);
        }

        var fingerprint = "members|" + caller.Tenant.Id;
        var window = PlatformSupport.OpenPage(caller.Tenant, pageSize, pageSizeSpecified, continuation, fingerprint);
        if (window.Error is not null)
        {
            return OperationResult<MemberPageResult>.Fail(window.Error);
        }

        var page = await _members.ListAsync(caller.Tenant.Id, window.Value!.PageSize, window.Value.Cursor, cancellationToken).ConfigureAwait(false);
        return OperationResult<MemberPageResult>.Ok(new MemberPageResult(
            page.Items.Select(ToResult).ToArray(),
            PlatformSupport.Seal(fingerprint, window.Value.PageSize, page.Continuation)));
    }

    /// <summary>Assigns or removes the TenantOwner flag. FR-9.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="isTenantOwner">The new flag.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The member.</returns>
    public async Task<OperationResult<MemberResult>> SetTenantOwnerAsync(Caller caller, string? email, bool isTenantOwner, CancellationToken cancellationToken)
    {
        var denied = Permissions.AuthorizeTenant(caller, PlatformAction.SetTenantOwner, null);
        if (denied is not null)
        {
            return OperationResult<MemberResult>.Fail(denied);
        }

        if (email is null)
        {
            return OperationResult<MemberResult>.Fail(PlatformError.BadRequest("email: required"));
        }

        if (!EmailRules.TryNormalize(email, out var normalized, out var emailError))
        {
            return OperationResult<MemberResult>.Fail(PlatformError.BadRequest(emailError));
        }

        var stored = await _members.FindByEmailAsync(caller.Tenant.Id, normalized, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return OperationResult<MemberResult>.Fail(PlatformError.NotFound);
        }

        if (stored.Id == caller.Member.Id)
        {
            return OperationResult<MemberResult>.Fail(PlatformError.NotAllowedOnYourself);
        }
        if (stored.IsTenantOwner != isTenantOwner)
        {
            stored = await UpdateMemberAsync(stored.TenantId, stored.Id, current =>
            {
                current.IsTenantOwner = isTenantOwner;
            }, cancellationToken).ConfigureAwait(false);
            Audit(caller.Member.Id, AuditActions.TenantOwnerChanged, ("tenantId", caller.Tenant.Id), ("memberId", stored.Id));
        }

        return OperationResult<MemberResult>.Ok(ToResult(stored));
    }

    /// <summary>Adds an email to a workspace. FR-19.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="role">The raw API role.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The assignment.</returns>
    public async Task<OperationResult<RoleAssignmentResult>> AddToWorkspaceAsync(Caller caller, string workspaceId, string? email, string? role, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var workspace = await LiveWorkspaceAsync(caller, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace.Error is not null)
        {
            return OperationResult<RoleAssignmentResult>.Fail(workspace.Error);
        }

        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.AddWorkspaceMember, null, null, null);
        if (denied is not null)
        {
            return OperationResult<RoleAssignmentResult>.Fail(denied);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<RoleAssignmentResult>.Fail(blocked);
        }

        if (email is null)
        {
            return OperationResult<RoleAssignmentResult>.Fail(PlatformError.BadRequest("email: required"));
        }

        if (!EmailRules.TryNormalize(email, out var normalized, out var emailError))
        {
            return OperationResult<RoleAssignmentResult>.Fail(PlatformError.BadRequest(emailError));
        }

        if (role is null)
        {
            return OperationResult<RoleAssignmentResult>.Fail(PlatformError.BadRequest("role: required"));
        }

        if (!StoredRoles.TryParse(role, out var stored))
        {
            return OperationResult<RoleAssignmentResult>.Fail(PlatformError.BadRequest("role: invalid"));
        }

        var existing = await _members.FindByEmailAsync(caller.Tenant.Id, normalized, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.WorkspaceRoles.TryGetValue(workspaceId, out var currentRole))
        {
            var self = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.ChangeWorkspaceRole, existing.Id, currentRole, stored);
            if (self is not null)
            {
                return OperationResult<RoleAssignmentResult>.Fail(self);
            }
        }

        return await AssignAsync(caller, workspace.Value!, normalized, stored, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes a workspace role. FR-21.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="role">The raw API role.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The assignment. No email is sent.</returns>
    public async Task<OperationResult<WorkspaceMemberView>> ChangeRoleAsync(Caller caller, string workspaceId, string? email, string? role, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var workspace = await LiveWorkspaceAsync(caller, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace.Error is not null)
        {
            return OperationResult<WorkspaceMemberView>.Fail(workspace.Error);
        }

        var located = await LocateWorkspaceMemberAsync(caller, workspaceId, email, PlatformAction.ChangeWorkspaceRole, cancellationToken).ConfigureAwait(false);
        if (located.Error is not null)
        {
            return OperationResult<WorkspaceMemberView>.Fail(located.Error);
        }

        var member = located.Value!.Member;
        var current = located.Value.Role;
        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.ChangeWorkspaceRole, null, null, null);
        if (denied is not null)
        {
            return OperationResult<WorkspaceMemberView>.Fail(denied);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<WorkspaceMemberView>.Fail(blocked);
        }

        if (role is null)
        {
            return OperationResult<WorkspaceMemberView>.Fail(PlatformError.BadRequest("role: required"));
        }

        if (!StoredRoles.TryParse(role, out var stored))
        {
            return OperationResult<WorkspaceMemberView>.Fail(PlatformError.BadRequest("role: invalid"));
        }

        var self = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.ChangeWorkspaceRole, member.Id, current, stored);
        if (self is not null)
        {
            return OperationResult<WorkspaceMemberView>.Fail(self);
        }

        if (current != stored)
        {
            member = await UpdateMemberAsync(member.TenantId, member.Id, currentMember =>
            {
                currentMember.WorkspaceRoles[workspaceId] = stored;
            }, cancellationToken).ConfigureAwait(false);
            Audit(caller.Member.Id, AuditActions.WorkspaceRoleChanged, ("tenantId", caller.Tenant.Id), ("workspaceId", workspaceId), ("memberId", member.Id));
        }

        return OperationResult<WorkspaceMemberView>.Ok(new WorkspaceMemberView(member.Email, StoredRoles.ToApi(stored)));
    }

    /// <summary>Removes a workspace role. FR-20.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The confirmation.</returns>
    public async Task<OperationResult<DeletedResult>> RemoveFromWorkspaceAsync(Caller caller, string workspaceId, string? email, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var workspace = await LiveWorkspaceAsync(caller, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace.Error is not null)
        {
            return OperationResult<DeletedResult>.Fail(workspace.Error);
        }

        var located = await LocateWorkspaceMemberAsync(caller, workspaceId, email, PlatformAction.RemoveWorkspaceMember, cancellationToken).ConfigureAwait(false);
        if (located.Error is not null)
        {
            return OperationResult<DeletedResult>.Fail(located.Error);
        }

        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.RemoveWorkspaceMember, located.Value!.Member.Id, located.Value.Role, null);
        if (denied is not null)
        {
            return OperationResult<DeletedResult>.Fail(denied);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<DeletedResult>.Fail(blocked);
        }

        var removed = await UpdateMemberAsync(located.Value.Member.TenantId, located.Value.Member.Id, currentMember =>
        {
            currentMember.WorkspaceRoles.Remove(workspaceId);
        }, cancellationToken).ConfigureAwait(false);
        Audit(caller.Member.Id, AuditActions.WorkspaceRoleRemoved, ("tenantId", caller.Tenant.Id), ("workspaceId", workspaceId), ("memberId", removed.Id));
        return OperationResult<DeletedResult>.Ok(new DeletedResult(true));
    }

    /// <summary>Lists members who hold a role on a workspace. FR-39.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The requested page size, when specified.</param>
    /// <param name="pageSizeSpecified">Whether a page size was sent.</param>
    /// <param name="continuation">The continuation token, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The page.</returns>
    public async Task<OperationResult<WorkspaceMemberPageResult>> ListWorkspaceMembersAsync(Caller caller, string workspaceId, int? pageSize, bool pageSizeSpecified, string? continuation, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var workspace = await LiveWorkspaceAsync(caller, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace.Error is not null)
        {
            return OperationResult<WorkspaceMemberPageResult>.Fail(workspace.Error);
        }

        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.ListWorkspaceMembers, null, null, null);
        if (denied is not null)
        {
            return OperationResult<WorkspaceMemberPageResult>.Fail(denied);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<WorkspaceMemberPageResult>.Fail(blocked);
        }

        var fingerprint = "workspace-members|" + caller.Tenant.Id + "|" + workspaceId;
        var window = PlatformSupport.OpenPage(caller.Tenant, pageSize, pageSizeSpecified, continuation, fingerprint);
        if (window.Error is not null)
        {
            return OperationResult<WorkspaceMemberPageResult>.Fail(window.Error);
        }

        var page = await _members.ListByWorkspaceAsync(caller.Tenant.Id, workspaceId, window.Value!.PageSize, window.Value.Cursor, cancellationToken).ConfigureAwait(false);
        var views = page.Items.Select(member => new WorkspaceMemberView(member.Email, StoredRoles.ToApi(member.WorkspaceRoles[workspaceId]))).ToArray();
        return OperationResult<WorkspaceMemberPageResult>.Ok(new WorkspaceMemberPageResult(views, PlatformSupport.Seal(fingerprint, window.Value.PageSize, page.Continuation)));
    }

    /// <summary>Assigns a role while creating a workspace. The caller has already been authorized.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspace">The new workspace.</param>
    /// <param name="email">The normalized email.</param>
    /// <param name="storedRole">The stored role.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The assignment.</returns>
    public Task<OperationResult<RoleAssignmentResult>> AssignNewWorkspaceAsync(Caller caller, WorkspaceDocument workspace, string email, string storedRole, CancellationToken cancellationToken)
    {
        return AssignAsync(caller, workspace, email, storedRole, cancellationToken);
    }

    /// <summary>Checks that the caller may perform a workspace action before the body is read. FR-42.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="action">The action.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 404 or 403, or null when the body may be read.</returns>
    public async Task<PlatformError?> AccessAsync(Caller caller, string workspaceId, PlatformAction action, CancellationToken cancellationToken)
    {
        var workspace = await LiveWorkspaceAsync(caller, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace.Error is not null)
        {
            return workspace.Error;
        }

        return Permissions.AuthorizeWorkspace(caller, workspaceId, action, null, null, null);
    }

    #region private

    private readonly IMemberRepository _members;
    private readonly IWorkspaceRepository _workspaces;
    private readonly TokenService _tokens;
    private readonly IEmailSender _email;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly AgentConnectOptions _options;
    private readonly ILogger<MemberService> _logger;

    private async Task<OperationResult<RoleAssignmentResult>> AssignAsync(Caller caller, WorkspaceDocument workspace, string email, string storedRole, CancellationToken cancellationToken)
    {
        var existing = await _members.FindByEmailAsync(caller.Tenant.Id, email, cancellationToken).ConfigureAwait(false);
        MemberDocument member;
        if (existing is null)
        {
            member = CreateMember(caller.Tenant.Id, email, false);
            member.WorkspaceRoles[workspace.Id] = storedRole;
            var duplicate = await Unique.CreateAsync(ct => _members.CreateAsync(member, ct), "email: already exists", cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return OperationResult<RoleAssignmentResult>.Fail(duplicate);
            }
            Audit(caller.Member.Id, AuditActions.MemberAdded, ("tenantId", caller.Tenant.Id), ("memberId", member.Id));
        }
        else
        {
            member = await UpdateMemberAsync(existing.TenantId, existing.Id, current =>
            {
                current.WorkspaceRoles[workspace.Id] = storedRole;
            }, cancellationToken).ConfigureAwait(false);
        }

        Audit(caller.Member.Id, AuditActions.WorkspaceRoleAssigned, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id), ("memberId", member.Id));
        var sent = !member.InvitationSent
            ? await SendInvitationMailAsync(caller.Member.Id, caller.Member.Email, member, caller.Tenant, workspace.Name, StoredRoles.ToApi(storedRole), cancellationToken).ConfigureAwait(false)
            : await SendAccessMailAsync(caller, member, workspace, storedRole, cancellationToken).ConfigureAwait(false);
        return OperationResult<RoleAssignmentResult>.Ok(new RoleAssignmentResult(member.Email, StoredRoles.ToApi(storedRole), sent));
    }

    private async Task<bool> SendInvitationMailAsync(string actorId, string invitedBy, MemberDocument member, TenantDocument tenant, string? workspaceName, string? role, CancellationToken cancellationToken)
    {
        if (!TryOpenToken(member, out var token))
        {
            return false;
        }

        var message = EmailTemplates.Invitation(member.Email, tenant.Name, invitedBy, workspaceName, role, token, _options.McpServerUrl, _options.RestApiUrl);
        if (!await PlatformSupport.TrySendAsync(_email, _logger, message, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await UpdateMemberAsync(member.TenantId, member.Id, current =>
        {
            current.InvitationSent = true;
            current.LastTokenSent = PlatformSupport.After(_clock.UtcNow, current.LastTokenIssued);
        }, cancellationToken).ConfigureAwait(false);
        Audit(actorId, AuditActions.InvitationSent, ("tenantId", tenant.Id), ("memberId", member.Id));
        return true;
    }

    private async Task<bool> SendAccessMailAsync(Caller caller, MemberDocument member, WorkspaceDocument workspace, string storedRole, CancellationToken cancellationToken)
    {
        var message = EmailTemplates.WorkspaceAccess(member.Email, caller.Tenant.Name, workspace.Name, StoredRoles.ToApi(storedRole), caller.Member.Email);
        return await PlatformSupport.TrySendAsync(_email, _logger, message, cancellationToken).ConfigureAwait(false);
    }

    private bool TryOpenToken(MemberDocument member, out string token)
    {
        try
        {
            token = _tokens.Decrypt(member.TokenEncrypted);
            return true;
        }
        catch (Exception exception)
        {
            token = "";
            _logger.LogWarning("Stored token could not be opened ({ExceptionType}).", exception.GetType().Name);
            return false;
        }
    }

    private MemberDocument CreateMember(string tenantId, string email, bool isTenantOwner)
    {
        var id = PlatformSupport.NewId();
        var issued = _tokens.Issue(tenantId, id);
        var now = _clock.UtcNow;
        return new MemberDocument
        {
            Id = id,
            TenantId = tenantId,
            Email = email,
            TokenHash = issued.Hash,
            TokenEncrypted = issued.Encrypted,
            IsTenantOwner = isTenantOwner,
            Created = now,
            LastTokenIssued = now,
        };
    }

    private async Task<OperationResult<MemberDocument>> RequireTenantMemberAsync(Caller caller, PlatformAction action, string? email, CancellationToken cancellationToken)
    {
        var emailOk = EmailRules.TryNormalize(email, out var normalized, out var emailError);
        MemberDocument? member = null;
        if (emailOk)
        {
            member = await _members.FindByEmailAsync(caller.Tenant.Id, normalized, cancellationToken).ConfigureAwait(false);
        }

        if (member is null)
        {
            if (!emailOk && Permissions.AuthorizeTenant(caller, action, null) is PlatformError denied)
            {
                return OperationResult<MemberDocument>.Fail(denied);
            }

            if (!emailOk)
            {
                return OperationResult<MemberDocument>.Fail(PlatformError.BadRequest(emailError));
            }

            return OperationResult<MemberDocument>.Fail(PlatformError.NotFound);
        }

        var error = Permissions.AuthorizeTenant(caller, action, member.Id);
        if (error is not null)
        {
            return OperationResult<MemberDocument>.Fail(error);
        }

        return OperationResult<MemberDocument>.Ok(member);
    }

    private Task<MemberDocument> UpdateMemberAsync(string tenantId, string memberId, Action<MemberDocument> mutate, CancellationToken cancellationToken)
    {
        return DocumentUpdates.UpdateAsync(
            ct => _members.GetAsync(tenantId, memberId, ct),
            (current, ct) => _members.ReplaceAsync(current, ct),
            mutate,
            _options.ConcurrencyRetries,
            cancellationToken);
    }

    private async Task<OperationResult<WorkspaceMemberLocation>> LocateWorkspaceMemberAsync(Caller caller, string workspaceId, string? email, PlatformAction action, CancellationToken cancellationToken)
    {
        var emailOk = EmailRules.TryNormalize(email, out var normalized, out var emailError);
        MemberDocument? member = null;
        if (emailOk)
        {
            member = await _members.FindByEmailAsync(caller.Tenant.Id, normalized, cancellationToken).ConfigureAwait(false);
        }

        var role = member is not null && member.WorkspaceRoles.TryGetValue(workspaceId, out var found) ? found : null;
        if (member is null || role is null)
        {
            if (!emailOk && Permissions.AuthorizeWorkspace(caller, workspaceId, action, null, null, null) is PlatformError denied)
            {
                return OperationResult<WorkspaceMemberLocation>.Fail(denied);
            }

            if (!emailOk)
            {
                return OperationResult<WorkspaceMemberLocation>.Fail(PlatformError.BadRequest(emailError));
            }

            return OperationResult<WorkspaceMemberLocation>.Fail(PlatformError.NotFound);
        }

        return OperationResult<WorkspaceMemberLocation>.Ok(new WorkspaceMemberLocation(member, role));
    }

    private async Task<OperationResult<WorkspaceDocument>> LiveWorkspaceAsync(Caller caller, string workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaces.GetAsync(caller.Tenant.Id, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.DeletedAt is not null)
        {
            return OperationResult<WorkspaceDocument>.Fail(PlatformError.NotFound);
        }

        if (!caller.CanSee(workspace.Id))
        {
            return OperationResult<WorkspaceDocument>.Fail(PlatformError.NotFound);
        }

        return OperationResult<WorkspaceDocument>.Ok(workspace);
    }

    private void Audit(string actor, string action, params (string Key, string Value)[] targets)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in targets)
        {
            map[key] = value;
        }

        _audit.Record(new AuditEntry(actor, action, _clock.UtcNow, map));
    }

    private sealed record WorkspaceMemberLocation(MemberDocument Member, string Role);

    private static MemberResult ToResult(MemberDocument member)
    {
        return new MemberResult(member.Id, member.Email, member.IsTenantOwner, member.InvitationSent);
    }

    #endregion
}
